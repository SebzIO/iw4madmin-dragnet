using System.Net;
using System.Text;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Storage;
using Dragnet.Transport;

namespace Dragnet.Services;

public sealed class DragnetNetworkProfileService
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetIdentityDocument _identity;
    private readonly Func<int> _localServerCount;

    public DragnetNetworkProfileService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetIdentityDocument identity,
        Func<int> localServerCount)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _identity = identity;
        _localServerCount = localServerCount;
    }

    public async Task<DragnetNetworkProfile?> GetAsync(string originId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(originId))
        {
            return null;
        }

        var events = await _eventStore.ListAsync(token);
        var peers = await _peerStore.ListAsync(token);
        var now = DateTimeOffset.UtcNow;
        var isLocal = originId.Equals(_identity.OriginId, StringComparison.OrdinalIgnoreCase);
        var peer = peers.FirstOrDefault(item =>
            item.OriginId.Equals(originId, StringComparison.OrdinalIgnoreCase));
        var originatedEvents = events
            .Where(item => item.Event.OriginId.Equals(originId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!isLocal && peer is null && originatedEvents.Count == 0)
        {
            return null;
        }

        var canonicalBans = originatedEvents
            .Where(item => item.Event.EventType is DragnetEventType.BanCreated)
            .GroupBy(item => CreateCanonicalKey(item.Event), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Event.CreatedAtUtc)
                .ThenByDescending(item => item.LastSeenUtc)
                .First())
            .OrderByDescending(item => item.Event.CreatedAtUtc)
            .ToList();
        var liftEvents = originatedEvents
            .Where(item => item.Event.EventType is DragnetEventType.BanLifted)
            .Select(item => item.Event)
            .ToList();
        var reviewedBans = canonicalBans.Count(item =>
            item.ReviewState is not (DragnetReviewState.PendingBan or DragnetReviewState.ExpiredBan));
        var approved = canonicalBans.Count(item =>
            item.ReviewState is DragnetReviewState.ApprovedBan);
        var denied = canonicalBans.Count(item =>
            item.ReviewState is DragnetReviewState.DeniedBan);
        var ignored = canonicalBans.Count(item =>
            item.ReviewState is DragnetReviewState.IgnoredBan);
        var pending = canonicalBans.Count(item =>
            item.ReviewState is DragnetReviewState.PendingBan);
        var evidenceCount = canonicalBans.Count(item =>
            !string.IsNullOrWhiteSpace(item.EvidenceUpdate?.EvidenceUrl ?? item.Event.EvidenceUrl));
        var eligibleNetworkIds = peers
            .Where(item => DragnetPeerHealth.IsActive(
                item,
                now,
                _configuration.PeerStaleAfter))
            .Select(item => item.OriginId)
            .Append(_identity.OriginId)
            .Where(id => !id.Equals(originId, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reportedSlots = 0;
        var enforcedSlots = 0;
        foreach (var ban in canonicalBans)
        {
            var latestReports = (ban.BanAttestations ?? [])
                .Where(attestation => !attestation.NetworkOriginId.Equals(
                    originId,
                    StringComparison.OrdinalIgnoreCase))
                .Where(attestation => eligibleNetworkIds.Contains(attestation.NetworkOriginId))
                .GroupBy(attestation => attestation.NetworkOriginId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.UpdatedAtUtc).First())
                .ToList();
            reportedSlots += latestReports.Count;
            enforcedSlots += latestReports.Count(item =>
                item.Status is DragnetBanCoverageStatus.Enforced);
        }

        var deliveryRecords = peer?.EventDeliveries ?? [];
        var acknowledgedDeliveries = deliveryRecords.Count(item =>
            item.AcknowledgedAtUtc is not null);
        var trust = _configuration.TrustedOrigins.FirstOrDefault(item =>
            item.OriginId.Equals(originId, StringComparison.OrdinalIgnoreCase));
        var firstEvent = originatedEvents
            .OrderBy(item => item.FirstSeenUtc)
            .FirstOrDefault();
        var identityName = isLocal
            ? _identity.OriginName
            : peer?.OriginName ?? firstEvent?.Event.OriginName ?? originId;
        var endpoint = isLocal
            ? _configuration.PublicEndpoint
            : peer?.Endpoint ?? firstEvent?.Event.OriginEndpoint;
        var lastSeen = isLocal ? now : peer?.LastSeenUtc ??
            originatedEvents.OrderByDescending(item => item.LastSeenUtc).FirstOrDefault()?.LastSeenUtc;
        var health = isLocal
            ? "Healthy"
            : peer is null
                ? "Historical"
                : DragnetPeerHealth.IsQuarantined(peer)
                    ? "Quarantined"
                    : !string.IsNullOrWhiteSpace(peer.LastError)
                    ? "Errored"
                    : now - peer.LastSeenUtc > _configuration.PeerStaleAfter
                        ? "Stale"
                        : peer.ConsecutiveFailures > 0
                            ? "Degraded"
                            : "Healthy";
        var coverageSlots = canonicalBans.Count * eligibleNetworkIds.Count;

        return new DragnetNetworkProfile
        {
            OriginId = originId,
            OriginName = identityName,
            Endpoint = NormalizeHttpsUrl(endpoint),
            Website = NormalizeHttpsUrl(isLocal ? _configuration.DirectoryWebsite : peer?.Website),
            Region = isLocal ? _configuration.DirectoryRegion : peer?.Region,
            Version = isLocal ? DragnetBuildInfo.Version : peer?.Version,
            ServerCount = isLocal ? Math.Max(0, _localServerCount()) : Math.Max(0, peer?.ServerCount ?? 0),
            Health = health,
            IsLocal = isLocal,
            IdentityVerified = isLocal || peer?.IdentityVerified == true,
            EndpointVerified = isLocal || peer?.EndpointVerifiedAtUtc is { } verifiedAt &&
                now - verifiedAt <= _configuration.PeerStaleAfter,
            FirstSeenUtc = isLocal ? null : peer?.FirstSeenUtc ?? firstEvent?.FirstSeenUtc,
            LastSeenUtc = lastSeen,
            CurrentFailureCount = peer?.ConsecutiveFailures ?? 0,
            LastFailureAtUtc = peer?.LastFailureAtUtc,
            TrustedByThisNetwork = isLocal || trust is not null,
            AutoApproveBans = trust?.AutoApproveBans == true,
            AutoApproveLifts = trust?.AutoApproveLifts == true,
            SubmittedBanCount = canonicalBans.Count,
            SubmittedLiftCount = liftEvents.Count,
            ActiveBanCount = canonicalBans.Count(item =>
                !item.Event.IsExpired(now) &&
                !liftEvents.Any(lift =>
                    DragnetEventRelationships.LiftMatchesBan(lift, item.Event))),
            EvidenceCount = evidenceCount,
            EvidenceRatePercent = Percent(evidenceCount, canonicalBans.Count),
            ReviewedBanCount = reviewedBans,
            ApprovedBanCount = approved,
            DeniedBanCount = denied,
            IgnoredBanCount = ignored,
            PendingBanCount = pending,
            ApprovalRatePercent = Percent(approved, reviewedBans),
            DenialRatePercent = Percent(denied, reviewedBans),
            IgnoreRatePercent = Percent(ignored, reviewedBans),
            EligibleCoverageSlots = coverageSlots,
            ReportedCoverageSlots = reportedSlots,
            EnforcedCoverageSlots = enforcedSlots,
            EnforcementCoveragePercent = Percent(enforcedSlots, coverageSlots),
            TrackedDeliveryCount = deliveryRecords.Count,
            AcknowledgedDeliveryCount = acknowledgedDeliveries,
            DeliveryAcknowledgementPercent = Percent(acknowledgedDeliveries, deliveryRecords.Count),
            SupportsDeliveryAcknowledgements = isLocal || peer?.SupportsDeliveryAcknowledgements == true,
            SupportsEvidenceUpdates = isLocal || peer?.SupportsEvidenceUpdates == true,
            SupportsBanAttestations = isLocal || peer?.SupportsBanAttestations == true,
            SupportsAttestationRefreshRequests = isLocal || peer?.SupportsAttestationRefreshRequests == true,
            RecentBans = canonicalBans.Take(20).Select(item => new DragnetNetworkProfileBan
            {
                EventId = item.Event.EventId,
                PlayerName = item.Event.PlayerName,
                Reason = item.Event.Reason,
                CreatedAtUtc = item.Event.CreatedAtUtc,
                ExpiresAtUtc = item.Event.ExpiresAtUtc,
                HasEvidence = !string.IsNullOrWhiteSpace(
                    item.EvidenceUpdate?.EvidenceUrl ?? item.Event.EvidenceUrl),
                ReviewState = item.ReviewState.ToString()
            }).ToList()
        };
    }

    public async Task<string?> RenderHtmlAsync(string originId, CancellationToken token)
    {
        var profile = await GetAsync(originId, token);
        if (profile is null)
        {
            return null;
        }

        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<title>");
        html.Append(Encode(profile.OriginName));
        html.AppendLine(" - Dragnet Network Profile</title>");
        html.AppendLine("<style>:root{color-scheme:dark;--bg:#0b0d10;--surface:#12161b;--surface2:#171d23;--line:#29313a;--text:#edf1f5;--muted:#9aa6b2;--accent:#45a3ff;--good:#52d273;--warn:#f0b84b;--bad:#ff6b6b}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px system-ui,sans-serif}.wrap{max-width:1180px;margin:auto;padding:24px}a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}.top{display:flex;justify-content:space-between;gap:20px;align-items:start}.brand{font-size:28px;font-weight:700}.sub,.muted{color:var(--muted)}.actions{display:flex;gap:8px;flex-wrap:wrap}.button{display:inline-flex;padding:10px 14px;background:var(--surface);border:1px solid var(--line);color:var(--text);font-weight:700}.button:hover{border-color:var(--accent);text-decoration:none}.status{display:inline-block;margin-top:10px;font-weight:700}.Healthy{color:var(--good)}.Degraded,.Stale,.Quarantined{color:var(--warn)}.Errored{color:var(--bad)}.Historical{color:var(--muted)}.metrics{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:10px;margin:22px 0}.metric,.panel{background:var(--surface);padding:16px}.metric strong{display:block;font-size:22px}.metric span{color:var(--muted)}.grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.panel h2{font-size:17px;margin:0 0 14px}.fields{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.field{background:var(--surface2);padding:12px}.field label{display:block;color:var(--muted);font-size:12px;margin-bottom:4px}.bar{height:8px;background:#252d35;margin-top:8px}.bar span{display:block;height:100%;background:var(--accent)}.bans{margin-top:12px;overflow:auto}.bans table{width:100%;border-collapse:collapse;min-width:760px}.bans th,.bans td{text-align:left;padding:11px 10px;vertical-align:top}.bans th{color:var(--muted);font-size:12px;text-transform:uppercase}.bans tr:hover td{background:var(--surface2)}.caps{display:flex;gap:8px;flex-wrap:wrap}.cap{background:var(--surface2);padding:7px 9px}.yes{color:var(--good)}.no{color:var(--muted)}footer{margin-top:22px}@media(max-width:800px){.top{flex-direction:column}.metrics{grid-template-columns:repeat(2,minmax(0,1fr))}.grid,.fields{grid-template-columns:1fr}.wrap{padding:16px}}@media(max-width:480px){.metrics{grid-template-columns:1fr}}</style></head><body><main class=\"wrap\">");
        html.Append("<header class=\"top\"><div><div class=\"brand\">");
        html.Append(Encode(profile.OriginName));
        html.Append("</div><div class=\"sub\">Dragnet network profile</div><div class=\"status ");
        html.Append(Encode(profile.Health));
        html.Append("\">");
        html.Append(Encode(profile.Health));
        html.Append("</div></div><div class=\"actions\"><a class=\"button\" href=\"/dragnet/ledger\">Ban ledger</a><a class=\"button\" href=\"/\">Back to IW4MAdmin</a></div></header>");
        html.Append("<section class=\"metrics\">");
        AppendMetric(html, profile.SubmittedBanCount, "submitted bans");
        AppendMetric(html, profile.ActiveBanCount, "active bans");
        AppendMetric(html, profile.ServerCount, "reported servers");
        AppendMetric(html, profile.EvidenceRatePercent, "evidence coverage", "%");
        html.Append("</section><div class=\"grid\">");
        html.Append("<section class=\"panel\"><h2>Identity and transport</h2><div class=\"fields\">");
        AppendField(html, "Origin fingerprint", profile.OriginId);
        AppendField(html, "Version", profile.Version ?? "Unknown");
        AppendField(html, "Region", profile.Region ?? "Not specified");
        AppendField(html, "First observed", DescribeTime(profile.FirstSeenUtc));
        AppendField(html, "Last heartbeat", DescribeTime(profile.LastSeenUtc));
        AppendField(html, "Identity proof", profile.IdentityVerified ? "Verified" : "Unverified");
        AppendField(html, "Endpoint proof", profile.EndpointVerified ? "Verified" : "Pending or stale");
        AppendField(html, "Current failure streak", profile.CurrentFailureCount.ToString());
        AppendField(html, "Last failed attempt", DescribeTime(profile.LastFailureAtUtc));
        html.Append("</div>");
        if (!string.IsNullOrWhiteSpace(profile.Endpoint))
        {
            html.Append("<p><strong>Endpoint:</strong> <a href=\"");
            html.Append(Encode(profile.Endpoint));
            html.Append("/health\" target=\"_blank\" rel=\"noopener noreferrer\">");
            html.Append(Encode(profile.Endpoint));
            html.Append("</a></p>");
        }
        if (!string.IsNullOrWhiteSpace(profile.Website))
        {
            html.Append("<p><strong>Community:</strong> <a href=\"");
            html.Append(Encode(profile.Website));
            html.Append("\" target=\"_blank\" rel=\"noopener noreferrer\">");
            html.Append(Encode(profile.Website));
            html.Append("</a></p>");
        }
        html.Append("</section><section class=\"panel\"><h2>This instance's trust and review history</h2><div class=\"fields\">");
        AppendField(html, "Trusted", profile.TrustedByThisNetwork ? "Yes" : "No");
        AppendField(html, "Automatic ban approval", profile.AutoApproveBans ? "Enabled" : "Disabled");
        AppendField(html, "Automatic lift approval", profile.AutoApproveLifts ? "Enabled" : "Disabled");
        AppendField(html, "Reviewed bans", profile.ReviewedBanCount.ToString());
        AppendRate(html, "Approved", profile.ApprovedBanCount, profile.ApprovalRatePercent);
        AppendRate(html, "Denied", profile.DeniedBanCount, profile.DenialRatePercent);
        AppendRate(html, "Ignored", profile.IgnoredBanCount, profile.IgnoreRatePercent);
        AppendField(html, "Pending", profile.PendingBanCount.ToString());
        html.Append("</div><p class=\"muted\">These decisions describe this IW4MAdmin instance only. They are not global reputation scores.</p></section>");
        html.Append("<section class=\"panel\"><h2>Propagation and delivery</h2><div class=\"fields\">");
        AppendRate(html, "Enforced coverage", profile.EnforcedCoverageSlots, profile.EnforcementCoveragePercent);
        AppendField(html, "Reported coverage slots", $"{profile.ReportedCoverageSlots} / {profile.EligibleCoverageSlots}");
        AppendRate(html, "Acknowledged deliveries", profile.AcknowledgedDeliveryCount, profile.DeliveryAcknowledgementPercent);
        AppendField(html, "Tracked deliveries", profile.TrackedDeliveryCount.ToString());
        AppendField(html, "Lift events submitted", profile.SubmittedLiftCount.ToString());
        AppendField(html, "Bans with evidence", $"{profile.EvidenceCount} / {profile.SubmittedBanCount}");
        html.Append("</div></section><section class=\"panel\"><h2>Protocol capabilities</h2><div class=\"caps\">");
        AppendCapability(html, "Delivery acknowledgements", profile.SupportsDeliveryAcknowledgements);
        AppendCapability(html, "Evidence updates", profile.SupportsEvidenceUpdates);
        AppendCapability(html, "Ban attestations", profile.SupportsBanAttestations);
        AppendCapability(html, "Attestation refresh", profile.SupportsAttestationRefreshRequests);
        html.Append("</div><p class=\"muted\">Capabilities are advertised by signed heartbeat identity data.</p></section></div>");
        html.Append("<section class=\"panel\" style=\"margin-top:12px\"><h2>Recent submitted bans</h2><div class=\"bans\"><table><thead><tr><th>Player</th><th>Reason</th><th>Evidence</th><th>Local review</th><th>Issued</th></tr></thead><tbody>");
        if (profile.RecentBans.Count == 0)
        {
            html.Append("<tr><td colspan=\"5\" class=\"muted\">No bans from this network are stored on this node.</td></tr>");
        }
        else
        {
            foreach (var ban in profile.RecentBans)
            {
                html.Append("<tr><td><a href=\"/dragnet/ledger?id=");
                html.Append(Uri.EscapeDataString(ban.EventId));
                html.Append("\"><strong>");
                html.Append(Encode(ban.PlayerName));
                html.Append("</strong></a></td><td>");
                html.Append(Encode(ban.Reason));
                html.Append("</td><td>");
                html.Append(ban.HasEvidence ? "Available" : "<span class=\"muted\">None</span>");
                html.Append("</td><td>");
                html.Append(Encode(ban.ReviewState));
                html.Append("</td><td>");
                html.Append(Encode($"{ban.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC"));
                html.Append("</td></tr>");
            }
        }
        html.Append("</tbody></table></div></section><footer class=\"muted\">Generated ");
        html.Append(Encode($"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC"));
        html.Append(" by Dragnet ");
        html.Append(Encode(DragnetBuildInfo.Version));
        html.AppendLine(".</footer></main></body></html>");
        return html.ToString();
    }

    private static string CreateCanonicalKey(DragnetEventEnvelope envelope) =>
        envelope.Iw4mAdminPenaltyId > 0
            ? $"{envelope.OriginId}:penalty:{envelope.Iw4mAdminPenaltyId}"
            : $"{envelope.OriginId}:event:{envelope.EventId}";

    private static int Percent(int value, int total) =>
        total <= 0 ? 0 : (int)Math.Round(value * 100d / total);

    private static void AppendMetric(StringBuilder html, int value, string label, string suffix = "")
    {
        html.Append("<div class=\"metric\"><strong>");
        html.Append(value);
        html.Append(Encode(suffix));
        html.Append("</strong><span>");
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

    private static void AppendRate(StringBuilder html, string label, int count, int percent)
    {
        html.Append("<div class=\"field\"><label>");
        html.Append(Encode(label));
        html.Append("</label><div>");
        html.Append(count);
        html.Append(" (");
        html.Append(percent);
        html.Append("%)</div><div class=\"bar\"><span style=\"width:");
        html.Append(Math.Clamp(percent, 0, 100));
        html.Append("%\"></span></div></div>");
    }

    private static void AppendCapability(StringBuilder html, string label, bool enabled)
    {
        html.Append("<span class=\"cap ");
        html.Append(enabled ? "yes" : "no");
        html.Append("\">");
        html.Append(enabled ? "Supported: " : "Not advertised: ");
        html.Append(Encode(label));
        html.Append("</span>");
    }

    private static string DescribeTime(DateTimeOffset? value) =>
        value is null ? "Never" : $"{value:yyyy-MM-dd HH:mm:ss} UTC";

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string? NormalizeHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.ToString().TrimEnd('/')
            : null;
}

