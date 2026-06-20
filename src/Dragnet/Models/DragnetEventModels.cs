using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dragnet.Models;

public enum DragnetEventType
{
    BanCreated,
    BanLifted
}

public enum DragnetPenaltyKind
{
    TempBan,
    Ban
}

public enum DragnetBanCategory
{
    Cheating,
    BanEvasion,
    ExploitAbuse,
    Toxicity,
    Security,
    Other
}

public enum DragnetReviewState
{
    PendingBan,
    ApprovedBan,
    DeniedBan,
    IgnoredBan,
    ExpiredBan,
    PendingLift,
    ApprovedLift,
    DeniedLift,
    IgnoredLift,
    WatchlistedBan,
    WatchlistedLift,
    WatchlistLifted
}

public enum DragnetBanCoverageStatus
{
    Accepted,
    Queued,
    Enforced
}

public sealed record DragnetEventEnvelope
{
    public required string EventId { get; init; }

    public required DragnetEventType EventType { get; init; }

    public required string OriginId { get; init; }

    public required string OriginName { get; init; }

    public required string OriginServerName { get; init; }

    public string? OriginEndpoint { get; init; }

    public string? ForwardedByPeerId { get; init; }

    public required string OriginPublicKeyPem { get; init; }

    public required DragnetPenaltyKind PenaltyKind { get; init; }

    public required int Iw4mAdminPenaltyId { get; init; }

    public required string PlayerNetworkId { get; init; }

    public string? PlayerGame { get; init; }

    public required string PlayerName { get; init; }

    public IReadOnlyList<string> PlayerAliases { get; init; } = [];

    public required string Reason { get; init; }

    public DragnetBanCategory? PublicCategory { get; init; }

    public string? PublicReason { get; init; }

    public string? AdminName { get; init; }

    public string? EvidenceUrl { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public required string Signature { get; init; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiresAt && expiresAt <= now;

    public string ComputeUnsignedHash()
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(GetSigningPayload()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string GetSigningPayload()
    {
        var unsigned = this with
        {
            PublicCategory = null,
            PublicReason = null,
            Signature = ""
        };
        return JsonSerializer.Serialize(unsigned, DragnetJson.Options);
    }
}

public sealed record DragnetStoredEvent
{
    public required DragnetEventEnvelope Event { get; init; }

    public required DragnetReviewState ReviewState { get; set; }

    public DateTimeOffset FirstSeenUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? LocalDecisionReason { get; set; }

    public DateTimeOffset? ReviewedAtUtc { get; set; }

    public int? ReviewedByClientId { get; set; }

    public string? ReviewedByName { get; set; }

    public List<DragnetReviewAuditEntry> AuditTrail { get; set; } = [];

    public int? ImportedPenaltyId { get; set; }

    public DateTimeOffset? ImportedAtUtc { get; set; }

    public string? ImportError { get; set; }

    public DragnetEvidenceUpdate? EvidenceUpdate { get; set; }

    public List<DragnetBanAttestation> BanAttestations { get; set; } = [];

    public DateTimeOffset? LastWatchlistAlertedAtUtc { get; set; }
}

public sealed record DragnetEvidenceUpdate
{
    public required string UpdateId { get; init; }

    public required string EventId { get; init; }

    public required string OriginId { get; init; }

    public required string OriginName { get; init; }

    public required string OriginPublicKeyPem { get; init; }

    public required string EvidenceUrl { get; init; }

    public required string SubmittedByName { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required string Signature { get; init; }

    public string GetSigningPayload() =>
        JsonSerializer.Serialize(this with { Signature = "" }, DragnetJson.Options);
}

public sealed record DragnetBanAttestation
{
    public required string AttestationId { get; init; }

    public required string EventId { get; init; }

    public required string NetworkOriginId { get; init; }

    public required string NetworkName { get; init; }

    public string? PublicEndpoint { get; init; }

    public required string NetworkPublicKeyPem { get; init; }

    public required int ServerCount { get; init; }

    public IReadOnlyList<string> ServerNames { get; init; } = [];

    public required DragnetBanCoverageStatus Status { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public required string Signature { get; init; }

    public string GetSigningPayload() =>
        JsonSerializer.Serialize(this with { Signature = "" }, DragnetJson.Options);
}

public sealed record DragnetReviewAuditEntry
{
    public required DateTimeOffset ReviewedAtUtc { get; init; }

    public int? ReviewedByClientId { get; init; }

    public required string ReviewedByName { get; init; }

    public required DragnetReviewState PreviousState { get; init; }

    public required DragnetReviewState NewState { get; init; }

    public string? Reason { get; init; }
}

public static class DragnetJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);
}
