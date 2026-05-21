using Dragnet.Models;
using Dragnet.Storage;

namespace Dragnet.Services;

public sealed class DragnetReviewService
{
    private readonly DragnetEventStore _store;

    public DragnetReviewService(DragnetEventStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<DragnetStoredEvent>> ListPendingAsync(
        DragnetReviewState state,
        int count,
        CancellationToken token)
    {
        await _store.ExpireElapsedTempBansAsync(DateTimeOffset.UtcNow, token);
        return (await _store.ListAsync(token))
            .Where(item => item.ReviewState == state)
            .Take(count)
            .ToList();
    }

    public async Task<DragnetReviewResult> ApplyActionAsync(
        string eventId,
        DragnetReviewAction action,
        string? reason,
        CancellationToken token)
    {
        var item = await FindByPrefixAsync(eventId, token);
        if (item.Match is null)
        {
            return item.Result;
        }

        var (expectedState, targetState) = GetStateTransition(action);
        if (item.Match.ReviewState != expectedState)
        {
            return DragnetReviewResult.Failed(
                $"Dragnet event is {item.Match.ReviewState}, not {expectedState}.");
        }

        await _store.SetReviewStateAsync(item.Match.Event.EventId, targetState, reason, token);
        return DragnetReviewResult.Succeeded(
            $"Dragnet event {ShortId(item.Match.Event.EventId)} marked {targetState}.");
    }

    public async Task<(DragnetStoredEvent? Match, DragnetReviewResult Result)> FindByPrefixAsync(
        string eventIdPrefix,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(eventIdPrefix))
        {
            return (null, DragnetReviewResult.Failed("Provide a Dragnet event id."));
        }

        var matches = (await _store.ListAsync(token))
            .Where(item => item.Event.EventId.StartsWith(eventIdPrefix, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matches.Count switch
        {
            1 => (matches[0], DragnetReviewResult.Succeeded("Matched Dragnet event.")),
            0 => (null, DragnetReviewResult.Failed("No Dragnet event matched that id.")),
            _ => (null, DragnetReviewResult.Failed("Multiple Dragnet events matched that id. Use a longer id prefix."))
        };
    }

    public static string ShortId(string eventId) => eventId.Length <= 12 ? eventId : eventId[..12];

    private static (DragnetReviewState ExpectedState, DragnetReviewState TargetState) GetStateTransition(
        DragnetReviewAction action) => action switch
    {
        DragnetReviewAction.ApproveBan => (DragnetReviewState.PendingBan, DragnetReviewState.ApprovedBan),
        DragnetReviewAction.DenyBan => (DragnetReviewState.PendingBan, DragnetReviewState.DeniedBan),
        DragnetReviewAction.IgnoreBan => (DragnetReviewState.PendingBan, DragnetReviewState.IgnoredBan),
        DragnetReviewAction.ApproveLift => (DragnetReviewState.PendingLift, DragnetReviewState.ApprovedLift),
        DragnetReviewAction.DenyLift => (DragnetReviewState.PendingLift, DragnetReviewState.DeniedLift),
        DragnetReviewAction.IgnoreLift => (DragnetReviewState.PendingLift, DragnetReviewState.IgnoredLift),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };
}

public enum DragnetReviewAction
{
    ApproveBan,
    DenyBan,
    IgnoreBan,
    ApproveLift,
    DenyLift,
    IgnoreLift
}

public sealed record DragnetReviewResult(bool Success, string Message)
{
    public static DragnetReviewResult Succeeded(string message) => new(true, message);

    public static DragnetReviewResult Failed(string message) => new(false, message);
}
