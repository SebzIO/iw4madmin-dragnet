namespace Dragnet.Models;

public enum DragnetAuditCategory
{
    Moderation,
    Evidence,
    Trust,
    Peer,
    Configuration,
    Notification,
    Update,
    System
}

public sealed record DragnetAuditEntry
{
    public required string AuditId { get; init; }
    public required DragnetAuditCategory Category { get; init; }
    public required string Action { get; init; }
    public required string ActorName { get; init; }
    public int? ActorClientId { get; init; }
    public string? TargetName { get; init; }
    public string? TargetId { get; init; }
    public string? OriginName { get; init; }
    public string? EventId { get; init; }
    public string? Details { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
}
