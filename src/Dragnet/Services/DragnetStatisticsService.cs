using Dragnet.Models;
using Dragnet.Storage;
using Dragnet.Transport;
namespace Dragnet.Services;

public sealed class DragnetStatisticsService
{
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly Func<int> _localServerCount;

    public DragnetStatisticsService(
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        Func<int> localServerCount)
    {
        _eventStore = eventStore;
        _peerStore = peerStore;
        _localServerCount = localServerCount;
    }

    public async Task<DragnetStatistics> GetAsync(CancellationToken token)
    {
        var events = await _eventStore.ListAsync(token);
        var peers = await _peerStore.ListAsync(token);
        var peerNodes = peers
            .GroupBy(peer => peer.Endpoint.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var localServerCount = _localServerCount();
        var participatingServerCount = Math.Max(0, localServerCount) +
                                       peerNodes.Sum(node =>
                                           node.Max(peer => Math.Clamp(peer.ServerCount, 1, 10_000)));
        var sharedBanCount = events.Count(item => item.Event.EventType is DragnetEventType.BanCreated);

        return new DragnetStatistics(
            ParticipatingServerCount: participatingServerCount,
            ParticipatingNodeCount: peerNodes.Count + 1,
            SharedBanCount: sharedBanCount);
    }
}

public sealed record DragnetStatistics(
    int ParticipatingServerCount,
    int ParticipatingNodeCount,
    int SharedBanCount);
