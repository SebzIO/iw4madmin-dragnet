using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Storage;
using Dragnet.Transport;

namespace Dragnet.Services;

public sealed class DragnetLedgerService
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly Func<int> _localServerCount;
    private readonly DragnetIdentityDocument _identity;

    public DragnetLedgerService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        Func<int> localServerCount,
        DragnetIdentityDocument identity)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _localServerCount = localServerCount;
        _identity = identity;
    }

    public async Task<DragnetLedgerSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        var events = await _eventStore.ListAsync(token);
        var peers = await _peerStore.ListAsync(token);
        var now = DateTimeOffset.UtcNow;
        var lifts = events
            .Where(item => item.Event.EventType is DragnetEventType.BanLifted)
            .Select(item => item.Event)
            .ToList();
        var networks = BuildNetworkRoster(peers, events, now);
        var knownNetworkCount = networks.Count;
        var bans = events
            .Where(item => item.Event.EventType is DragnetEventType.BanCreated)
            .GroupBy(item => CreateCanonicalKey(item.Event), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildLedgerBan(group.ToList(), lifts, networks, now))
            .OrderByDescending(ban => ban.CreatedAtUtc)
            .ToList();

        return new DragnetLedgerSnapshot
        {
            GeneratedAtUtc = now,
            DragnetVersion = DragnetBuildInfo.Version,
            KnownNetworkCount = knownNetworkCount,
            KnownServerCount = Math.Max(0, _localServerCount()) +
                               peers.Sum(peer => Math.Max(0, peer.ServerCount)),
            Bans = bans
        };
    }

    private DragnetLedgerBan BuildLedgerBan(
        IReadOnlyList<DragnetStoredEvent> groupedEvents,
        IReadOnlyList<DragnetEventEnvelope> lifts,
        IReadOnlyList<DragnetLedgerNetwork> networks,
        DateTimeOffset now)
    {
        var newest = groupedEvents
            .OrderByDescending(item => item.Event.CreatedAtUtc)
            .ThenByDescending(item => item.LastSeenUtc)
            .First();
        var envelope = newest.Event;
        var adminName = groupedEvents
            .OrderByDescending(item => item.Event.CreatedAtUtc)
            .ThenByDescending(item => item.LastSeenUtc)
            .Select(item => item.Event.AdminName?.Trim())
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        var lifted = groupedEvents.Any(item =>
            lifts.Any(lift => DragnetEventRelationships.LiftMatchesBan(lift, item.Event)));
        var status = lifted
            ? "Lifted"
            : groupedEvents.All(item => item.Event.IsExpired(now))
                ? "Expired"
                : "Active";
        var latestAttestations = groupedEvents
            .SelectMany(item => item.BanAttestations ?? [])
            .Where(attestation => !attestation.NetworkOriginId.Equals(
                envelope.OriginId,
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(attestation => attestation.NetworkOriginId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(attestation => attestation.UpdatedAtUtc).First())
            .ToDictionary(
                attestation => attestation.NetworkOriginId,
                StringComparer.OrdinalIgnoreCase);
        var coverage = networks
            .Where(network => !network.OriginId.Equals(
                envelope.OriginId,
                StringComparison.OrdinalIgnoreCase))
            .Select(network =>
            {
                latestAttestations.TryGetValue(network.OriginId, out var attestation);
                var staleReport = attestation is not null &&
                                  attestation.Status is DragnetBanCoverageStatus.Queued &&
                                  (now - attestation.UpdatedAtUtc > MaxCoverageReportAge() ||
                                   network.LastSeenUtc is { } seenAt &&
                                   seenAt - attestation.UpdatedAtUtc > MaxCoverageReportAge());
                return new DragnetLedgerAttestation
                {
                    NetworkOriginId = network.OriginId,
                    NetworkName = attestation?.NetworkName ?? network.Name,
                    PublicEndpoint = NormalizeEndpointUrl(attestation?.PublicEndpoint ?? network.Endpoint),
                    ServerCount = attestation?.ServerCount ?? network.ServerCount,
                    ServerNames = attestation?.ServerNames ?? [],
                    Status = attestation?.Status.ToString() ?? "Unreported",
                    Availability = network.Availability,
                    UpdatedAtUtc = attestation?.UpdatedAtUtc,
                    IsStale = staleReport
                };
            })
            .OrderBy(attestation => CoverageSort(attestation.Status))
            .ThenBy(attestation => attestation.NetworkName)
            .ToList();
        var reported = coverage.Where(item => item.Status != "Unreported").ToList();
        var evidence = groupedEvents
            .Select(item => new
            {
                Url = item.EvidenceUpdate?.EvidenceUrl ?? item.Event.EvidenceUrl,
                UpdatedAt = item.EvidenceUpdate?.CreatedAtUtc ?? item.Event.CreatedAtUtc
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => NormalizeHttpsUrl(item.Url))
            .FirstOrDefault(item => item is not null);
        var unavailableCount = coverage.Count(item => item.Availability == "Unavailable");
        var staleCount = coverage.Count(item => item.IsStale);
        var enforcedCount = reported.Count(item =>
            item.Status == DragnetBanCoverageStatus.Enforced.ToString());
        var reconciliationStatus = coverage.Count == 0 || enforcedCount == coverage.Count
            ? "Fully enforced"
            : unavailableCount > 0 || staleCount > 0
                ? "Needs attention"
                : reported.Count == 0
                    ? "Awaiting reports"
                    : "Partially propagated";

        return new DragnetLedgerBan
        {
            EventId = envelope.EventId,
            EventIds = groupedEvents.Select(item => item.Event.EventId).Distinct().ToList(),
            DuplicateEventCount = Math.Max(0, groupedEvents.Count - 1),
            PlayerName = envelope.PlayerName,
            PlayerNetworkId = envelope.PlayerNetworkId,
            PlayerGame = envelope.PlayerGame,
            Reason = string.IsNullOrWhiteSpace(envelope.PublicReason) ? envelope.Reason : envelope.PublicReason,
            OriginReason = envelope.Reason,
            PublicCategory = (envelope.PublicCategory ?? DragnetRiskClassifier.ClassifyCategory(envelope.Reason)).ToString(),
            RiskScore = DragnetRiskClassifier.Assess(envelope).Label,
            RiskSummary = DragnetRiskClassifier.Assess(envelope).Summary,
            OriginName = envelope.OriginName,
            OriginServerName = envelope.OriginServerName,
            AdminName = adminName,
            PenaltyKind = envelope.PenaltyKind.ToString(),
            CreatedAtUtc = groupedEvents.Min(item => item.Event.CreatedAtUtc),
            ExpiresAtUtc = groupedEvents.Any(item => item.Event.ExpiresAtUtc is null)
                ? null
                : groupedEvents.Max(item => item.Event.ExpiresAtUtc),
            Status = status,
            EvidenceUrl = evidence,
            EligibleNetworkCount = coverage.Count,
            AcceptedNetworkCount = reported.Count,
            EnforcedNetworkCount = enforcedCount,
            EnforcedServerCount = reported
                .Where(item => item.Status == DragnetBanCoverageStatus.Enforced.ToString())
                .Sum(item => item.ServerCount),
            UnreportedNetworkCount = coverage.Count(item => item.Status == "Unreported"),
            UnavailableNetworkCount = unavailableCount,
            StaleReportCount = staleCount,
            ReconciliationStatus = reconciliationStatus,
            Attestations = coverage
        };
    }

    private IReadOnlyList<DragnetLedgerNetwork> BuildNetworkRoster(
        IReadOnlyList<DragnetPeerRecord> peers,
        IReadOnlyList<DragnetStoredEvent> events,
        DateTimeOffset now)
    {
        var roster = peers
            .Where(peer =>
                !string.IsNullOrWhiteSpace(peer.OriginId) &&
                DragnetPeerHealth.IsActive(peer, now, _configuration.PeerStaleAfter))
            .Select(peer => new DragnetLedgerNetwork(
                peer.OriginId,
                peer.OriginName,
                peer.Endpoint,
                peer.ServerCount,
                !string.IsNullOrWhiteSpace(peer.LastError) || now - peer.LastSeenUtc > _configuration.PeerStaleAfter
                    ? "Unavailable"
                    : peer.ConsecutiveFailures > 0
                        ? "Degraded"
                        : "Healthy",
                peer.LastSeenUtc))
            .Append(new DragnetLedgerNetwork(
                _identity.OriginId,
                _identity.OriginName,
                _configuration.PublicEndpoint,
                Math.Max(0, _localServerCount()),
                "Healthy",
                now))
            .GroupBy(network => network.OriginId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(network => network.Availability == "Unavailable").First())
            .ToList();
        return roster;
    }

    private static string CreateCanonicalKey(DragnetEventEnvelope envelope) =>
        envelope.Iw4mAdminPenaltyId > 0
            ? $"{envelope.OriginId}:penalty:{envelope.Iw4mAdminPenaltyId}"
            : $"{envelope.OriginId}:event:{envelope.EventId}";

    private TimeSpan MaxCoverageReportAge() =>
        TimeSpan.FromTicks(Math.Max(
            TimeSpan.FromMinutes(10).Ticks,
            _configuration.PeerHeartbeatInterval.Ticks * 3));

    private static int CoverageSort(string status) => status switch
    {
        "Enforced" => 0,
        "Queued" => 1,
        "Accepted" => 2,
        _ => 3
    };

    private static string? NormalizeHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.ToString().TrimEnd('/')
            : null;

    private string? NormalizeEndpointUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        !string.IsNullOrWhiteSpace(uri.Host) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         (!_configuration.RequireHttps && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            ? uri.ToString().TrimEnd('/')
            : null;
}

public sealed record DragnetLedgerSnapshot
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required string DragnetVersion { get; init; }
    public required int KnownNetworkCount { get; init; }
    public required int KnownServerCount { get; init; }
    public IReadOnlyList<DragnetLedgerBan> Bans { get; init; } = [];
}

public sealed record DragnetLedgerBan
{
    public required string EventId { get; init; }
    public IReadOnlyList<string> EventIds { get; init; } = [];
    public required int DuplicateEventCount { get; init; }
    public required string PlayerName { get; init; }
    public required string PlayerNetworkId { get; init; }
    public string? PlayerGame { get; init; }
    public required string Reason { get; init; }
    public string? OriginReason { get; init; }
    public required string PublicCategory { get; init; }
    public required string RiskScore { get; init; }
    public required string RiskSummary { get; init; }
    public required string OriginName { get; init; }
    public required string OriginServerName { get; init; }
    public string? AdminName { get; init; }
    public required string PenaltyKind { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public required string Status { get; init; }
    public string? EvidenceUrl { get; init; }
    public required int EligibleNetworkCount { get; init; }
    public required int AcceptedNetworkCount { get; init; }
    public required int EnforcedNetworkCount { get; init; }
    public required int EnforcedServerCount { get; init; }
    public required int UnreportedNetworkCount { get; init; }
    public required int UnavailableNetworkCount { get; init; }
    public required int StaleReportCount { get; init; }
    public required string ReconciliationStatus { get; init; }
    public IReadOnlyList<DragnetLedgerAttestation> Attestations { get; init; } = [];
}

public sealed record DragnetLedgerAttestation
{
    public required string NetworkOriginId { get; init; }
    public required string NetworkName { get; init; }
    public string? PublicEndpoint { get; init; }
    public required int ServerCount { get; init; }
    public IReadOnlyList<string> ServerNames { get; init; } = [];
    public required string Status { get; init; }
    public required string Availability { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public required bool IsStale { get; init; }
}

internal sealed record DragnetLedgerNetwork(
    string OriginId,
    string Name,
    string? Endpoint,
    int ServerCount,
    string Availability,
    DateTimeOffset? LastSeenUtc);
