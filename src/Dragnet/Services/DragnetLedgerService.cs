using System.Net;
using System.Text;
using Dragnet.Configuration;
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

    public DragnetLedgerService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        Func<int> localServerCount)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _localServerCount = localServerCount;
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
        var bans = events
            .Where(item => item.Event.EventType is DragnetEventType.BanCreated)
            .Select(item =>
            {
                var envelope = item.Event;
                var lifted = lifts.Any(lift =>
                    lift.OriginId.Equals(envelope.OriginId, StringComparison.OrdinalIgnoreCase) &&
                    lift.Iw4mAdminPenaltyId == envelope.Iw4mAdminPenaltyId &&
                    lift.PlayerNetworkId.Equals(envelope.PlayerNetworkId, StringComparison.OrdinalIgnoreCase) &&
                    lift.CreatedAtUtc >= envelope.CreatedAtUtc);
                var status = lifted
                    ? "Lifted"
                    : envelope.IsExpired(now)
                        ? "Expired"
                        : "Active";
                var attestations = (item.BanAttestations ?? [])
                    .OrderByDescending(attestation => attestation.Status)
                    .ThenBy(attestation => attestation.NetworkName)
                    .Select(attestation => new DragnetLedgerAttestation
                    {
                        NetworkOriginId = attestation.NetworkOriginId,
                        NetworkName = attestation.NetworkName,
                        PublicEndpoint = NormalizeHttpsUrl(attestation.PublicEndpoint),
                        ServerCount = attestation.ServerCount,
                        ServerNames = attestation.ServerNames,
                        Status = attestation.Status.ToString(),
                        UpdatedAtUtc = attestation.UpdatedAtUtc
                    })
                    .ToList();
                return new DragnetLedgerBan
                {
                    EventId = envelope.EventId,
                    PlayerName = envelope.PlayerName,
                    PlayerNetworkId = envelope.PlayerNetworkId,
                    PlayerGame = envelope.PlayerGame,
                    Reason = envelope.Reason,
                    OriginName = envelope.OriginName,
                    OriginServerName = envelope.OriginServerName,
                    PenaltyKind = envelope.PenaltyKind.ToString(),
                    CreatedAtUtc = envelope.CreatedAtUtc,
                    ExpiresAtUtc = envelope.ExpiresAtUtc,
                    Status = status,
                    EvidenceUrl = NormalizeHttpsUrl(item.EvidenceUpdate?.EvidenceUrl ?? envelope.EvidenceUrl),
                    AcceptedNetworkCount = attestations.Count,
                    EnforcedNetworkCount = attestations.Count(attestation =>
                        attestation.Status == DragnetBanCoverageStatus.Enforced.ToString()),
                    EnforcedServerCount = attestations
                        .Where(attestation => attestation.Status == DragnetBanCoverageStatus.Enforced.ToString())
                        .Sum(attestation => attestation.ServerCount),
                    Attestations = attestations
                };
            })
            .OrderByDescending(ban => ban.CreatedAtUtc)
            .ToList();

        return new DragnetLedgerSnapshot
        {
            GeneratedAtUtc = now,
            DragnetVersion = DragnetBuildInfo.Version,
            KnownNetworkCount = peers.Select(peer => peer.OriginId)
                .Append("local")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            KnownServerCount = Math.Max(0, _localServerCount()) +
                               peers.Sum(peer => Math.Max(0, peer.ServerCount)),
            Bans = bans
        };
    }

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
                ban.EventId.Equals(selectedEventId, StringComparison.OrdinalIgnoreCase))
            : null;
        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine("<title>Dragnet Public Ban Ledger</title>");
        html.AppendLine("<style>");
        html.AppendLine(":root{color-scheme:dark;--bg:#0b0d10;--surface:#12161b;--line:#29313a;--text:#edf1f5;--muted:#9aa6b2;--accent:#45a3ff;--good:#52d273;--warn:#f0b84b;--bad:#ff6b6b}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px system-ui,sans-serif;letter-spacing:0}a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}.wrap{max-width:1280px;margin:auto;padding:24px}.top{display:flex;justify-content:space-between;gap:20px;align-items:end;border-bottom:1px solid var(--line);padding-bottom:20px}.brand{font-size:28px;font-weight:700}.sub,.muted{color:var(--muted)}.metrics{display:flex;gap:24px;flex-wrap:wrap}.metric strong{display:block;font-size:22px}.tools{display:flex;gap:10px;margin:20px 0}.tools input{width:min(420px,100%);background:var(--surface);border:1px solid var(--line);color:var(--text);padding:10px 12px}.tools button{background:var(--accent);border:0;color:#06111c;padding:10px 16px;font-weight:700;cursor:pointer}.table{overflow:auto;border:1px solid var(--line)}table{width:100%;border-collapse:collapse;min-width:900px}th,td{padding:12px;text-align:left;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-size:12px;text-transform:uppercase;background:var(--surface)}tr:hover td{background:#10151a}.status{font-weight:700}.Active{color:var(--bad)}.Expired,.Lifted{color:var(--muted)}.coverage{color:var(--good)}.detail{margin-top:24px;border-top:2px solid var(--accent);background:var(--surface);padding:20px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}.field{border-bottom:1px solid var(--line);padding-bottom:10px}.field label{display:block;color:var(--muted);font-size:12px;margin-bottom:4px}.attest{margin-top:18px}.attest-row{display:grid;grid-template-columns:2fr 1fr 1fr 1.5fr;gap:12px;padding:10px 0;border-bottom:1px solid var(--line)}.Queued{color:var(--warn)}.Enforced{color:var(--good)}.Accepted{color:var(--accent)}@media(max-width:760px){.wrap{padding:16px}.top{align-items:start;flex-direction:column}.grid{grid-template-columns:1fr}.attest-row{grid-template-columns:1fr 1fr}.brand{font-size:23px}}</style></head><body><main class=\"wrap\">");
        html.Append("<header class=\"top\"><div><div class=\"brand\">Dragnet Public Ban Ledger</div><div class=\"sub\">Signed, peer-to-peer ban coverage across participating IW4MAdmin networks.</div></div><div class=\"metrics\">");
        AppendMetric(html, snapshot.Bans.Count, "shared bans");
        AppendMetric(html, snapshot.KnownNetworkCount, "known networks");
        AppendMetric(html, snapshot.Bans.Count(ban => ban.Status == "Active"), "active bans");
        html.Append("</div></header>");
        html.Append("<form class=\"tools\" method=\"get\" action=\"/dragnet/ledger\"><input name=\"q\" value=\"");
        html.Append(Encode(search));
        html.Append("\" placeholder=\"Search player, network ID, reason, or origin\"><button type=\"submit\">Search</button></form>");
        html.AppendLine("<div class=\"table\"><table><thead><tr><th>Player</th><th>Origin</th><th>Type</th><th>Status</th><th>Accepted</th><th>Enforced servers</th><th>Issued</th></tr></thead><tbody>");
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
            html.Append("</td><td class=\"coverage\">");
            html.Append(Encode($"{ban.AcceptedNetworkCount} / {snapshot.KnownNetworkCount} known"));
            html.Append("</td><td>");
            html.Append(ban.EnforcedServerCount);
            html.Append("</td><td>");
            html.Append(Encode($"{ban.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC"));
            html.AppendLine("</td></tr>");
        }
        if (filtered.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"7\" class=\"muted\">No bans match this search.</td></tr>");
        }
        html.AppendLine("</tbody></table></div>");
        if (selected is not null)
        {
            AppendDetail(html, selected, snapshot.KnownNetworkCount);
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
        DragnetLedgerBan ban,
        int knownNetworkCount)
    {
        html.Append("<section class=\"detail\"><h2>");
        html.Append(Encode(ban.PlayerName));
        html.Append("</h2><div class=\"grid\">");
        AppendField(html, "Reason", ban.Reason);
        AppendField(html, "Network ID", $"{ban.PlayerNetworkId} {ban.PlayerGame}".Trim());
        AppendField(html, "Origin", $"{ban.OriginName} / {ban.OriginServerName}");
        AppendField(html, "Status", ban.Status);
        AppendField(html, "Accepted coverage", $"{ban.AcceptedNetworkCount} of {knownNetworkCount} known networks");
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
        html.Append("<div class=\"attest\"><h3>Network acceptance and enforcement</h3>");
        if (ban.Attestations.Count == 0)
        {
            html.Append("<p class=\"muted\">No upgraded network has published an acceptance attestation yet.</p>");
        }
        else
        {
            html.Append("<div class=\"attest-row muted\"><strong>Network</strong><strong>Status</strong><strong>Servers</strong><strong>Updated</strong></div>");
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
                html.Append("</div><div>");
                html.Append(attestation.ServerCount);
                html.Append("</div><div>");
                html.Append(Encode($"{attestation.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC"));
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
    public required int AcceptedNetworkCount { get; init; }
    public required int EnforcedNetworkCount { get; init; }
    public required int EnforcedServerCount { get; init; }
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
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
