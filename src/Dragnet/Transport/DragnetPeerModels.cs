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

    public DateTimeOffset SeenAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string GetSigningPayload() =>
        JsonSerializer.Serialize(this with { Signature = null }, DragnetJson.Options);
}

public sealed record DragnetHeartbeatRequest
{
    public required DragnetPeerInfo Sender { get; init; }

    public IReadOnlyList<DragnetPeerInfo> KnownPeers { get; init; } = [];

    public IReadOnlyList<DragnetEventEnvelope> Events { get; init; } = [];
}

public sealed record DragnetHeartbeatResponse
{
    public required DragnetPeerInfo Receiver { get; init; }

    public IReadOnlyList<DragnetPeerInfo> KnownPeers { get; init; } = [];

    public IReadOnlyList<DragnetEventEnvelope> Events { get; init; } = [];
}

public sealed record DragnetPeerRecord
{
    public required string OriginId { get; init; }

    public required string OriginName { get; set; }

    public required string Endpoint { get; set; }

    public DateTimeOffset FirstSeenUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastEventSentAtUtc { get; set; }

    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset? LastFailureAtUtc { get; set; }

    public string? LastFailureMessage { get; set; }

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

    public bool IsBootstrap { get; set; }
}
