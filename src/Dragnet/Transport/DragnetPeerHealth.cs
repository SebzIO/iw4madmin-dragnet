namespace Dragnet.Transport;

public static class DragnetPeerHealth
{
    public static bool IsQuarantined(DragnetPeerRecord peer) =>
        peer.QuarantinedAtUtc is not null;

    public static bool IsStale(
        DragnetPeerRecord peer,
        DateTimeOffset now,
        TimeSpan staleAfter) =>
        now - peer.LastSeenUtc > staleAfter;

    public static bool IsActive(
        DragnetPeerRecord peer,
        DateTimeOffset now,
        TimeSpan staleAfter) =>
        !IsQuarantined(peer) &&
        string.IsNullOrWhiteSpace(peer.LastError) &&
        !IsStale(peer, now, staleAfter);
}
