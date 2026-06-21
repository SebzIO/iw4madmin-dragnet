using Dragnet.Models;
using Dragnet.Services;
using Dragnet.Storage;
using Dragnet.Transport;

namespace Dragnet;

public static class DragnetMenuBridge
{
    private static DragnetPeerStore? _peerStore;
    private static DragnetTrustService? _trustService;
    private static DragnetReviewService? _reviewService;

    internal static void Configure(
        DragnetPeerStore peerStore,
        DragnetTrustService trustService,
        DragnetReviewService reviewService)
    {
        _peerStore = peerStore;
        _trustService = trustService;
        _reviewService = reviewService;
    }

    public static async Task<string> ExecuteAsync(
        string action,
        string id,
        string actorName,
        int? actorClientId)
    {
        if (_peerStore is null || _trustService is null || _reviewService is null)
        {
            return "Dragnet is still starting. Try again shortly.";
        }

        action = action.Trim().ToLowerInvariant();
        id = id.Trim();

        if (action is "resync" or "clearerror" or "trust" or "untrust")
        {
            var peers = await _peerStore.ListAsync(CancellationToken.None);
            var peer = peers.FirstOrDefault(item =>
                item.OriginId.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (peer is null && id.Length >= 16)
            {
                var partialMatches = peers.Where(item =>
                        item.OriginId.StartsWith(id, StringComparison.OrdinalIgnoreCase) ||
                        id.StartsWith(item.OriginId, StringComparison.OrdinalIgnoreCase) ||
                        item.OriginId.EndsWith(id, StringComparison.OrdinalIgnoreCase) ||
                        id.EndsWith(item.OriginId, StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToList();
                if (partialMatches.Count == 1)
                {
                    peer = partialMatches[0];
                }
            }
            if (peer is null)
            {
                return "Dragnet peer was not found.";
            }

            switch (action)
            {
                case "resync":
                    return await _peerStore.RequestResyncAsync(peer.OriginId, CancellationToken.None)
                        ? $"Resync queued for {peer.OriginName}."
                        : $"Could not queue a resync for {peer.OriginName}.";
                case "clearerror":
                    await _peerStore.ClearErrorAsync(peer.OriginId, CancellationToken.None);
                    return $"Cleared the current error for {peer.OriginName}.";
                case "trust":
                    await _trustService.TrustAsync(peer.OriginId, peer.OriginName, false, false, CancellationToken.None);
                    return $"Trusted Dragnet origin {peer.OriginName}.";
                case "untrust":
                    return await _trustService.UntrustAsync(peer.OriginId, CancellationToken.None)
                        ? $"Removed trust from {peer.OriginName}."
                        : $"{peer.OriginName} was not trusted.";
            }
        }

        if (action is "approve" or "reject")
        {
            var match = await _reviewService.FindByPrefixAsync(id, CancellationToken.None);
            if (match.Match is null)
            {
                return match.Result.Message;
            }

            var reviewAction = (action, match.Match.ReviewState) switch
            {
                ("approve", DragnetReviewState.PendingBan) => DragnetReviewAction.ApproveBan,
                ("reject", DragnetReviewState.PendingBan) => DragnetReviewAction.DenyBan,
                ("approve", DragnetReviewState.PendingLift) => DragnetReviewAction.ApproveLift,
                ("reject", DragnetReviewState.PendingLift) => DragnetReviewAction.DenyLift,
                _ => (DragnetReviewAction?)null
            };
            if (reviewAction is null)
            {
                return $"Dragnet event is {match.Match.ReviewState} and is no longer pending.";
            }

            var result = await _reviewService.ApplyActionAsync(
                match.Match.Event.EventId,
                reviewAction.Value,
                "Reviewed from CWS Admin Menu",
                actorName,
                actorClientId,
                CancellationToken.None);
            return result.Message;
        }

        return "That Dragnet menu action is not supported.";
    }
}
