using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Services;
using Dragnet.Storage;
using Dragnet.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text;
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
    private readonly DragnetLedgerService _ledgerService;
    private readonly DragnetWebfrontService _webfrontService;
    private readonly DragnetNetworkProfileService _networkProfileService;
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
        DragnetLedgerService ledgerService,
        DragnetWebfrontService webfrontService,
        DragnetNetworkProfileService networkProfileService,
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
        _ledgerService = ledgerService;
        _webfrontService = webfrontService;
        _networkProfileService = networkProfileService;
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
    [HttpGet("/dragnet/ledger")]
    [Produces("text/html")]
    public async Task<ContentResult> Ledger(CancellationToken token) =>
        Content(await _webfrontService.RenderPublicLedgerAsync(token), "text/html; charset=utf-8");

    [AllowAnonymous]
    [HttpGet("/Interaction/Render/Webfront::Nav::Main::DragnetLedger")]
    [Produces("text/html")]
    public async Task<ContentResult> LedgerNavigation(CancellationToken token) =>
        Content(await _webfrontService.RenderPublicLedgerAsync(token), "text/html; charset=utf-8");

    [AllowAnonymous]
    [HttpGet("/dragnet/ledger/data")]
    [ProducesResponseType<DragnetLedgerSnapshot>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DragnetLedgerSnapshot>> LedgerData(CancellationToken token) =>
        Ok(await _ledgerService.GetSnapshotAsync(token));

    [AllowAnonymous]
    [HttpGet("/dragnet/network/data")]
    [ProducesResponseType<DragnetNetworkProfile>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DragnetNetworkProfile>> NetworkProfileData(
        [FromQuery] string id,
        CancellationToken token)
    {
        var profile = await _networkProfileService.GetAsync(id, token);
        return profile is null ? NotFound() : Ok(profile);
    }

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
            LedgerUrl = endpoint is null ? null : $"{endpoint}/ledger",
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
        var activePeers = peers.Where(peer =>
            DragnetPeerHealth.IsActive(peer, now, _configuration.PeerStaleAfter)).ToList();
        var update = _updateService.Status;
        var deliverableEventIds = events
            .Where(item => item.ReviewState is DragnetReviewState.ApprovedBan or DragnetReviewState.ApprovedLift)
            .Where(item => item.Event.EventType is DragnetEventType.BanLifted || !item.Event.IsExpired(now))
            .Select(item => item.Event.EventId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acknowledgementPeers = peers
            .Where(peer =>
                peer.SupportsDeliveryAcknowledgements &&
                DragnetPeerHealth.IsActive(peer, now, _configuration.PeerStaleAfter))
            .ToList();
        var acknowledgedDeliveries = acknowledgementPeers.Sum(peer =>
            (peer.EventDeliveries ?? []).Count(delivery =>
                delivery.AcknowledgedAtUtc is not null &&
                deliverableEventIds.Contains(delivery.EventId)));
        var deliveryTargets = deliverableEventIds.Count * acknowledgementPeers.Count;

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
            AutoUpdateEnabled = update.AutoUpdateEnabled,
            InstalledUpdateVersion = update.InstalledVersion,
            UpdateInstalledAtUtc = update.InstalledAtUtc,
            RestartRequired = update.RestartRequired,
            UpdateInstallError = update.InstallError,
            PublicEndpoint = _configuration.PublicEndpoint,
            PeerCount = activePeers.Count,
            HealthyPeerCount = activePeers.Count(peer => peer.ConsecutiveFailures == 0),
            DegradedPeerCount = activePeers.Count(peer => peer.ConsecutiveFailures > 0),
            StalePeerCount = peers.Count(peer =>
                !DragnetPeerHealth.IsQuarantined(peer) &&
                now - peer.LastSeenUtc > _configuration.PeerStaleAfter),
            ErroredPeerCount = peers.Count(peer =>
                !DragnetPeerHealth.IsQuarantined(peer) &&
                !string.IsNullOrWhiteSpace(peer.LastError)),
            QuarantinedPeerCount = peers.Count(DragnetPeerHealth.IsQuarantined),
            GossipEligiblePeerCount = activePeers.Count,
            RecentlyAdvertisedPeerCount = activePeers.Count(peer =>
                peer.LastAdvertisedAtUtc is { } advertisedAt &&
                now - advertisedAt <= _configuration.PeerStaleAfter),
            VerifiedIdentityPeerCount = activePeers.Count(peer => peer.IdentityVerified),
            LegacyIdentityPeerCount = activePeers.Count(peer => !peer.IdentityVerified),
            DeliveryAcknowledgementPeerCount = acknowledgementPeers.Count,
            DeliverableEventCount = deliverableEventIds.Count,
            AcknowledgedDeliveryCount = acknowledgedDeliveries,
            PendingDeliveryCount = Math.Max(0, deliveryTargets - acknowledgedDeliveries),
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

    [Authorize(Policy = "Permissions.Interaction.Read")]
    [HttpGet("/api/dragnet/diagnostics")]
    [Produces("application/json")]
    public async Task<IActionResult> Diagnostics(CancellationToken token)
    {
        var report = DragnetDiagnosticsService.Create(
            _configuration,
            await _peerStore.ListAsync(token),
            await _eventStore.ListAsync(token),
            _updateService.Status,
            DateTimeOffset.UtcNow);
        var payload = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
        var filename = $"dragnet-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json";
        return File(Encoding.UTF8.GetBytes(payload), "application/json", filename);
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
    public string? LedgerUrl { get; init; }
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

    public required bool AutoUpdateEnabled { get; init; }

    public string? InstalledUpdateVersion { get; init; }

    public DateTimeOffset? UpdateInstalledAtUtc { get; init; }

    public required bool RestartRequired { get; init; }

    public string? UpdateInstallError { get; init; }

    public string? PublicEndpoint { get; init; }

    public required int PeerCount { get; init; }

    public required int HealthyPeerCount { get; init; }

    public required int DegradedPeerCount { get; init; }

    public required int StalePeerCount { get; init; }

    public required int ErroredPeerCount { get; init; }

    public required int QuarantinedPeerCount { get; init; }

    public required int GossipEligiblePeerCount { get; init; }

    public required int RecentlyAdvertisedPeerCount { get; init; }

    public required int VerifiedIdentityPeerCount { get; init; }

    public required int LegacyIdentityPeerCount { get; init; }

    public required int DeliveryAcknowledgementPeerCount { get; init; }

    public required int DeliverableEventCount { get; init; }

    public required int AcknowledgedDeliveryCount { get; init; }

    public required int PendingDeliveryCount { get; init; }

    public required int PendingBanCount { get; init; }

    public required int PendingLiftCount { get; init; }

    public required int ApprovedBanCount { get; init; }

    public required int ApprovedLiftCount { get; init; }

    public required int ImportedCount { get; init; }

    public required int QueuedImportCount { get; init; }

    public required int ImportErrorCount { get; init; }
}
