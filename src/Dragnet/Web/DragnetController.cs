using Dragnet.Configuration;
using Dragnet.Models;
using Dragnet.Services;
using Dragnet.Storage;
using Dragnet.Transport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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

    public DragnetController(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetTransportService transportService,
        DragnetUpdateService updateService)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _transportService = transportService;
        _updateService = updateService;
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
            UpdateAvailable = update.UpdateAvailable,
            UpdateCheckEnabled = update.CheckEnabled,
            UpdateCheckedAtUtc = update.CheckedAtUtc,
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

public sealed record DragnetStatusResponse
{
    public required bool Enabled { get; init; }

    public required string Version { get; init; }

    public string? LatestVersion { get; init; }

    public required bool UpdateAvailable { get; init; }

    public required bool UpdateCheckEnabled { get; init; }

    public DateTimeOffset? UpdateCheckedAtUtc { get; init; }

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
