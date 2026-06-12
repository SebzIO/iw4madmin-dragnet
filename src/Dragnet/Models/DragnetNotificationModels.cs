namespace Dragnet.Models;

public enum DragnetNotificationType
{
    NewBan,
    NewLift,
    EvidenceUpdated,
    StaleReview,
    UpdateInstalled
}

public sealed record DragnetNotification
{
    public required string NotificationId { get; init; }
    public required DragnetNotificationType Type { get; init; }
    public required string EventId { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required string OriginName { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public List<int> AcknowledgedByClientIds { get; set; } = [];
}
