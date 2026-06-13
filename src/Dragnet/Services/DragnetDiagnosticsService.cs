using Dragnet.Configuration;
using Dragnet.Models;
using Dragnet.Transport;

namespace Dragnet.Services;

public static class DragnetDiagnosticsService
{
    public static DragnetDiagnosticsReport Create(
        DragnetConfiguration configuration,
        IReadOnlyList<DragnetPeerRecord> peers,
        IReadOnlyList<DragnetStoredEvent> events,
        DragnetUpdateStatus update,
        DateTimeOffset now)
    {
        var deliverableEventIds = events
            .Where(item => item.ReviewState is DragnetReviewState.ApprovedBan or DragnetReviewState.ApprovedLift)
            .Where(item => item.Event.EventType is DragnetEventType.BanLifted || !item.Event.IsExpired(now))
            .Select(item => item.Event.EventId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var peerReports = peers
            .Select(peer => CreatePeerReport(peer, deliverableEventIds, configuration.PeerStaleAfter, now))
            .OrderBy(report => report.HealthScore)
            .ThenBy(report => report.OriginName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var activeReports = peerReports.Where(report => report.Active).ToList();
        var networkScore = activeReports.Count == 0
            ? 0
            : (int)Math.Round(activeReports.Average(report => report.HealthScore));

        return new DragnetDiagnosticsReport
        {
            GeneratedAtUtc = now,
            Version = DragnetBuildInfo.Version,
            PublicEndpoint = configuration.PublicEndpoint,
            Update = new DragnetDiagnosticsUpdate
            {
                RunningVersion = update.CurrentVersion,
                LatestVersion = update.LatestVersion,
                InstalledVersion = update.InstalledVersion,
                UpdateAvailable = update.UpdateAvailable,
                RestartRequired = update.RestartRequired,
                LastCheckedAtUtc = update.CheckedAtUtc,
                CheckError = update.CheckError,
                InstallError = update.InstallError
            },
            Configuration = new DragnetSanitizedConfiguration
            {
                Enabled = configuration.Enabled,
                RequireHttps = configuration.RequireHttps,
                DirectoryListingEnabled = configuration.DirectoryListingEnabled,
                ImportApprovedEvents = configuration.ImportApprovedEvents,
                NotificationsEnabled = configuration.NotificationsEnabled,
                UpdateCheckEnabled = configuration.UpdateCheckEnabled,
                AutoUpdateEnabled = configuration.AutoUpdateEnabled,
                PeerHeartbeatInterval = configuration.PeerHeartbeatInterval,
                PeerStaleAfter = configuration.PeerStaleAfter,
                PeerFailureThreshold = configuration.PeerFailureThreshold,
                PeerQuarantineAfter = configuration.PeerQuarantineAfter,
                QuarantinedPeerProbeInterval = configuration.QuarantinedPeerProbeInterval,
                BootstrapPeerCount = configuration.BootstrapPeers.Count(peer => peer.Enabled),
                TrustedOriginCount = configuration.TrustedOrigins.Count
            },
            NetworkHealthScore = networkScore,
            NetworkHealthState = HealthState(networkScore, activeReports.Count),
            ActivePeerCount = activeReports.Count,
            TotalPeerCount = peerReports.Count,
            DeliverableEventCount = deliverableEventIds.Count,
            PendingDeliveryCount = peerReports.Sum(report => report.PendingDeliveryCount),
            Peers = peerReports
        };
    }

    private static DragnetPeerDiagnostics CreatePeerReport(
        DragnetPeerRecord peer,
        IReadOnlySet<string> deliverableEventIds,
        TimeSpan staleAfter,
        DateTimeOffset now)
    {
        var pending = (peer.EventDeliveries ?? [])
            .Where(delivery =>
                delivery.AcknowledgedAtUtc is null &&
                deliverableEventIds.Contains(delivery.EventId))
            .OrderBy(delivery => delivery.FirstSentAtUtc)
            .ToList();
        var acknowledged = (peer.EventDeliveries ?? []).Count(delivery =>
            delivery.AcknowledgedAtUtc is not null &&
            deliverableEventIds.Contains(delivery.EventId));
        var assessment = DragnetPeerHealth.Assess(
            peer,
            now,
            staleAfter,
            pending.Count,
            pending.FirstOrDefault()?.FirstSentAtUtc);
        double? successRate = peer.HeartbeatAttemptCount == 0
            ? null
            : peer.HeartbeatSuccessCount * 100d / peer.HeartbeatAttemptCount;

        return new DragnetPeerDiagnostics
        {
            OriginId = peer.OriginId,
            OriginName = peer.OriginName,
            Endpoint = peer.Endpoint,
            Version = peer.Version,
            Active = DragnetPeerHealth.IsActive(peer, now, staleAfter),
            HealthScore = assessment.Score,
            HealthState = assessment.State,
            HealthCauses = assessment.Causes,
            HeartbeatAttemptCount = peer.HeartbeatAttemptCount,
            HeartbeatSuccessCount = peer.HeartbeatSuccessCount,
            HeartbeatFailureCount = peer.HeartbeatFailureCount,
            HeartbeatSuccessRate = successRate is null ? null : Math.Round(successRate.Value, 1),
            LastHeartbeatLatencyMs = peer.LastHeartbeatLatencyMs,
            AverageHeartbeatLatencyMs = peer.AverageHeartbeatLatencyMs,
            LastHeartbeatSucceededAtUtc = peer.LastHeartbeatSucceededAtUtc,
            LastSeenUtc = peer.LastSeenUtc,
            LastFailureAtUtc = peer.LastFailureAtUtc,
            LastFailureMessage = peer.LastFailureMessage,
            ConsecutiveFailures = peer.ConsecutiveFailures,
            QuarantinedAtUtc = peer.QuarantinedAtUtc,
            AcknowledgedDeliveryCount = acknowledged,
            PendingDeliveryCount = pending.Count,
            OldestPendingDeliveryAtUtc = pending.FirstOrDefault()?.FirstSentAtUtc,
            RecentTelemetry = (peer.TelemetryEvents ?? [])
                .OrderByDescending(item => item.OccurredAtUtc)
                .Take(20)
                .ToList()
        };
    }

    private static string HealthState(int score, int activePeerCount) =>
        activePeerCount == 0
            ? "No active peers"
            : score >= 90
                ? "Healthy"
                : score >= 70
                    ? "Degraded"
                    : score >= 40
                        ? "Unhealthy"
                        : "Critical";
}

public sealed record DragnetDiagnosticsReport
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required string Version { get; init; }
    public string? PublicEndpoint { get; init; }
    public required DragnetDiagnosticsUpdate Update { get; init; }
    public required DragnetSanitizedConfiguration Configuration { get; init; }
    public required int NetworkHealthScore { get; init; }
    public required string NetworkHealthState { get; init; }
    public required int ActivePeerCount { get; init; }
    public required int TotalPeerCount { get; init; }
    public required int DeliverableEventCount { get; init; }
    public required int PendingDeliveryCount { get; init; }
    public IReadOnlyList<DragnetPeerDiagnostics> Peers { get; init; } = [];
}

public sealed record DragnetDiagnosticsUpdate
{
    public required string RunningVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? InstalledVersion { get; init; }
    public required bool UpdateAvailable { get; init; }
    public required bool RestartRequired { get; init; }
    public DateTimeOffset? LastCheckedAtUtc { get; init; }
    public string? CheckError { get; init; }
    public string? InstallError { get; init; }
}

public sealed record DragnetSanitizedConfiguration
{
    public required bool Enabled { get; init; }
    public required bool RequireHttps { get; init; }
    public required bool DirectoryListingEnabled { get; init; }
    public required bool ImportApprovedEvents { get; init; }
    public required bool NotificationsEnabled { get; init; }
    public required bool UpdateCheckEnabled { get; init; }
    public required bool AutoUpdateEnabled { get; init; }
    public required TimeSpan PeerHeartbeatInterval { get; init; }
    public required TimeSpan PeerStaleAfter { get; init; }
    public required int PeerFailureThreshold { get; init; }
    public required TimeSpan PeerQuarantineAfter { get; init; }
    public required TimeSpan QuarantinedPeerProbeInterval { get; init; }
    public required int BootstrapPeerCount { get; init; }
    public required int TrustedOriginCount { get; init; }
}

public sealed record DragnetPeerDiagnostics
{
    public required string OriginId { get; init; }
    public required string OriginName { get; init; }
    public required string Endpoint { get; init; }
    public string? Version { get; init; }
    public required bool Active { get; init; }
    public required int HealthScore { get; init; }
    public required string HealthState { get; init; }
    public IReadOnlyList<string> HealthCauses { get; init; } = [];
    public required long HeartbeatAttemptCount { get; init; }
    public required long HeartbeatSuccessCount { get; init; }
    public required long HeartbeatFailureCount { get; init; }
    public double? HeartbeatSuccessRate { get; init; }
    public double? LastHeartbeatLatencyMs { get; init; }
    public double? AverageHeartbeatLatencyMs { get; init; }
    public DateTimeOffset? LastHeartbeatSucceededAtUtc { get; init; }
    public required DateTimeOffset LastSeenUtc { get; init; }
    public DateTimeOffset? LastFailureAtUtc { get; init; }
    public string? LastFailureMessage { get; init; }
    public required int ConsecutiveFailures { get; init; }
    public DateTimeOffset? QuarantinedAtUtc { get; init; }
    public required int AcknowledgedDeliveryCount { get; init; }
    public required int PendingDeliveryCount { get; init; }
    public DateTimeOffset? OldestPendingDeliveryAtUtc { get; init; }
    public IReadOnlyList<DragnetPeerTelemetryEvent> RecentTelemetry { get; init; } = [];
}
