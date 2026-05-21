using Dragnet.Configuration;
using Dragnet.Models;
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

    public DragnetController(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetTransportService transportService)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _transportService = transportService;
    }

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("/dragnet/heartbeat")]
    [ProducesResponseType<DragnetHeartbeatResponse>(StatusCodes.Status200OK)]
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

        return Ok(await _transportService.HandleHeartbeatAsync(request, token));
    }

    [Authorize(Policy = "Permissions.Interaction.Read")]
    [HttpGet("/api/dragnet/status")]
    [ProducesResponseType<DragnetStatusResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DragnetStatusResponse>> Status(CancellationToken token)
    {
        var events = await _eventStore.ListAsync(token);
        var peers = await _peerStore.ListAsync(token);

        return Ok(new DragnetStatusResponse
        {
            Enabled = _configuration.Enabled,
            PublicEndpoint = _configuration.PublicEndpoint,
            PeerCount = peers.Count,
            HealthyPeerCount = peers.Count(peer => string.IsNullOrWhiteSpace(peer.LastError)),
            PendingBanCount = events.Count(item => item.ReviewState is DragnetReviewState.PendingBan),
            PendingLiftCount = events.Count(item => item.ReviewState is DragnetReviewState.PendingLift),
            ApprovedBanCount = events.Count(item => item.ReviewState is DragnetReviewState.ApprovedBan),
            ApprovedLiftCount = events.Count(item => item.ReviewState is DragnetReviewState.ApprovedLift),
            ImportedCount = events.Count(item => item.ImportedAtUtc is not null),
            ImportErrorCount = events.Count(item => !string.IsNullOrWhiteSpace(item.ImportError))
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

    public string? PublicEndpoint { get; init; }

    public required int PeerCount { get; init; }

    public required int HealthyPeerCount { get; init; }

    public required int PendingBanCount { get; init; }

    public required int PendingLiftCount { get; init; }

    public required int ApprovedBanCount { get; init; }

    public required int ApprovedLiftCount { get; init; }

    public required int ImportedCount { get; init; }

    public required int ImportErrorCount { get; init; }
}
