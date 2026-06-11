using System.Net;
using System.Text;
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
        var lifted = groupedEvents.Any(item => lifts.Any(lift =>
            lift.OriginId.Equals(item.Event.OriginId, StringComparison.OrdinalIgnoreCase) &&
            lift.Iw4mAdminPenaltyId == item.Event.Iw4mAdminPenaltyId &&
            lift.PlayerNetworkId.Equals(item.Event.PlayerNetworkId, StringComparison.OrdinalIgnoreCase) &&
            lift.CreatedAtUtc >= item.Event.CreatedAtUtc));
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
                    PublicEndpoint = NormalizeHttpsUrl(attestation?.PublicEndpoint ?? network.Endpoint),
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
            Reason = envelope.Reason,
            OriginName = envelope.OriginName,
            OriginServerName = envelope.OriginServerName,
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
        var attestationNetworks = events
            .SelectMany(item => item.BanAttestations ?? [])
            .Select(attestation => new DragnetLedgerNetwork(
                attestation.NetworkOriginId,
                attestation.NetworkName,
                attestation.PublicEndpoint,
                attestation.ServerCount,
                "Unavailable",
                null));
        var roster = peers
            .Where(peer => !string.IsNullOrWhiteSpace(peer.OriginId))
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
            .Concat(attestationNetworks)
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

    public async Task<string> RenderHtmlAsync(
        string? selectedEventId,
        string? search,
        CancellationToken token)
    {
        var snapshot = await GetSnapshotAsync(token);
        search = search?.Trim();
        var filtered = string.IsNullOrWhiteSpace(search)
            ? snapshot.Bans
            : snapshot.Bans.Where(ban =>
                    ban.PlayerName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    ban.PlayerNetworkId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    ban.Reason.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    ban.OriginName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        var selected = !string.IsNullOrWhiteSpace(selectedEventId)
            ? snapshot.Bans.FirstOrDefault(ban =>
                ban.EventId.Equals(selectedEventId, StringComparison.OrdinalIgnoreCase) ||
                ban.EventIds.Contains(selectedEventId, StringComparer.OrdinalIgnoreCase))
            : null;
        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<title>Dragnet Public Ban Ledger</title>");
        html.AppendLine("<style>");
        html.AppendLine(":root{color-scheme:dark;--bg:#0b0d10;--surface:#12161b;--line:#29313a;--text:#edf1f5;--muted:#9aa6b2;--accent:#45a3ff;--good:#52d273;--warn:#f0b84b;--bad:#ff6b6b}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px system-ui,sans-serif;letter-spacing:0}a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}.wrap{max-width:1280px;margin:auto;padding:24px}.top{display:flex;justify-content:space-between;gap:20px;align-items:end;border-bottom:1px solid var(--line);padding-bottom:20px}.brand{font-size:28px;font-weight:700}.sub,.muted{color:var(--muted)}.metrics{display:flex;gap:24px;flex-wrap:wrap}.metric strong{display:block;font-size:22px}.tools{display:flex;gap:10px;margin:20px 0}.tools input{width:min(420px,100%);background:var(--surface);border:1px solid var(--line);color:var(--text);padding:10px 12px}.tools button{background:var(--accent);border:0;color:#06111c;padding:10px 16px;font-weight:700;cursor:pointer}.table{overflow:auto;border:1px solid var(--line)}table{width:100%;border-collapse:collapse;min-width:1000px}th,td{padding:12px;text-align:left;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-size:12px;text-transform:uppercase;background:var(--surface)}tr:hover td{background:#10151a}.status{font-weight:700}.Active{color:var(--bad)}.Expired,.Lifted,.Unreported,.Unavailable{color:var(--muted)}.coverage,.Healthy,.Enforced{color:var(--good)}.Degraded,.Queued,.stale{color:var(--warn)}.Accepted{color:var(--accent)}.detail{margin-top:24px;border-top:2px solid var(--accent);background:var(--surface);padding:20px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}.field{border-bottom:1px solid var(--line);padding-bottom:10px}.field label{display:block;color:var(--muted);font-size:12px;margin-bottom:4px}.attest{margin-top:18px}.attest-row{display:grid;grid-template-columns:2fr 1fr 1fr 1fr 1.5fr;gap:12px;padding:10px 0;border-bottom:1px solid var(--line)}@media(max-width:760px){.wrap{padding:16px}.top{align-items:start;flex-direction:column}.grid{grid-template-columns:1fr}.attest-row{grid-template-columns:1fr 1fr}.brand{font-size:23px}}</style></head><body><main class=\"wrap\">");
        html.Append("<header class=\"top\"><div><div class=\"brand\">Dragnet Public Ban Ledger</div><div class=\"sub\">Signed, peer-to-peer ban coverage across participating IW4MAdmin networks.</div></div><div class=\"metrics\">");
        AppendMetric(html, snapshot.Bans.Count, "shared bans");
        AppendMetric(html, snapshot.KnownNetworkCount, "known networks");
        AppendMetric(html, snapshot.Bans.Count(ban => ban.Status == "Active"), "active bans");
        html.Append("</div></header>");
        html.Append("<form class=\"tools\" method=\"get\" action=\"/dragnet/ledger\"><input name=\"q\" value=\"");
        html.Append(Encode(search));
        html.Append("\" placeholder=\"Search player, network ID, reason, or origin\"><button type=\"submit\">Search</button></form>");
        html.AppendLine("<div class=\"table\"><table><thead><tr><th>Player</th><th>Origin</th><th>Type</th><th>Status</th><th>Reconciliation</th><th>Accepted</th><th>Enforced servers</th><th>Issued</th></tr></thead><tbody>");
        foreach (var ban in filtered)
        {
            html.Append("<tr><td><a href=\"/dragnet/ledger?id=");
            html.Append(Uri.EscapeDataString(ban.EventId));
            html.Append("\"><strong>");
            html.Append(Encode(ban.PlayerName));
            html.Append("</strong></a><div class=\"muted\">");
            html.Append(Encode($"{ban.PlayerNetworkId} {ban.PlayerGame}".Trim()));
            html.Append("</div></td><td>");
            html.Append(Encode(ban.OriginName));
            html.Append("<div class=\"muted\">");
            html.Append(Encode(ban.OriginServerName));
            html.Append("</div></td><td>");
            html.Append(Encode(ban.PenaltyKind));
            html.Append("</td><td class=\"status ");
            html.Append(Encode(ban.Status));
            html.Append("\">");
            html.Append(Encode(ban.Status));
            html.Append("</td><td>");
            html.Append(Encode(ban.ReconciliationStatus));
            if (ban.UnavailableNetworkCount > 0 || ban.StaleReportCount > 0)
            {
                html.Append("<div class=\"muted\">");
                html.Append(Encode($"{ban.UnavailableNetworkCount} unavailable, {ban.StaleReportCount} stale"));
                html.Append("</div>");
            }
            html.Append("</td><td class=\"coverage\">");
            html.Append(Encode($"{ban.AcceptedNetworkCount} / {ban.EligibleNetworkCount} peers"));
            html.Append("</td><td>");
            html.Append(ban.EnforcedServerCount);
            html.Append("</td><td>");
            html.Append(Encode($"{ban.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC"));
            html.AppendLine("</td></tr>");
        }
        if (filtered.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"8\" class=\"muted\">No bans match this search.</td></tr>");
        }
        html.AppendLine("</tbody></table></div>");
        if (selected is not null)
        {
            AppendDetail(html, selected);
        }
        html.Append("<footer class=\"muted\" style=\"margin-top:24px\">Generated ");
        html.Append(Encode($"{snapshot.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC"));
        html.Append(" by Dragnet ");
        html.Append(Encode(snapshot.DragnetVersion));
        html.AppendLine(". Acceptance and enforcement are independently signed statements from participating networks.</footer></main></body></html>");
        return html.ToString();
    }

    private static void AppendDetail(
        StringBuilder html,
        DragnetLedgerBan ban)
    {
        html.Append("<section class=\"detail\"><h2>");
        html.Append(Encode(ban.PlayerName));
        html.Append("</h2><div class=\"grid\">");
        AppendField(html, "Reason", ban.Reason);
        AppendField(html, "Network ID", $"{ban.PlayerNetworkId} {ban.PlayerGame}".Trim());
        AppendField(html, "Origin", $"{ban.OriginName} / {ban.OriginServerName}");
        AppendField(html, "Status", ban.Status);
        AppendField(html, "Reconciliation", ban.ReconciliationStatus);
        AppendField(html, "Peer acceptance", $"{ban.AcceptedNetworkCount} of {ban.EligibleNetworkCount} known peer networks");
        AppendField(html, "Unreported", ban.UnreportedNetworkCount.ToString());
        AppendField(html, "Unavailable", ban.UnavailableNetworkCount.ToString());
        if (ban.DuplicateEventCount > 0)
        {
            AppendField(html, "Consolidated events", $"{ban.EventIds.Count} event records");
        }
        AppendField(html, "Created", $"{ban.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        AppendField(html, "Expires", ban.ExpiresAtUtc is null
            ? "Permanent"
            : $"{ban.ExpiresAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        html.Append("</div>");
        if (!string.IsNullOrWhiteSpace(ban.EvidenceUrl))
        {
            html.Append("<p><strong>Evidence:</strong> <a target=\"_blank\" rel=\"noopener noreferrer\" href=\"");
            html.Append(Encode(ban.EvidenceUrl));
            html.Append("\">");
            html.Append(Encode(ban.EvidenceUrl));
            html.Append("</a></p>");
        }
        html.Append("<div class=\"attest\"><h3>Peer network acceptance and enforcement</h3>");
        if (ban.Attestations.Count == 0)
        {
            html.Append("<p class=\"muted\">No peer network has published an acceptance attestation yet. Origin enforcement is implicit.</p>");
        }
        else
        {
            html.Append("<div class=\"attest-row muted\"><strong>Network</strong><strong>Report</strong><strong>Availability</strong><strong>Servers</strong><strong>Updated</strong></div>");
            foreach (var attestation in ban.Attestations)
            {
                html.Append("<div class=\"attest-row\"><div>");
                if (!string.IsNullOrWhiteSpace(attestation.PublicEndpoint))
                {
                    html.Append("<a href=\"");
                    html.Append(Encode(attestation.PublicEndpoint));
                    html.Append("/ledger\" target=\"_blank\" rel=\"noopener noreferrer\">");
                    html.Append(Encode(attestation.NetworkName));
                html.Append("</a>");
                }
                else
                {
                    html.Append(Encode(attestation.NetworkName));
                }
                if (attestation.ServerNames.Count > 0)
                {
                    html.Append("<div class=\"muted\">");
                    html.Append(Encode(string.Join(", ", attestation.ServerNames)));
                    html.Append("</div>");
                }
                html.Append("</div><div class=\"");
                html.Append(Encode(attestation.Status));
                html.Append("\">");
                html.Append(Encode(attestation.Status));
                if (attestation.IsStale)
                {
                    html.Append("<div class=\"stale\">Stale report</div>");
                }
                html.Append("</div><div class=\"");
                html.Append(Encode(attestation.Availability));
                html.Append("\">");
                html.Append(Encode(attestation.Availability));
                html.Append("</div><div>");
                html.Append(attestation.ServerCount);
                html.Append("</div><div>");
                html.Append(attestation.UpdatedAtUtc is { } updatedAt
                    ? Encode($"{updatedAt:yyyy-MM-dd HH:mm} UTC")
                    : "<span class=\"muted\">Never reported</span>");
                html.Append("</div></div>");
            }
        }
        html.Append("</div></section>");
    }

    private static void AppendMetric(StringBuilder html, int value, string label)
    {
        html.Append("<div class=\"metric\"><strong>");
        html.Append(value);
        html.Append("</strong><span class=\"muted\">");
        html.Append(Encode(label));
        html.Append("</span></div>");
    }

    private static void AppendField(StringBuilder html, string label, string value)
    {
        html.Append("<div class=\"field\"><label>");
        html.Append(Encode(label));
        html.Append("</label><div>");
        html.Append(Encode(value));
        html.Append("</div></div>");
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string? NormalizeHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(uri.Host)
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
    public required string OriginName { get; init; }
    public required string OriginServerName { get; init; }
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
