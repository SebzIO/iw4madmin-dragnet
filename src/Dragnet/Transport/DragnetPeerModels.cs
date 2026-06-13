using System.Text.Json;
using Dragnet.Models;

namespace Dragnet.Transport;

public sealed record DragnetPeerInfo
{
    public required string OriginId { get; init; }

    public required string OriginName { get; init; }

    public string? PublicEndpoint { get; init; }

    public int ServerCount { get; init; }

    public bool DirectoryListed { get; init; }

    public string? Region { get; init; }

    public string? Website { get; init; }

    public string? Version { get; init; }

    public string? PublicKeyPem { get; init; }

    public string? Signature { get; init; }

    public bool SupportsDeliveryAcknowledgements { get; init; }

    public bool SupportsEvidenceUpdates { get; init; }

    public bool SupportsBanAttestations { get; init; }

    public bool SupportsAttestationRefreshRequests { get; init; }

    public DateTimeOffset SeenAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string GetSigningPayload() =>
        JsonSerializer.Serialize(new
        {
            OriginId,
            OriginName,
            PublicEndpoint,
            ServerCount,
            DirectoryListed,
            Region,
            Website,
            Version,
            PublicKeyPem,
            Signature = (string?)null,
            SeenAtUtc
        }, DragnetJson.Options);
}

public sealed record DragnetHeartbeatRequest
{
    public required DragnetPeerInfo Sender { get; init; }

    public IReadOnlyList<DragnetPeerInfo> KnownPeers { get; init; } = [];

    public IReadOnlyList<DragnetEventEnvelope> Events { get; init; } = [];

    public IReadOnlyList<string> AcknowledgedEventIds { get; init; } = [];

    public IReadOnlyList<DragnetEvidenceUpdate> EvidenceUpdates { get; init; } = [];

    public IReadOnlyList<DragnetBanAttestation> BanAttestations { get; init; } = [];

    public IReadOnlyList<string> AttestationRefreshEventIds { get; init; } = [];
}

public sealed record DragnetHeartbeatResponse
{
    public required DragnetPeerInfo Receiver { get; init; }

    public IReadOnlyList<DragnetPeerInfo> KnownPeers { get; init; } = [];

    public IReadOnlyList<DragnetEventEnvelope> Events { get; init; } = [];

    public IReadOnlyList<string> AcknowledgedEventIds { get; init; } = [];

    public IReadOnlyList<DragnetEvidenceUpdate> EvidenceUpdates { get; init; } = [];

    public IReadOnlyList<DragnetBanAttestation> BanAttestations { get; init; } = [];

    public IReadOnlyList<string> AttestationRefreshEventIds { get; init; } = [];
}

public sealed record DragnetPeerRecord
{
    public required string OriginId { get; init; }

    public required string OriginName { get; set; }

    public required string Endpoint { get; set; }

    public DateTimeOffset FirstSeenUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastEventSentAtUtc { get; set; }

    public string? LastEventSentId { get; set; }

    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset? LastFailureAtUtc { get; set; }

    public DateTimeOffset? FirstFailureAtUtc { get; set; }

    public string? LastFailureMessage { get; set; }

    public DateTimeOffset? QuarantinedAtUtc { get; set; }

    public DateTimeOffset? LastRecoveryProbeAtUtc { get; set; }

    public long HeartbeatAttemptCount { get; set; }

    public long HeartbeatSuccessCount { get; set; }

    public long HeartbeatFailureCount { get; set; }

    public double? LastHeartbeatLatencyMs { get; set; }

    public double? AverageHeartbeatLatencyMs { get; set; }

    public DateTimeOffset? LastHeartbeatSucceededAtUtc { get; set; }

    public List<DragnetPeerTelemetryEvent> TelemetryEvents { get; set; } = [];

    public int ServerCount { get; set; }

    public bool DirectoryListed { get; set; }

    public string? Region { get; set; }

    public string? Website { get; set; }

    public string? Version { get; set; }

    public string? PublicKeyPem { get; set; }

    public string? Signature { get; set; }

    public bool IdentityVerified { get; set; }

    public DateTimeOffset? EndpointVerifiedAtUtc { get; set; }

    public DateTimeOffset? LastAdvertisedAtUtc { get; set; }

    public bool SupportsDeliveryAcknowledgements { get; set; }

    public bool SupportsEvidenceUpdates { get; set; }

    public bool SupportsBanAttestations { get; set; }

    public bool SupportsAttestationRefreshRequests { get; set; }

    public List<DragnetEventDeliveryRecord> EventDeliveries { get; set; } = [];

    public List<string> PendingAcknowledgementEventIds { get; set; } = [];

    public List<string> PendingAttestationRefreshEventIds { get; set; } = [];

    public DateTimeOffset? LastSyncVerifiedAtUtc { get; set; }

    public DateTimeOffset? LastResyncRequestedAtUtc { get; set; }

    public bool IsBootstrap { get; set; }
}

public enum DragnetPeerTelemetryEventType
{
    Connected,
    Failed,
    Quarantined,
    Recovered
}

public sealed record DragnetPeerTelemetryEvent
{
    public required DragnetPeerTelemetryEventType Type { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public string? Detail { get; init; }
    public double? LatencyMs { get; init; }
}

public sealed record DragnetEventDeliveryRecord
{
    public required string EventId { get; init; }
    public DateTimeOffset FirstSentAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSentAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int SendAttempts { get; set; } = 1;
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
}
