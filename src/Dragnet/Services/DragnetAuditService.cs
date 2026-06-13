using Dragnet.Models;
using Dragnet.Storage;

namespace Dragnet.Services;

public sealed class DragnetAuditService
{
    private readonly DragnetAuditStore _store;

    public DragnetAuditService(DragnetAuditStore store)
    {
        _store = store;
    }

    public Task RecordAsync(
        DragnetAuditCategory category,
        string action,
        string actorName,
        int? actorClientId,
        string? targetName,
        string? targetId,
        string? originName,
        string? eventId,
        string? details,
        CancellationToken token) =>
        _store.AddAsync(new DragnetAuditEntry
        {
            AuditId = Guid.NewGuid().ToString("N"),
            Category = category,
            Action = action,
            ActorName = string.IsNullOrWhiteSpace(actorName) ? "Dragnet system" : actorName.Trim(),
            ActorClientId = actorClientId,
            TargetName = NullIfWhiteSpace(targetName),
            TargetId = NullIfWhiteSpace(targetId),
            OriginName = NullIfWhiteSpace(originName),
            EventId = NullIfWhiteSpace(eventId),
            Details = NullIfWhiteSpace(details),
            OccurredAtUtc = DateTimeOffset.UtcNow
        }, token);

    public Task<IReadOnlyList<DragnetAuditEntry>> ListAsync(
        int maximum,
        CancellationToken token) =>
        _store.ListAsync(maximum, token);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
