using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Services;
using Dragnet.Storage;
using Dragnet.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Dragnet.Web;

[ApiController]
[Produces("application/json")]
public sealed class DragnetController : ControllerBase
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetTransportService _transportService;
    private readonly DragnetUpdateService _updateService;
    private readonly DragnetDirectoryService _directoryService;
    private readonly DragnetIdentityDocument _identity;
    private readonly DragnetIdentityService _identityService;
    private readonly Func<int> _localServerCount;

    public DragnetController(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetTransportService transportService,
        DragnetUpdateService updateService,
        DragnetDirectoryService directoryService,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        Func<int> localServerCount)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _transportService = transportService;
        _updateService = updateService;
        _directoryService = directoryService;
        _identity = identity;
        _identityService = identityService;
        _localServerCount = localServerCount;
    }

    [AllowAnonymous]
    [HttpGet("/dragnet/health")]
    [ProducesResponseType<DragnetHealthResponse>(StatusCodes.Status200OK)]
    public ActionResult<DragnetHealthResponse> Health()
    {
        var unsigned = new DragnetHealthResponse
        {
            Status = _configuration.Enabled ? "ready" : "disabled",
            Version = DragnetBuildInfo.Version,
            OriginId = _identity.OriginId,
            OriginName = _identity.OriginName,
            ServerCount = Math.Max(0, _localServerCount()),
            PublicEndpoint = _configuration.PublicEndpoint,
            PublicKeyPem = _identity.PublicKeyPem,
            CheckedAtUtc = DateTimeOffset.UtcNow
        };
        return Ok(unsigned with
        {
            Signature = _identityService.Sign(_identity, unsigned.GetSigningPayload())
        });
    }

    [AllowAnonymous]
    [HttpGet("/dragnet/directory")]
    [ProducesResponseType<IReadOnlyList<DragnetDirectoryEntry>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DragnetDirectoryEntry>>> Directory(CancellationToken token) =>
        Ok(await _directoryService.ListAsync(token));

    [AllowAnonymous]
    [HttpGet("/dragnet/setup-guide")]
    [ProducesResponseType<DragnetSetupGuideResponse>(StatusCodes.Status200OK)]
    public ActionResult<DragnetSetupGuideResponse> SetupGuide()
    {
        var endpoint = _configuration.PublicEndpoint?.TrimEnd('/');
        return Ok(new DragnetSetupGuideResponse
        {
            NetworkName = _configuration.OriginName,
            PublicEndpoint = endpoint,
            HealthUrl = endpoint is null ? null : $"{endpoint}/health",
            HeartbeatUrl = endpoint is null ? null : $"{endpoint}/heartbeat",
            DirectoryUrl = endpoint is null ? null : $"{endpoint}/directory",
            OfficialBootstrapEndpoint = DragnetConfiguration.OfficialBootstrapEndpoint,
            DirectoryListingEnabled = _configuration.DirectoryListingEnabled,
            RequiredProxyFeatures =
            [
                "Valid HTTPS certificate for the public endpoint",
                "Forward X-Forwarded-Proto as https",
                "Allow POST requests to /dragnet/heartbeat",
                "Enable WebSocket upgrade support for the IW4MAdmin webfront"
            ],
            VerificationSteps =
            [
                "Confirm the health URL returns this origin fingerprint.",
                "Confirm a bootstrap peer can POST a signed heartbeat to the heartbeat URL.",
                "Wait for a direct signed heartbeat before expecting a verified directory badge.",
                "Trust remote origins separately; directory verification never grants trust."
            ]
        });
    }

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("/dragnet/heartbeat")]
    [ProducesResponseType<DragnetHeartbeatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DragnetHeartbeatResponse>> Heartbeat(
        [FromBody] DragnetHeartbeatRequest request,
        CancellationToken token)
    {
        if (!_configuration.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (_configuration.RequireHttps && !IsHttpsRequest())
        {
            return Forbid();
        }

        try
        {
            return Ok(await _transportService.HandleHeartbeatAsync(request, token));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize(Policy = "Permissions.Interaction.Read")]
    [HttpGet("/api/dragnet/status")]
    [ProducesResponseType<DragnetStatusResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DragnetStatusResponse>> Status(CancellationToken token)
    {
        var events = await _eventStore.ListAsync(token);
        var peers = await _peerStore.ListAsync(token);
        var now = DateTimeOffset.UtcNow;
        var update = _updateService.Status;

        return Ok(new DragnetStatusResponse
        {
            Enabled = _configuration.Enabled,
            Version = DragnetBuildInfo.Version,
            LatestVersion = update.LatestVersion,
            LatestReleaseUrl = update.ReleaseUrl,
            UpdateAvailable = update.UpdateAvailable,
            UpdateCheckEnabled = update.CheckEnabled,
            UpdateCheckedAtUtc = update.CheckedAtUtc,
            UpdateCheckError = update.CheckError,
            PublicEndpoint = _configuration.PublicEndpoint,
            PeerCount = peers.Count,
            HealthyPeerCount = peers.Count(peer =>
                string.IsNullOrWhiteSpace(peer.LastError) &&
                peer.ConsecutiveFailures == 0 &&
                now - peer.LastSeenUtc <= _configuration.PeerStaleAfter),
            DegradedPeerCount = peers.Count(peer =>
                string.IsNullOrWhiteSpace(peer.LastError) &&
                peer.ConsecutiveFailures > 0 &&
                now - peer.LastSeenUtc <= _configuration.PeerStaleAfter),
            StalePeerCount = peers.Count(peer => now - peer.LastSeenUtc > _configuration.PeerStaleAfter),
            ErroredPeerCount = peers.Count(peer => !string.IsNullOrWhiteSpace(peer.LastError)),
            PendingBanCount = events.Count(item => item.ReviewState is DragnetReviewState.PendingBan),
            PendingLiftCount = events.Count(item => item.ReviewState is DragnetReviewState.PendingLift),
            ApprovedBanCount = events.Count(item => item.ReviewState is DragnetReviewState.ApprovedBan),
            ApprovedLiftCount = events.Count(item => item.ReviewState is DragnetReviewState.ApprovedLift),
            ImportedCount = events.Count(item => item.ImportedAtUtc is not null),
            QueuedImportCount = events.Count(item =>
                item.ImportError?.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase) == true),
            ImportErrorCount = events.Count(item =>
                !string.IsNullOrWhiteSpace(item.ImportError) &&
                !item.ImportError.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase))
        });
    }

    private bool IsHttpsRequest()
    {
        if (Request.IsHttps)
        {
            return true;
        }

        return _configuration.TrustForwardedHttpsHeader &&
               Request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto) &&
               forwardedProto.Any(value => string.Equals(value, "https", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record DragnetHealthResponse
{
    public required string Status { get; init; }
    public required string Version { get; init; }
    public required string OriginId { get; init; }
    public required string OriginName { get; init; }
    public required int ServerCount { get; init; }
    public string? PublicEndpoint { get; init; }
    public required string PublicKeyPem { get; init; }
    public required DateTimeOffset CheckedAtUtc { get; init; }
    public string? Signature { get; init; }

    public string GetSigningPayload() =>
        JsonSerializer.Serialize(this with { Signature = null }, DragnetJson.Options);
}

public sealed record DragnetSetupGuideResponse
{
    public required string NetworkName { get; init; }
    public string? PublicEndpoint { get; init; }
    public string? HealthUrl { get; init; }
    public string? HeartbeatUrl { get; init; }
    public string? DirectoryUrl { get; init; }
    public required string OfficialBootstrapEndpoint { get; init; }
    public required bool DirectoryListingEnabled { get; init; }
    public IReadOnlyList<string> RequiredProxyFeatures { get; init; } = [];
    public IReadOnlyList<string> VerificationSteps { get; init; } = [];
}

public sealed record DragnetStatusResponse
{
    public required bool Enabled { get; init; }

    public required string Version { get; init; }

    public string? LatestVersion { get; init; }

    public string? LatestReleaseUrl { get; init; }

    public required bool UpdateAvailable { get; init; }

    public required bool UpdateCheckEnabled { get; init; }

    public DateTimeOffset? UpdateCheckedAtUtc { get; init; }

    public string? UpdateCheckError { get; init; }

    public string? PublicEndpoint { get; init; }

    public required int PeerCount { get; init; }

    public required int HealthyPeerCount { get; init; }

    public required int DegradedPeerCount { get; init; }

    public required int StalePeerCount { get; init; }

    public required int ErroredPeerCount { get; init; }

    public required int PendingBanCount { get; init; }

    public required int PendingLiftCount { get; init; }

    public required int ApprovedBanCount { get; init; }

    public required int ApprovedLiftCount { get; init; }

    public required int ImportedCount { get; init; }

    public required int QueuedImportCount { get; init; }

    public required int ImportErrorCount { get; init; }
}