public sealed record DragnetNetworkProfile
{
    public required string OriginId { get; init; }
    public required string OriginName { get; init; }
    public string? Endpoint { get; init; }
    public string? Website { get; init; }
    public string? Region { get; init; }
    public string? Version { get; init; }
    public required int ServerCount { get; init; }
    public required string Health { get; init; }
    public required bool IsLocal { get; init; }
    public required bool IdentityVerified { get; init; }
    public required bool EndpointVerified { get; init; }
    public DateTimeOffset? FirstSeenUtc { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
    public required int CurrentFailureCount { get; init; }
    public DateTimeOffset? LastFailureAtUtc { get; init; }
    public required bool TrustedByThisNetwork { get; init; }
    public required bool AutoApproveBans { get; init; }
    public required bool AutoApproveLifts { get; init; }
    public required int SubmittedBanCount { get; init; }
    public required int SubmittedLiftCount { get; init; }
    public required int ActiveBanCount { get; init; }
    public required int EvidenceCount { get; init; }
    public required int EvidenceRatePercent { get; init; }
    public required int ReviewedBanCount { get; init; }
    public required int ApprovedBanCount { get; init; }
    public required int DeniedBanCount { get; init; }
    public required int IgnoredBanCount { get; init; }
    public required int PendingBanCount { get; init; }
    public required int ApprovalRatePercent { get; init; }
    public required int DenialRatePercent { get; init; }
    public required int IgnoreRatePercent { get; init; }
    public required int EligibleCoverageSlots { get; init; }
    public required int ReportedCoverageSlots { get; init; }
    public required int EnforcedCoverageSlots { get; init; }
    public required int EnforcementCoveragePercent { get; init; }
    public required int TrackedDeliveryCount { get; init; }
    public required int AcknowledgedDeliveryCount { get; init; }
    public required int DeliveryAcknowledgementPercent { get; init; }
    public required bool SupportsDeliveryAcknowledgements { get; init; }
    public required bool SupportsEvidenceUpdates { get; init; }
    public required bool SupportsBanAttestations { get; init; }
    public required bool SupportsAttestationRefreshRequests { get; init; }
    public IReadOnlyList<DragnetNetworkProfileBan> RecentBans { get; init; } = [];
}

public sealed record DragnetNetworkProfileBan
{
    public required string EventId { get; init; }
    public required string PlayerName { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public required bool HasEvidence { get; init; }
    public required string ReviewState { get; init; }
}
