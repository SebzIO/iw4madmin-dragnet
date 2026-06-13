using Dragnet.Models;
using Dragnet.Storage;

namespace Dragnet.Services;

public sealed class DragnetReviewService
{
    private readonly DragnetEventStore _store;
    private readonly DragnetImportService _importService;
    private readonly DragnetTrustService _trustService;
    private readonly DragnetAttestationService? _attestationService;
    private readonly DragnetAuditService? _auditService;

    public DragnetReviewService(
        DragnetEventStore store,
        DragnetImportService importService,
        DragnetTrustService trustService)
        : this(store, importService, trustService, null)
    {
    }

    public DragnetReviewService(
        DragnetEventStore store,
        DragnetImportService importService,
        DragnetTrustService trustService,
        DragnetAttestationService? attestationService,
        DragnetAuditService? auditService = null)
    {
        _store = store;
        _importService = importService;
        _trustService = trustService;
        _attestationService = attestationService;
        _auditService = auditService;
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
        string reviewedByName,
        int? reviewedByClientId,
        CancellationToken token)
    {
        var item = await FindByPrefixAsync(eventId, token);
        if (item.Match is null)
        {
            return item.Result;
        }

        var targetState = GetTargetState(action);
        if (!CanApplyAction(item.Match.ReviewState, action))
        {
            return DragnetReviewResult.Failed(
                $"Dragnet event is {item.Match.ReviewState}; it cannot be changed to {targetState}.");
        }

        if (IsApproval(action) && !_trustService.Evaluate(item.Match.Event).IsTrusted)
        {
            return DragnetReviewResult.Failed(
                "Dragnet origin is not trusted. Trust the origin before approving/importing this event.");
        }

        var importResult = IsApproval(action)
            ? await _importService.ImportApprovedAsync(item.Match, token)
            : null;

        if (importResult is { Success: false })
        {
            return DragnetReviewResult.Failed(
                $"Dragnet import failed: {importResult.Message}");
        }

        await _store.SetReviewStateAsync(
            item.Match.Event.EventId,
            targetState,
            reason,
            reviewedByName,
            reviewedByClientId,
            token);
        if (action is DragnetReviewAction.ApproveBan &&
            _attestationService is not null &&
            importResult is { Imported: false } &&
            !importResult.Message.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase))
        {
            await _attestationService.PublishAsync(
                item.Match.Event.EventId,
                DragnetBanCoverageStatus.Accepted,
                token);
        }
        var message = $"Dragnet event {ShortId(item.Match.Event.EventId)} marked {targetState}.";
        if (importResult is { Imported: true })
        {
            message += " Imported into IW4MAdmin.";
        }
        else if (importResult is { Imported: false })
        {
            message += $" {importResult.Message}";
        }
        if (_auditService is not null)
        {
            await _auditService.RecordAsync(
                DragnetAuditCategory.Moderation,
                action.ToString(),
                reviewedByName,
                reviewedByClientId,
                item.Match.Event.PlayerName,
                item.Match.Event.PlayerNetworkId,
                item.Match.Event.OriginName,
                item.Match.Event.EventId,
                message,
                token);
        }

        return DragnetReviewResult.Succeeded(message);
    }

    public async Task<DragnetReviewResult> RetryImportAsync(
        string eventId,
        CancellationToken token)
    {
        var item = await FindByPrefixAsync(eventId, token);
        if (item.Match is null)
        {
            return item.Result;
        }

        if (item.Match.ReviewState is not (DragnetReviewState.ApprovedBan or DragnetReviewState.ApprovedLift))
        {
            return DragnetReviewResult.Failed("Only approved Dragnet events can be retried for import.");
        }

        if (!_trustService.Evaluate(item.Match.Event).IsTrusted)
        {
            return DragnetReviewResult.Failed(
                "Dragnet origin is not trusted. Trust the origin before retrying import.");
        }

        var importResult = await _importService.ImportApprovedAsync(item.Match, token);
        return importResult.Success
            ? DragnetReviewResult.Succeeded(importResult.Message)
            : DragnetReviewResult.Failed($"Dragnet import failed: {importResult.Message}");
    }

    public async Task<DragnetBulkReviewResult> ApplyBulkActionAsync(
        IReadOnlyList<string> eventIds,
        DragnetReviewAction action,
        string? reason,
        string reviewedByName,
        int? reviewedByClientId,
        CancellationToken token)
    {
        var distinctIds = eventIds
            .Where(eventId => !string.IsNullOrWhiteSpace(eventId))
            .Select(eventId => eventId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctIds.Count == 0)
        {
            return DragnetBulkReviewResult.Failed("Select at least one Dragnet event.");
        }

        if (distinctIds.Count > 100)
        {
            return DragnetBulkReviewResult.Failed("A bulk review can include at most 100 Dragnet events.");
        }

        var failures = new List<string>();
        var succeeded = 0;
        foreach (var eventId in distinctIds)
        {
            if (await _store.GetAsync(eventId, token) is null)
            {
                failures.Add($"{ShortId(eventId)}: Dragnet event not found.");
                continue;
            }

            var result = await ApplyActionAsync(
                eventId,
                action,
                reason,
                reviewedByName,
                reviewedByClientId,
                token);
            if (result.Success)
            {
                succeeded++;
            }
            else
            {
                failures.Add($"{ShortId(eventId)}: {result.Message}");
            }
        }

        return new DragnetBulkReviewResult(
            true,
            succeeded,
            failures.Count,
            failures,
            $"Bulk review complete: {succeeded} approved, {failures.Count} failed.");
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

    private static DragnetReviewState GetTargetState(DragnetReviewAction action) => action switch
    {
        DragnetReviewAction.ApproveBan => DragnetReviewState.ApprovedBan,
        DragnetReviewAction.DenyBan => DragnetReviewState.DeniedBan,
        DragnetReviewAction.IgnoreBan => DragnetReviewState.IgnoredBan,
        DragnetReviewAction.ApproveLift => DragnetReviewState.ApprovedLift,
        DragnetReviewAction.DenyLift => DragnetReviewState.DeniedLift,
        DragnetReviewAction.IgnoreLift => DragnetReviewState.IgnoredLift,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    private static bool CanApplyAction(
        DragnetReviewState currentState,
        DragnetReviewAction action)
    {
        var targetState = GetTargetState(action);
        if (currentState == targetState)
        {
            return false;
        }

        return action switch
        {
            DragnetReviewAction.ApproveBan or
            DragnetReviewAction.DenyBan or
            DragnetReviewAction.IgnoreBan =>
                currentState is DragnetReviewState.PendingBan or
                    DragnetReviewState.DeniedBan or
                    DragnetReviewState.IgnoredBan,
            DragnetReviewAction.ApproveLift or
            DragnetReviewAction.DenyLift or
            DragnetReviewAction.IgnoreLift =>
                currentState is DragnetReviewState.PendingLift or
                    DragnetReviewState.DeniedLift or
                    DragnetReviewState.IgnoredLift,
            _ => false
        };
    }

    private static bool IsApproval(DragnetReviewAction action) =>
        action is DragnetReviewAction.ApproveBan or DragnetReviewAction.ApproveLift;
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

public sealed record DragnetBulkReviewResult(
    bool Success,
    int SucceededCount,
    int FailedCount,
    IReadOnlyList<string> Failures,
    string Message)
{
    public static DragnetBulkReviewResult Failed(string message) =>
        new(false, 0, 0, [], message);
}
