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

    public static DragnetPeerHealthAssessment Assess(
        DragnetPeerRecord peer,
        DateTimeOffset now,
        TimeSpan staleAfter,
        int pendingDeliveries = 0,
        DateTimeOffset? oldestPendingAtUtc = null)
    {
        var score = 100;
        var causes = new List<string>();

        if (IsQuarantined(peer))
        {
            score -= 70;
            causes.Add("Peer is quarantined");
        }
        else if (IsStale(peer, now, staleAfter))
        {
            score -= 45;
            causes.Add("Heartbeat is stale");
        }

        if (!string.IsNullOrWhiteSpace(peer.LastError))
        {
            score -= 25;
            causes.Add("Transport error is active");
        }
        else if (peer.ConsecutiveFailures > 0)
        {
            score -= Math.Min(20, peer.ConsecutiveFailures * 5);
            causes.Add($"{peer.ConsecutiveFailures} consecutive heartbeat failure(s)");
        }

        if (peer.HeartbeatAttemptCount > 0)
        {
            var successRate = peer.HeartbeatSuccessCount * 100d / peer.HeartbeatAttemptCount;
            if (successRate < 90)
            {
                score -= successRate < 60 ? 25 : successRate < 80 ? 15 : 8;
                causes.Add($"Heartbeat success rate is {successRate:0.#}%");
            }
        }

        if (peer.AverageHeartbeatLatencyMs is { } averageLatency)
        {
            if (averageLatency >= 2000)
            {
                score -= 20;
                causes.Add($"Average heartbeat latency is {averageLatency:0} ms");
            }
            else if (averageLatency >= 750)
            {
                score -= 10;
                causes.Add($"Average heartbeat latency is {averageLatency:0} ms");
            }
        }

        if (pendingDeliveries > 0)
        {
            score -= Math.Min(20, 5 + pendingDeliveries);
            causes.Add($"{pendingDeliveries} delivery acknowledgement(s) pending");
            if (oldestPendingAtUtc is { } oldest &&
                now - oldest >= TimeSpan.FromMinutes(10))
            {
                score -= 10;
                causes.Add($"Oldest acknowledgement is {(now - oldest).TotalMinutes:0} minutes old");
            }
        }

        score = Math.Clamp(score, 0, 100);
        return new DragnetPeerHealthAssessment
        {
            Score = score,
            State = score >= 90 ? "Healthy" : score >= 70 ? "Degraded" : score >= 40 ? "Unhealthy" : "Critical",
            Causes = causes
        };
    }
}

public sealed record DragnetPeerHealthAssessment
{
    public required int Score { get; init; }
    public required string State { get; init; }
    public IReadOnlyList<string> Causes { get; init; } = [];
}
