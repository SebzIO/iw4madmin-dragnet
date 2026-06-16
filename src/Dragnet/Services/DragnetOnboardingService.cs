using System.Text.Json;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Transport;
using Dragnet.Web;

namespace Dragnet.Services;

public sealed class DragnetOnboardingService
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetIdentityDocument _identity;
    private readonly DragnetIdentityService _identityService;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetUpdateService _updateService;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private DragnetOnboardingStatus? _cached;
    private DateTimeOffset _cachedAtUtc;

    public DragnetOnboardingService(
        DragnetConfiguration configuration,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        DragnetPeerStore peerStore,
        DragnetUpdateService updateService)
        : this(configuration, identity, identityService, peerStore, updateService, new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        })
    {
    }

    public DragnetOnboardingService(
        DragnetConfiguration configuration,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        DragnetPeerStore peerStore,
        DragnetUpdateService updateService,
        HttpClient httpClient)
    {
        _configuration = configuration;
        _identity = identity;
        _identityService = identityService;
        _peerStore = peerStore;
        _updateService = updateService;
        _httpClient = httpClient;
    }

    public async Task<DragnetOnboardingStatus> GetStatusAsync(CancellationToken token)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAtUtc < TimeSpan.FromMinutes(2))
        {
            return _cached;
        }

        await _checkLock.WaitAsync(token);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAtUtc < TimeSpan.FromMinutes(2))
            {
                return _cached;
            }

            var peers = await _peerStore.ListAsync(token);
            var endpoint = NormalizeEndpoint(_configuration.PublicEndpoint);
            var endpointUri = endpoint is null ? null : new Uri(endpoint);
            var endpointVerified = false;
            var endpointReachable = false;
            var endpointIdentityMatched = false;
            var endpointSignatureVerified = false;
            string? endpointError = null;

            if (endpointUri is not null)
            {
                try
                {
                    using var response = await _httpClient.GetAsync($"{endpoint}/health", token);
                    var body = await response.Content.ReadAsStringAsync(token);
                    if (!response.IsSuccessStatusCode)
                    {
                        endpointError = $"{(int)response.StatusCode} {response.ReasonPhrase}";
                    }
                    else
                    {
                        endpointReachable = true;
                        var health = JsonSerializer.Deserialize<DragnetHealthResponse>(body, DragnetJson.Options);
                        endpointIdentityMatched =
                            health is not null &&
                            string.Equals(health.OriginId, _identity.OriginId, StringComparison.OrdinalIgnoreCase);
                        endpointSignatureVerified =
                            health is not null &&
                            endpointIdentityMatched &&
                            !string.IsNullOrWhiteSpace(health.Signature) &&
                            _identityService.Verify(
                                health.OriginId,
                                health.PublicKeyPem,
                                health.GetSigningPayload(),
                                health.Signature);
                        endpointVerified = endpointIdentityMatched && endpointSignatureVerified;
                        if (!endpointIdentityMatched)
                        {
                            endpointError = "Health response did not match this Dragnet identity.";
                        }
                        else if (!endpointSignatureVerified)
                        {
                            endpointError = "Health response matched this identity but did not contain a valid signed proof.";
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    endpointError = ex.Message;
                }
            }

            var update = _updateService.Status;
            _cached = new DragnetOnboardingStatus(
                IdentityConfigured: !string.IsNullOrWhiteSpace(_configuration.OriginName) &&
                                    !_configuration.OriginName.Equals(
                                        "Unnamed Dragnet Origin",
                                        StringComparison.OrdinalIgnoreCase),
                EndpointConfigured: endpointUri is not null,
                EndpointUsesHttps: endpointUri?.Scheme == Uri.UriSchemeHttps ||
                                   (!_configuration.RequireHttps && endpointUri?.Scheme == Uri.UriSchemeHttp),
                EndpointReachable: endpointReachable,
                EndpointIdentityMatched: endpointIdentityMatched,
                EndpointSignatureVerified: endpointSignatureVerified,
                EndpointVerified: endpointVerified,
                EndpointError: endpointError,
                PeerConnected: peers.Any(peer =>
                    string.IsNullOrWhiteSpace(peer.LastError) &&
                    DateTimeOffset.UtcNow - peer.LastSeenUtc <= _configuration.PeerStaleAfter),
                SeedConfigured: _configuration.BootstrapPeers.Any(peer =>
                    peer.Enabled && !string.IsNullOrWhiteSpace(peer.Endpoint)),
                UpdateCurrent: !update.UpdateAvailable && !string.IsNullOrWhiteSpace(update.LatestVersion),
                RestartRequired: !string.Equals(
                    _configuration.OriginName,
                    _identity.OriginName,
                    StringComparison.Ordinal));
            _cachedAtUtc = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public void Invalidate() => _cached = null;

    private static string? NormalizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.TrimEnd('/'), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.ToString().TrimEnd('/');
    }
}

public sealed record DragnetOnboardingStatus(
    bool IdentityConfigured,
    bool EndpointConfigured,
    bool EndpointUsesHttps,
    bool EndpointReachable,
    bool EndpointIdentityMatched,
    bool EndpointSignatureVerified,
    bool EndpointVerified,
    string? EndpointError,
    bool PeerConnected,
    bool SeedConfigured,
    bool UpdateCurrent,
    bool RestartRequired)
{
    public int CompletedChecks =>
        new[]
        {
            IdentityConfigured,
            EndpointConfigured,
            EndpointUsesHttps,
            EndpointReachable,
            EndpointIdentityMatched,
            EndpointSignatureVerified,
            PeerConnected,
            UpdateCurrent
        }.Count(value => value);

    public const int TotalChecks = 8;
}
