using System.Net.Http.Json;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Services;
using Dragnet.Storage;
using Microsoft.Extensions.Logging;

namespace Dragnet.Transport;

public sealed class DragnetTransportService : IDisposable
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetIdentityDocument _identity;
    private readonly DragnetIdentityService _identityService;
    private readonly DragnetReviewService _reviewService;
    private readonly DragnetTrustService _trustService;
    private readonly Func<int> _localServerCount;
    private readonly ILogger<DragnetTransportService> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    public DragnetTransportService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        DragnetReviewService reviewService,
        DragnetTrustService trustService,
        Func<int> localServerCount,
        ILogger<DragnetTransportService> logger)
        : this(
            configuration,
            eventStore,
            peerStore,
            identity,
            identityService,
            reviewService,
            trustService,
            localServerCount,
            logger,
            new HttpClient(),
            ownsHttpClient: true)
    {
    }

    public DragnetTransportService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        DragnetReviewService reviewService,
        DragnetTrustService trustService,
        Func<int> localServerCount,
        ILogger<DragnetTransportService> logger,
        HttpClient httpClient,
        bool ownsHttpClient = false)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _identity = identity;
        _identityService = identityService;
        _reviewService = reviewService;
        _trustService = trustService;
        _localServerCount = localServerCount;
        _logger = logger;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public void Start()
    {
        if (_runTask is not null || !_configuration.Enabled)
        {
            return;
        }

        _runCancellation = new CancellationTokenSource();
        _runTask = RunAsync(_runCancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_runCancellation is null || _runTask is null)
        {
            return;
        }

        await _runCancellation.CancelAsync();

        try
        {
            await _runTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _runTask = null;
        }
    }

    public async Task<DragnetHeartbeatResponse> HandleHeartbeatAsync(
        DragnetHeartbeatRequest request,
        CancellationToken token)
    {
        ValidateHeartbeatRequest(request);
        var senderIdentityVerified = VerifyPeerIdentity(request.Sender, requireFresh: true);

        if (!IsLocalPeer(request.Sender))
        {
            await _peerStore.UpsertAsync(request.Sender, token, senderIdentityVerified);
        }

        await ImportEventsAsync(request.Events, token);

        foreach (var peer in request.KnownPeers)
        {
            if (!IsLocalPeer(peer))
            {
                await _peerStore.UpsertAsync(peer, token, VerifyPeerIdentity(peer, requireFresh: false));
            }
        }

        var eventBatch = await CreateEventBatchAsync(request.Sender.OriginId, token);
        await _peerStore.MarkEventBatchSentAsync(request.Sender.OriginId, eventBatch, token);

        return new DragnetHeartbeatResponse
        {
            Receiver = CreateLocalPeerInfo(),
            KnownPeers = await CreateKnownPeerInfoAsync(token),
            Events = eventBatch
        };
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_configuration.PeerHeartbeatInterval);

        await SendHeartbeatsAsync(token);

        while (await timer.WaitForNextTickAsync(token))
        {
            await SendHeartbeatsAsync(token);
        }
    }

    private async Task SendHeartbeatsAsync(CancellationToken token)
    {
        var peers = await _peerStore.ListAsync(token);
        foreach (var peer in peers)
        {
            if (IsLocalPeer(peer))
            {
                continue;
            }

            try
            {
                if (!IsAllowedEndpoint(peer.Endpoint))
                {
                    await _peerStore.MarkErrorAsync(peer.OriginId, "Peer endpoint must be absolute HTTPS", token);
                    continue;
                }

                var eventBatch = await CreateEventBatchAsync(peer.OriginId, token);
                var request = new DragnetHeartbeatRequest
                {
                    Sender = CreateLocalPeerInfo(),
                    KnownPeers = await CreateKnownPeerInfoAsync(token),
                    Events = eventBatch
                };

                var response = await _httpClient.PostAsJsonAsync(
                    $"{peer.Endpoint.TrimEnd('/')}/heartbeat",
                    request,
                    DragnetJson.WireOptions,
                    token);

                await EnsureSuccessAsync(response, token);
                var heartbeat = await response.Content.ReadFromJsonAsync<DragnetHeartbeatResponse>(
                    DragnetJson.WireOptions,
                    token);

                if (heartbeat is null)
                {
                    await _peerStore.MarkErrorAsync(
                        peer.OriginId,
                        "Empty heartbeat response",
                        token,
                        _configuration.PeerFailureThreshold);
                    continue;
                }

                if (IsFixedOriginPeer(peer) &&
                    !string.Equals(heartbeat.Receiver.OriginId, peer.OriginId, StringComparison.OrdinalIgnoreCase))
                {
                    await _peerStore.MarkErrorAsync(
                        peer.OriginId,
                        $"Unexpected origin id {heartbeat.Receiver.OriginId}",
                        token);
                    continue;
                }

                foreach (var knownPeer in heartbeat.KnownPeers)
                {
                    if (!IsLocalPeer(knownPeer))
                    {
                        await _peerStore.UpsertAsync(
                            knownPeer,
                            token,
                            VerifyPeerIdentity(knownPeer, requireFresh: false));
                    }
                }

                var receiverIdentityVerified = VerifyPeerIdentity(heartbeat.Receiver, requireFresh: true);
                await ImportEventsAsync(heartbeat.Events, token);
                await _peerStore.MarkHeartbeatSucceededAsync(
                    peer.OriginId,
                    heartbeat.Receiver,
                    token,
                    receiverIdentityVerified);
                await _peerStore.MarkEventBatchSentAsync(heartbeat.Receiver.OriginId, eventBatch, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _peerStore.MarkErrorAsync(
                    peer.OriginId,
                    ex.Message,
                    token,
                    _configuration.PeerFailureThreshold);
                _logger.LogWarning(ex, "Dragnet heartbeat to {Endpoint} failed", peer.Endpoint);
            }
        }
    }

    private async Task ImportEventsAsync(IReadOnlyList<DragnetEventEnvelope> events, CancellationToken token)
    {
        foreach (var envelope in events)
        {
            if (string.Equals(envelope.OriginId, _identity.OriginId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (envelope.EventType is DragnetEventType.BanCreated &&
                envelope.IsExpired(DateTimeOffset.UtcNow))
            {
                continue;
            }

            if (!VerifyEnvelope(envelope))
            {
                _logger.LogWarning("Rejected Dragnet event {EventId}: invalid signature", envelope.EventId);
                continue;
            }

            var trust = _trustService.Evaluate(envelope);
            var reviewState = envelope.EventType is DragnetEventType.BanLifted
                ? DragnetReviewState.PendingLift
                : DragnetReviewState.PendingBan;

            var inserted = await _eventStore.UpsertAsync(new DragnetStoredEvent
            {
                Event = envelope,
                ReviewState = reviewState
            }, token);

            if (inserted)
            {
                _logger.LogInformation(
                    "Imported Dragnet event {EventId} from {OriginName}",
                    envelope.EventId,
                    envelope.OriginName);
            }

            if (inserted && trust.AutoApprove)
            {
                var action = envelope.EventType is DragnetEventType.BanLifted
                    ? DragnetReviewAction.ApproveLift
                    : DragnetReviewAction.ApproveBan;
                var result = await _reviewService.ApplyActionAsync(
                    envelope.EventId,
                    action,
                    "Auto-approved trusted origin",
                    "Dragnet auto-approval",
                    null,
                    token);
                if (!result.Success)
                {
                    _logger.LogWarning(
                        "Auto-approval failed for Dragnet event {EventId}: {Reason}",
                        envelope.EventId,
                        result.Message);
                }
            }
        }
    }

    private void ValidateHeartbeatRequest(DragnetHeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sender.OriginId) ||
            string.IsNullOrWhiteSpace(request.Sender.OriginName))
        {
            throw new InvalidOperationException("Heartbeat sender identity is required.");
        }

        ValidateServerCount(request.Sender.ServerCount);

        if (string.Equals(request.Sender.OriginId, _identity.OriginId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Heartbeat sender cannot be the local origin.");
        }

        if (request.KnownPeers.Count > _configuration.MaxKnownPeersPerHeartbeat)
        {
            throw new InvalidOperationException("Heartbeat contains too many known peers.");
        }

        if (request.Events.Count > _configuration.MaxEventsPerHeartbeat)
        {
            throw new InvalidOperationException("Heartbeat contains too many events.");
        }

        if (!string.IsNullOrWhiteSpace(request.Sender.PublicEndpoint) &&
            !IsAllowedEndpoint(request.Sender.PublicEndpoint))
        {
            throw new InvalidOperationException("Heartbeat sender endpoint must be absolute HTTPS.");
        }

        foreach (var peer in request.KnownPeers)
        {
            if (string.IsNullOrWhiteSpace(peer.OriginId) ||
                string.IsNullOrWhiteSpace(peer.OriginName))
            {
                throw new InvalidOperationException("Known peer identity is required.");
            }

            ValidateServerCount(peer.ServerCount);

            if (!string.IsNullOrWhiteSpace(peer.PublicEndpoint) &&
                !IsAllowedEndpoint(peer.PublicEndpoint))
            {
                throw new InvalidOperationException("Known peer endpoint must be absolute HTTPS.");
            }

            ValidateDirectoryMetadata(peer);
        }

        ValidateDirectoryMetadata(request.Sender);
    }

    private DragnetPeerInfo CreateLocalPeerInfo()
    {
        var unsigned = new DragnetPeerInfo
        {
            OriginId = _identity.OriginId,
            OriginName = _identity.OriginName,
            PublicEndpoint = _configuration.PublicEndpoint,
            ServerCount = _localServerCount(),
            DirectoryListed = _configuration.DirectoryListingEnabled,
            Region = NormalizeMetadata(_configuration.DirectoryRegion),
            Website = NormalizeMetadata(_configuration.DirectoryWebsite),
            Version = DragnetBuildInfo.Version,
            PublicKeyPem = _identity.PublicKeyPem,
            SeenAtUtc = DateTimeOffset.UtcNow
        };
        return unsigned with
        {
            Signature = _identityService.Sign(_identity, unsigned.GetSigningPayload())
        };
    }

    private async Task<IReadOnlyList<DragnetPeerInfo>> CreateKnownPeerInfoAsync(CancellationToken token)
    {
        var peers = await _peerStore.ListAsync(token);
        return peers
            .Where(peer => string.IsNullOrWhiteSpace(peer.LastError))
            .Take(Math.Max(0, _configuration.MaxKnownPeersPerHeartbeat))
            .Select(peer => new DragnetPeerInfo
            {
                OriginId = peer.OriginId,
                OriginName = peer.OriginName,
                PublicEndpoint = peer.Endpoint,
                ServerCount = peer.ServerCount,
                DirectoryListed = peer.DirectoryListed,
                Region = peer.Region,
                Website = peer.Website,
                Version = peer.Version,
                PublicKeyPem = peer.PublicKeyPem,
                Signature = peer.Signature,
                SeenAtUtc = peer.LastSeenUtc
            })
            .ToList();
    }

    private async Task<IReadOnlyList<DragnetEventEnvelope>> CreateEventBatchAsync(
        string peerOriginId,
        CancellationToken token)
    {
        var events = await _eventStore.ListAsync(token);
        var peer = (await _peerStore.ListAsync(token))
            .FirstOrDefault(peer => string.Equals(peer.OriginId, peerOriginId, StringComparison.OrdinalIgnoreCase));
        var lastSentAt = peer?.LastEventSentAtUtc;

        return events
            .Where(item => item.ReviewState is DragnetReviewState.ApprovedBan or DragnetReviewState.ApprovedLift)
            .Where(item => item.Event.EventType is DragnetEventType.BanLifted ||
                           !item.Event.IsExpired(DateTimeOffset.UtcNow))
            .Where(item => lastSentAt is null || item.Event.CreatedAtUtc > lastSentAt)
            .OrderBy(item => item.Event.CreatedAtUtc)
            .Take(Math.Max(1, _configuration.MaxEventsPerHeartbeat))
            .Select(item => item.Event)
            .ToList();
    }

    private bool VerifyEnvelope(DragnetEventEnvelope envelope)
    {
        try
        {
            return _identityService.Verify(envelope);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private bool VerifyPeerIdentity(DragnetPeerInfo peer, bool requireFresh)
    {
        if (string.IsNullOrWhiteSpace(peer.PublicKeyPem) ||
            string.IsNullOrWhiteSpace(peer.Signature))
        {
            return false;
        }

        try
        {
            if (_identityService.Verify(
                    peer.OriginId,
                    peer.PublicKeyPem,
                    peer.GetSigningPayload(),
                    peer.Signature))
            {
                var now = DateTimeOffset.UtcNow;
                if (peer.SeenAtUtc > now.AddMinutes(5) ||
                    requireFresh && now - peer.SeenAtUtc > _configuration.PeerStaleAfter)
                {
                    throw new InvalidOperationException($"Peer identity proof is stale for {peer.OriginName}.");
                }

                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
        }

        throw new InvalidOperationException($"Peer identity proof is invalid for {peer.OriginName}.");
    }

    private bool IsAllowedEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return !_configuration.RequireHttps || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static void ValidateDirectoryMetadata(DragnetPeerInfo peer)
    {
        if (peer.OriginName.Length > 120 ||
            peer.Region?.Length > 80 ||
            peer.Version?.Length > 40)
        {
            throw new InvalidOperationException("Heartbeat directory metadata exceeds allowed lengths.");
        }

        if (!string.IsNullOrWhiteSpace(peer.Website) &&
            (!Uri.TryCreate(peer.Website, UriKind.Absolute, out var website) ||
             website.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Heartbeat directory website must be an absolute HTTPS URL.");
        }
    }

    private static string? NormalizeMetadata(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool IsLocalPeer(DragnetPeerInfo peer)
    {
        return string.Equals(peer.OriginId, _identity.OriginId, StringComparison.OrdinalIgnoreCase) ||
               IsLocalEndpoint(peer.PublicEndpoint) ||
               IsLocalEndpoint(peer.OriginId);
    }

    private bool IsLocalPeer(DragnetPeerRecord peer)
    {
        return string.Equals(peer.OriginId, _identity.OriginId, StringComparison.OrdinalIgnoreCase) ||
               IsLocalEndpoint(peer.Endpoint) ||
               IsLocalEndpoint(peer.OriginId);
    }

    private bool IsLocalEndpoint(string? endpoint)
    {
        return !string.IsNullOrWhiteSpace(_configuration.PublicEndpoint) &&
               !string.IsNullOrWhiteSpace(endpoint) &&
               endpoint.TrimEnd('/').Equals(_configuration.PublicEndpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFixedOriginPeer(DragnetPeerRecord peer) =>
        !Uri.TryCreate(peer.OriginId, UriKind.Absolute, out _);

    private static void ValidateServerCount(int serverCount)
    {
        if (serverCount is < 0 or > 10_000)
        {
            throw new InvalidOperationException("Peer server count is outside the allowed range.");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(token);
        var message = string.IsNullOrWhiteSpace(responseBody)
            ? $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase})."
            : $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). {responseBody}";
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    public void Dispose()
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
