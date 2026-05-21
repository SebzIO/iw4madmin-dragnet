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
    private readonly ILogger<DragnetTransportService> _logger;
    private readonly HttpClient _httpClient = new();
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
        ILogger<DragnetTransportService> logger)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _identity = identity;
        _identityService = identityService;
        _reviewService = reviewService;
        _trustService = trustService;
        _logger = logger;
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
        await _peerStore.UpsertAsync(request.Sender, token);
        await ImportEventsAsync(request.Events, token);

        foreach (var peer in request.KnownPeers)
        {
            if (!string.Equals(peer.OriginId, _identity.OriginId, StringComparison.OrdinalIgnoreCase))
            {
                await _peerStore.UpsertAsync(peer, token);
            }
        }

        return new DragnetHeartbeatResponse
        {
            Receiver = CreateLocalPeerInfo(),
            KnownPeers = await CreateKnownPeerInfoAsync(token),
            Events = await CreateEventBatchAsync(token)
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
            if (string.Equals(peer.OriginId, _identity.OriginId, StringComparison.OrdinalIgnoreCase))
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

                var request = new DragnetHeartbeatRequest
                {
                    Sender = CreateLocalPeerInfo(),
                    KnownPeers = await CreateKnownPeerInfoAsync(token),
                    Events = await CreateEventBatchAsync(token)
                };

                var response = await _httpClient.PostAsJsonAsync(
                    $"{peer.Endpoint.TrimEnd('/')}/heartbeat",
                    request,
                    DragnetJson.Options,
                    token);

                response.EnsureSuccessStatusCode();
                var heartbeat = await response.Content.ReadFromJsonAsync<DragnetHeartbeatResponse>(
                    DragnetJson.Options,
                    token);

                if (heartbeat is null)
                {
                    await _peerStore.MarkErrorAsync(peer.OriginId, "Empty heartbeat response", token);
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

                await _peerStore.UpsertAsync(heartbeat.Receiver, token);
                foreach (var knownPeer in heartbeat.KnownPeers)
                {
                    if (!string.Equals(knownPeer.OriginId, _identity.OriginId, StringComparison.OrdinalIgnoreCase))
                    {
                        await _peerStore.UpsertAsync(knownPeer, token);
                    }
                }

                await ImportEventsAsync(heartbeat.Events, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _peerStore.MarkErrorAsync(peer.OriginId, ex.Message, token);
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
                var result = await _reviewService.ApplyActionAsync(envelope.EventId, action, "Auto-approved trusted origin", token);
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

    private DragnetPeerInfo CreateLocalPeerInfo() => new()
    {
        OriginId = _identity.OriginId,
        OriginName = _identity.OriginName,
        PublicEndpoint = _configuration.PublicEndpoint,
        SeenAtUtc = DateTimeOffset.UtcNow
    };

    private async Task<IReadOnlyList<DragnetPeerInfo>> CreateKnownPeerInfoAsync(CancellationToken token)
    {
        var peers = await _peerStore.ListAsync(token);
        return peers
            .Where(peer => string.IsNullOrWhiteSpace(peer.LastError))
            .Take(50)
            .Select(peer => new DragnetPeerInfo
            {
                OriginId = peer.OriginId,
                OriginName = peer.OriginName,
                PublicEndpoint = peer.Endpoint,
                SeenAtUtc = peer.LastSeenUtc
            })
            .ToList();
    }

    private async Task<IReadOnlyList<DragnetEventEnvelope>> CreateEventBatchAsync(CancellationToken token)
    {
        var events = await _eventStore.ListAsync(token);
        return events
            .Where(item => item.ReviewState is DragnetReviewState.ApprovedBan or DragnetReviewState.ApprovedLift)
            .Where(item => item.Event.EventType is DragnetEventType.BanLifted ||
                           !item.Event.IsExpired(DateTimeOffset.UtcNow))
            .OrderByDescending(item => item.Event.CreatedAtUtc)
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

    private bool IsAllowedEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return !_configuration.RequireHttps || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool IsFixedOriginPeer(DragnetPeerRecord peer) =>
        !Uri.TryCreate(peer.OriginId, UriKind.Absolute, out _);

    public void Dispose()
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _httpClient.Dispose();
    }
}
