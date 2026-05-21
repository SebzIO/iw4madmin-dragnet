using Dragnet.Models;

namespace Dragnet.Transport;

public sealed record DragnetPeerInfo
{
    public required string OriginId { get; init; }

    public required string OriginName { get; init; }

    public string? PublicEndpoint { get; init; }

    public DateTimeOffset SeenAtUtc { get; init; } = DateTimeOffset.UtcNow;
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

    public string? LastError { get; set; }

    public bool IsBootstrap { get; set; }
}
