using Dragnet.Models;

namespace Dragnet.Services;

internal static class DragnetEventRelationships
{
    public static bool LiftMatchesBan(
        DragnetEventEnvelope lift,
        DragnetEventEnvelope ban)
    {
        if (lift.EventType is not DragnetEventType.BanLifted ||
            ban.EventType is not DragnetEventType.BanCreated ||
            !lift.OriginId.Equals(ban.OriginId, StringComparison.OrdinalIgnoreCase) ||
            !lift.PlayerNetworkId.Equals(ban.PlayerNetworkId, StringComparison.OrdinalIgnoreCase) ||
            !GamesMatch(lift.PlayerGame, ban.PlayerGame))
        {
            return false;
        }

        if (lift.Iw4mAdminPenaltyId > 0 && ban.Iw4mAdminPenaltyId > 0)
        {
            return lift.Iw4mAdminPenaltyId == ban.Iw4mAdminPenaltyId;
        }

        return lift.CreatedAtUtc <= DateTimeOffset.UnixEpoch ||
               ban.CreatedAtUtc <= DateTimeOffset.UnixEpoch ||
               lift.CreatedAtUtc >= ban.CreatedAtUtc;
    }

    private static bool GamesMatch(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ||
        string.IsNullOrWhiteSpace(right) ||
        left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
