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
    public string? PlayerName { get; init; }
    public string? Reason { get; init; }
    public string? PlayerGame { get; init; }
    public string? AdminName { get; init; }
    public string? OriginServerName { get; init; }
    public string? RiskScore { get; init; }
    public string? RiskSummary { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public List<int> AcknowledgedByClientIds { get; set; } = [];
}
