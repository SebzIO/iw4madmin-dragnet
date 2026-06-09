using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Data.Models.Client;
using Dragnet.Configuration;
using Dragnet.Models;
using Dragnet.Services;
using Dragnet.Storage;
using Dragnet.Transport;
using SharedLibraryCore.Helpers;
using SharedLibraryCore.Interfaces;

namespace Dragnet.Web;

public sealed class DragnetWebfrontService
{
    public const string NavigationInteractionId = "Webfront::Nav::Admin::Dragnet";
    public const string ReviewInteractionId = "Dragnet::Review";
    public const string TrustInteractionId = "Dragnet::Trust";
    public const string PeerInteractionId = "Dragnet::Peer";

    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetReviewService _reviewService;
    private readonly DragnetTrustService _trustService;
    private readonly Func<IManager> _managerFactory;

    public DragnetWebfrontService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetReviewService reviewService,
        DragnetTrustService trustService,
        Func<IManager> managerFactory)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _reviewService = reviewService;
        _trustService = trustService;
        _managerFactory = managerFactory;
    }

    public Task<IInteractionData> CreateNavigationInteractionAsync(CancellationToken token)
    {
        IInteractionData interaction = new InteractionData
        {
            Name = "Dragnet",
            Description = "Dragnet",
            DisplayMeta = "ph-network",
            InteractionId = NavigationInteractionId,
            MinimumPermission = _configuration.WebfrontPermission,
            InteractionType = InteractionType.TemplateContent,
            Source = "Dragnet",
            Action = async (_, _, _, meta, actionToken) => await RenderDashboardAsync(meta, actionToken)
        };

        return Task.FromResult(interaction);
    }

    public Task<IInteractionData> CreateReviewInteractionAsync(CancellationToken token)
    {
        IInteractionData interaction = new InteractionData
        {
            Name = "Dragnet Review",
            Description = "Review Dragnet event",
            DisplayMeta = "ph-check-circle",
            InteractionId = ReviewInteractionId,
            MinimumPermission = _configuration.ReviewPermission,
            InteractionType = InteractionType.RawContent,
            Source = "Dragnet",
            Action = async (originId, _, _, meta, actionToken) =>
                await ProcessReviewActionAsync(originId, meta, actionToken)
        };

        return Task.FromResult(interaction);
    }

    public Task<IInteractionData> CreateTrustInteractionAsync(CancellationToken token)
    {
        IInteractionData interaction = new InteractionData
        {
            Name = "Dragnet Trust",
            Description = "Manage Dragnet origin trust",
            DisplayMeta = "ph-shield-check",
            InteractionId = TrustInteractionId,
            MinimumPermission = _configuration.TrustPermission,
            InteractionType = InteractionType.RawContent,
            Source = "Dragnet",
            Action = async (originId, _, _, meta, actionToken) =>
                await ProcessTrustActionAsync(originId, meta, actionToken)
        };

        return Task.FromResult(interaction);
    }

    public Task<IInteractionData> CreatePeerInteractionAsync(CancellationToken token)
    {
        IInteractionData interaction = new InteractionData
        {
            Name = "Dragnet Peer",
            Description = "Manage Dragnet peer",
            DisplayMeta = "ph-plugs",
            InteractionId = PeerInteractionId,
            MinimumPermission = _configuration.PeerManagementPermission,
            InteractionType = InteractionType.RawContent,
            Source = "Dragnet",
            Action = async (originId, _, _, meta, actionToken) =>
                await ProcessPeerActionAsync(originId, meta, actionToken)
        };

        return Task.FromResult(interaction);
    }

    private async Task<string> RenderDashboardAsync(
        IDictionary<string, string>? meta,
        CancellationToken token)
    {
        var events = await _eventStore.ListAsync(token);
        var peers = await _peerStore.ListAsync(token);
        var filter = ParseFilter(meta);
        var selectedEventId = meta is not null && meta.TryGetValue("eventId", out var eventId)
            ? eventId
            : null;
        var pendingBans = events.Count(item => item.ReviewState is DragnetReviewState.PendingBan);
        var pendingLifts = events.Count(item => item.ReviewState is DragnetReviewState.PendingLift);
        var importFailures = events.Count(item => !string.IsNullOrWhiteSpace(item.ImportError));
        var now = DateTimeOffset.UtcNow;
        var healthyPeers = peers.Count(peer => IsHealthyPeer(peer, now));
        var stalePeers = peers.Count(peer => IsStalePeer(peer, now));
        var filteredEvents = FilterEvents(events, filter).Take(50).ToList();
        var selectedEvent = ResolveSelectedEvent(events, selectedEventId) ?? filteredEvents.FirstOrDefault();

        var html = new StringBuilder();
        html.AppendLine("<div class=\"space-y-6\">");
        html.AppendLine("<div class=\"grid grid-cols-1 md:grid-cols-6 gap-4\">");
        AppendMetric(html, "Pending bans", pendingBans.ToString());
        AppendMetric(html, "Pending lifts", pendingLifts.ToString());
        AppendMetric(html, "Import failures", importFailures.ToString());
        AppendMetric(html, "Known peers", peers.Count.ToString());
        AppendMetric(html, "Healthy peers", healthyPeers.ToString());
        AppendMetric(html, "Stale peers", stalePeers.ToString());
        html.AppendLine("</div>");

        html.AppendLine("<div class=\"rounded-lg border border-line bg-surface/50 overflow-hidden\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b border-line flex flex-col md:flex-row md:items-center md:justify-between gap-2\">");
        html.AppendLine("<h3 class=\"text-lg font-semibold\">Peer transport</h3>");
        html.Append("<span class=\"text-sm text-muted\">Endpoint: ");
        html.Append(Encode(_configuration.PublicEndpoint ?? "not configured"));
        html.AppendLine("</span>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted border-b border-line\"><tr><th class=\"px-4 py-3\">Origin</th><th class=\"px-4 py-3\">Endpoint</th><th class=\"px-4 py-3\">Source</th><th class=\"px-4 py-3\">Last seen</th><th class=\"px-4 py-3\">Last sent</th><th class=\"px-4 py-3\">Status</th><th class=\"px-4 py-3 text-right\">Actions</th></tr></thead><tbody>");

        if (peers.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"7\" class=\"px-4 py-6 text-center text-muted\">No peers discovered.</td></tr>");
        }
        else
        {
            foreach (var peer in peers.OrderByDescending(peer => peer.LastSeenUtc))
            {
                html.AppendLine("<tr class=\"border-b border-line/60\">");
                html.Append("<td class=\"px-4 py-3 font-medium\">");
                html.Append(Encode(peer.OriginName));
                html.AppendLine("</td>");
                html.Append("<td class=\"px-4 py-3 text-muted\">");
                html.Append(Encode(peer.Endpoint));
                html.AppendLine("</td>");
                html.Append("<td class=\"px-4 py-3 text-muted\">");
                html.Append(peer.IsBootstrap ? "Bootstrap" : "Discovered");
                html.AppendLine("</td>");
                html.Append("<td class=\"px-4 py-3 text-muted\">");
                html.Append(Encode(DescribeAge(now - peer.LastSeenUtc)));
                html.AppendLine("</td>");
                html.Append("<td class=\"px-4 py-3 text-muted\">");
                html.Append(peer.LastEventSentAtUtc is null || peer.LastEventSentAtUtc.Value <= DateTimeOffset.UnixEpoch
                    ? "Never"
                    : Encode(DescribeAge(now - peer.LastEventSentAtUtc.Value)));
                html.AppendLine("</td>");
                html.Append("<td class=\"px-4 py-3\">");
                AppendPeerStatus(html, peer, now);
                html.AppendLine("</td>");
                html.Append("<td class=\"px-4 py-3 text-right\">");
                AppendPeerButtons(html, peer);
                html.AppendLine("</td>");
                html.AppendLine("</tr>");
            }
        }

        html.AppendLine("</tbody></table></div></div>");

        if (selectedEvent is not null)
        {
            AppendEventDetail(html, selectedEvent, now);
        }

        html.AppendLine("<div class=\"rounded-lg border border-line bg-surface/50 overflow-hidden\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b border-line flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between\">");
        html.AppendLine("<h3 class=\"text-lg font-semibold\">Dragnet events</h3>");
        AppendFilterLinks(html, filter);
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted border-b border-line\"><tr><th class=\"px-4 py-3\">Player</th><th class=\"px-4 py-3\">Origin</th><th class=\"px-4 py-3\">Trust</th><th class=\"px-4 py-3\">Type</th><th class=\"px-4 py-3\">State</th><th class=\"px-4 py-3\">Import</th><th class=\"px-4 py-3\">Created</th><th class=\"px-4 py-3 text-right\">Actions</th></tr></thead><tbody>");

        foreach (var item in filteredEvents)
        {
            html.AppendLine("<tr class=\"border-b border-line/60\">");
            html.Append("<td class=\"px-4 py-3 font-medium\">");
            AppendEventLink(html, item.Event.EventId, item.Event.PlayerName, filter);
            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3 text-muted\">");
            html.Append(Encode(item.Event.OriginName));
            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3\">");
            AppendTrustStatus(html, item.Event);
            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3\">");
            html.Append(Encode(item.Event.EventType.ToString()));
            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3\">");
            html.Append(Encode(item.ReviewState.ToString()));
            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3\">");
            AppendImportStatus(html, item);
            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3 text-muted\">");
            html.Append(Encode(DescribeEventAge(item.Event, now)));
            if (item.ReviewedAtUtc is not null)
            {
                html.Append("<div class=\"text-xs text-muted\">Reviewed by ");
                html.Append(Encode(item.ReviewedByName ?? "Unknown"));
                html.Append("</div>");
            }

            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3 text-right\">");
            AppendTrustButtons(html, item.Event);
            AppendReviewButtons(html, item);
            html.AppendLine("</td>");
            html.AppendLine("</tr>");
        }

        if (filteredEvents.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"8\" class=\"px-4 py-6 text-center text-muted\">No Dragnet events stored.</td></tr>");
        }

        html.AppendLine("</tbody></table></div></div>");
        html.AppendLine("</div>");
        return html.ToString();
    }

    private async Task<string> ProcessReviewActionAsync(
        int originId,
        IDictionary<string, string>? meta,
        CancellationToken token)
    {
        var manager = _managerFactory();
        var origin = originId > 0 ? await manager.GetClientService().Get(originId) : null;
        if (!HasPermission(origin, _configuration.ReviewPermission))
        {
            return "You are not authorized to review Dragnet events.";
        }

        if (meta is null ||
            !meta.TryGetValue("EventId", out var eventId) ||
            !meta.TryGetValue("ReviewAction", out var actionValue))
        {
            return "Invalid Dragnet review action.";
        }

        if (string.Equals(actionValue, "RetryImport", StringComparison.OrdinalIgnoreCase))
        {
            var retryResult = await _reviewService.RetryImportAsync(eventId, token);
            return retryResult.Message;
        }

        if (!Enum.TryParse<DragnetReviewAction>(actionValue, true, out var action))
        {
            return "Invalid Dragnet review action.";
        }

        meta.TryGetValue("Reason", out var reason);
        var result = await _reviewService.ApplyActionAsync(
            eventId,
            action,
            reason,
            GetReviewerName(origin),
            origin?.ClientId,
            token);
        return result.Message;
    }

    private async Task<string> ProcessTrustActionAsync(
        int originId,
        IDictionary<string, string>? meta,
        CancellationToken token)
    {
        var manager = _managerFactory();
        var origin = originId > 0 ? await manager.GetClientService().Get(originId) : null;
        if (!HasPermission(origin, _configuration.TrustPermission))
        {
            return "You are not authorized to manage Dragnet trust.";
        }

        if (meta is null ||
            !meta.TryGetValue("OriginId", out var remoteOriginId) ||
            !meta.TryGetValue("OriginName", out var remoteOriginName) ||
            !meta.TryGetValue("TrustAction", out var trustAction))
        {
            return "Invalid Dragnet trust action.";
        }

        switch (trustAction)
        {
            case "Trust":
                await _trustService.TrustAsync(remoteOriginId, remoteOriginName, false, false, token);
                return $"Trusted Dragnet origin {remoteOriginName}.";

            case "TrustAuto":
                await _trustService.TrustAsync(remoteOriginId, remoteOriginName, true, true, token);
                return $"Trusted Dragnet origin {remoteOriginName} with auto-approval.";

            case "Untrust":
                return await _trustService.UntrustAsync(remoteOriginId, token)
                    ? $"Untrusted Dragnet origin {remoteOriginName}."
                    : "That Dragnet origin was not trusted.";

            default:
                return "Invalid Dragnet trust action.";
        }
    }

    private async Task<string> ProcessPeerActionAsync(
        int originId,
        IDictionary<string, string>? meta,
        CancellationToken token)
    {
        var manager = _managerFactory();
        var origin = originId > 0 ? await manager.GetClientService().Get(originId) : null;
        if (!HasPermission(origin, _configuration.PeerManagementPermission))
        {
            return "You are not authorized to manage Dragnet peers.";
        }

        if (meta is null ||
            !meta.TryGetValue("OriginId", out var peerOriginId) ||
            !meta.TryGetValue("PeerAction", out var peerAction))
        {
            return "Invalid Dragnet peer action.";
        }

        switch (peerAction)
        {
            case "ClearError":
                await _peerStore.ClearErrorAsync(peerOriginId, token);
                return "Cleared Dragnet peer error.";

            case "Remove":
                return await _peerStore.RemoveAsync(peerOriginId, token)
                    ? "Removed Dragnet peer."
                    : "That Dragnet peer was not found.";

            default:
                return "Invalid Dragnet peer action.";
        }
    }

    private void AppendEventDetail(
        StringBuilder html,
        DragnetStoredEvent item,
        DateTimeOffset now)
    {
        var envelope = item.Event;
        html.AppendLine("<div class=\"rounded-lg border border-line bg-surface/50 overflow-hidden\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b border-line flex flex-col gap-2 lg:flex-row lg:items-start lg:justify-between\">");
        html.AppendLine("<div>");
        html.Append("<h3 class=\"text-lg font-semibold\">");
        html.Append(Encode(envelope.PlayerName));
        html.AppendLine("</h3>");
        html.Append("<div class=\"text-sm text-muted font-mono\">");
        html.Append(Encode(DragnetReviewService.ShortId(envelope.EventId)));
        html.AppendLine("</div>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"text-right\">");
        AppendTrustStatus(html, envelope);
        html.Append("<div class=\"mt-2\">");
        AppendTrustButtons(html, envelope);
        AppendReviewButtons(html, item);
        html.AppendLine("</div></div></div>");

        html.AppendLine("<div class=\"grid grid-cols-1 lg:grid-cols-3 gap-0 border-b border-line\">");
        AppendDetailCell(html, "Type", $"{envelope.EventType} / {envelope.PenaltyKind}");
        AppendDetailCell(html, "Review state", item.ReviewState.ToString());
        AppendDetailCell(html, "Import", DescribeImport(item));
        AppendDetailCell(html, "Origin", $"{envelope.OriginName} / {envelope.OriginServerName}");
        AppendDetailCell(html, "Player network", string.IsNullOrWhiteSpace(envelope.PlayerGame)
            ? envelope.PlayerNetworkId
            : $"{envelope.PlayerNetworkId} ({envelope.PlayerGame})");
        AppendDetailCell(html, "Created", DescribeEventCreatedAt(envelope, now));
        AppendDetailCell(html, "Expires", envelope.ExpiresAtUtc is null
            ? "Permanent"
            : $"{envelope.ExpiresAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        AppendDetailCell(html, "IW4MAdmin penalty", envelope.Iw4mAdminPenaltyId > 0 ? envelope.Iw4mAdminPenaltyId.ToString() : "Unknown");
        AppendDetailCell(html, "Admin", envelope.AdminName ?? "Unknown");
        AppendDetailCell(html, "Reviewed by", item.ReviewedAtUtc is null
            ? "Not reviewed"
            : $"{item.ReviewedByName ?? "Unknown"} at {item.ReviewedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        html.AppendLine("</div>");

        html.AppendLine("<div class=\"p-4 space-y-4\">");
        html.AppendLine("<div>");
        html.AppendLine("<div class=\"text-sm text-muted mb-1\">Reason</div>");
        html.Append("<div class=\"whitespace-pre-wrap\">");
        html.Append(Encode(envelope.Reason));
        html.AppendLine("</div></div>");

        if (!string.IsNullOrWhiteSpace(envelope.EvidenceUrl))
        {
            html.AppendLine("<div>");
            html.AppendLine("<div class=\"text-sm text-muted mb-1\">Evidence</div>");
            html.Append("<a class=\"text-primary hover:underline break-all\" href=\"");
            html.Append(Encode(envelope.EvidenceUrl));
            html.Append("\" target=\"_blank\" rel=\"noopener noreferrer\">");
            html.Append(Encode(envelope.EvidenceUrl));
            html.AppendLine("</a></div>");
        }

        if (!string.IsNullOrWhiteSpace(item.LocalDecisionReason))
        {
            html.AppendLine("<div>");
            html.AppendLine("<div class=\"text-sm text-muted mb-1\">Local decision note</div>");
            html.Append("<div class=\"whitespace-pre-wrap\">");
            html.Append(Encode(item.LocalDecisionReason));
            html.AppendLine("</div></div>");
        }

        if (item.AuditTrail is { Count: > 0 })
        {
            html.AppendLine("<div>");
            html.AppendLine("<div class=\"text-sm text-muted mb-2\">Audit trail</div>");
            html.AppendLine("<div class=\"overflow-x-auto rounded-md border border-line\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted border-b border-line\"><tr><th class=\"px-3 py-2\">When</th><th class=\"px-3 py-2\">Reviewer</th><th class=\"px-3 py-2\">Change</th><th class=\"px-3 py-2\">Reason</th></tr></thead><tbody>");

            foreach (var entry in item.AuditTrail.OrderByDescending(entry => entry.ReviewedAtUtc))
            {
                html.AppendLine("<tr class=\"border-b border-line/60\">");
                html.Append("<td class=\"px-3 py-2 text-muted\">");
                html.Append(Encode($"{entry.ReviewedAtUtc:yyyy-MM-dd HH:mm:ss} UTC"));
                html.AppendLine("</td>");
                html.Append("<td class=\"px-3 py-2\">");
                html.Append(Encode(entry.ReviewedByName));
                if (entry.ReviewedByClientId is not null)
                {
                    html.Append(" <span class=\"text-muted\">#");
                    html.Append(Encode(entry.ReviewedByClientId.Value.ToString()));
                    html.Append("</span>");
                }

                html.AppendLine("</td>");
                html.Append("<td class=\"px-3 py-2\">");
                html.Append(Encode($"{entry.PreviousState} -> {entry.NewState}"));
                html.AppendLine("</td>");
                html.Append("<td class=\"px-3 py-2 text-muted\">");
                html.Append(Encode(string.IsNullOrWhiteSpace(entry.Reason) ? "No reason provided" : entry.Reason));
                html.AppendLine("</td>");
                html.AppendLine("</tr>");
            }

            html.AppendLine("</tbody></table></div></div>");
        }

        if (!string.IsNullOrWhiteSpace(item.ImportError))
        {
            html.AppendLine("<div>");
            html.AppendLine("<div class=\"text-sm text-muted mb-1\">Import error</div>");
            html.Append("<div class=\"text-danger whitespace-pre-wrap\">");
            html.Append(Encode(item.ImportError));
            html.AppendLine("</div></div>");
        }

        html.AppendLine("<div class=\"grid grid-cols-1 lg:grid-cols-2 gap-4 text-sm\">");
        AppendTechnicalDetail(html, "Origin id", envelope.OriginId);
        AppendTechnicalDetail(html, "Origin public key fingerprint", DragnetIdentityFingerprint(envelope.OriginPublicKeyPem));
        AppendTechnicalDetail(html, "Event hash", envelope.ComputeUnsignedHash());
        AppendTechnicalDetail(html, "Signature", envelope.Signature);
        html.AppendLine("</div>");
        html.AppendLine("</div></div>");
    }

    private static void AppendDetailCell(StringBuilder html, string label, string value)
    {
        html.AppendLine("<div class=\"p-4 border-r border-line/60 border-b border-line/60\">");
        html.Append("<div class=\"text-sm text-muted\">");
        html.Append(Encode(label));
        html.AppendLine("</div>");
        html.Append("<div class=\"mt-1 break-words\">");
        html.Append(Encode(value));
        html.AppendLine("</div></div>");
    }

    private static void AppendTechnicalDetail(StringBuilder html, string label, string value)
    {
        html.AppendLine("<div>");
        html.Append("<div class=\"text-sm text-muted mb-1\">");
        html.Append(Encode(label));
        html.AppendLine("</div>");
        html.Append("<div class=\"font-mono text-xs break-all rounded-md border border-line bg-surface-alt/40 p-2\">");
        html.Append(Encode(value));
        html.AppendLine("</div></div>");
    }

    private void AppendReviewButtons(StringBuilder html, DragnetStoredEvent item)
    {
        var isTrusted = _trustService.Evaluate(item.Event).IsTrusted;
        if (item.ImportedAtUtc is null &&
            !string.IsNullOrWhiteSpace(item.ImportError) &&
            item.ReviewState is DragnetReviewState.ApprovedBan or DragnetReviewState.ApprovedLift)
        {
            AppendRetryImportButton(html, item.Event.EventId);
        }

        switch (item.ReviewState)
        {
            case DragnetReviewState.PendingBan:
                if (isTrusted)
                {
                    AppendActionButton(html, item.Event.EventId, DragnetReviewAction.ApproveBan, "Approve", "ph-check");
                }

                AppendActionButton(html, item.Event.EventId, DragnetReviewAction.DenyBan, "Deny", "ph-x", includeReason: true);
                AppendActionButton(html, item.Event.EventId, DragnetReviewAction.IgnoreBan, "Ignore", "ph-eye-slash");
                break;

            case DragnetReviewState.PendingLift:
                if (isTrusted)
                {
                    AppendActionButton(html, item.Event.EventId, DragnetReviewAction.ApproveLift, "Approve lift", "ph-check");
                }

                AppendActionButton(html, item.Event.EventId, DragnetReviewAction.DenyLift, "Deny lift", "ph-x", includeReason: true);
                AppendActionButton(html, item.Event.EventId, DragnetReviewAction.IgnoreLift, "Ignore", "ph-eye-slash");
                break;

            default:
                html.Append("<span class=\"text-muted\">Reviewed</span>");
                break;
        }
    }

    private static void AppendRetryImportButton(StringBuilder html, string eventId)
    {
        var meta = new Dictionary<string, string>
        {
            ["InteractionId"] = ReviewInteractionId,
            ["ActionButtonLabel"] = "Retry import",
            ["Name"] = "Retry import",
            ["ShouldRefresh"] = "true",
            ["Inputs"] = BuildReviewInputs(eventId, "RetryImport", includeReason: false)
        };

        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer ml-2\" data-action=\"DynamicAction\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ph-arrow-clockwise mr-1\"></i>Retry import</span></button>");
    }

    private void AppendTrustButtons(StringBuilder html, DragnetEventEnvelope envelope)
    {
        var trust = _trustService.Evaluate(envelope);
        if (trust.IsTrusted)
        {
            AppendTrustActionButton(html, envelope, "Untrust", "Untrust", "ph-shield-slash");
            return;
        }

        AppendTrustActionButton(html, envelope, "Trust", "Trust", "ph-shield-check");
        AppendTrustActionButton(html, envelope, "TrustAuto", "Trust + auto", "ph-shield-star");
    }

    private static void AppendTrustActionButton(
        StringBuilder html,
        DragnetEventEnvelope envelope,
        string trustAction,
        string label,
        string icon)
    {
        var meta = new Dictionary<string, string>
        {
            ["InteractionId"] = TrustInteractionId,
            ["ActionButtonLabel"] = label,
            ["Name"] = label,
            ["ShouldRefresh"] = "true",
            ["Inputs"] = BuildTrustInputs(envelope, trustAction)
        };

        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer ml-2\" data-action=\"DynamicAction\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append(" mr-1\"></i>");
        html.Append(Encode(label));
        html.Append("</span></button>");
    }

    private void AppendPeerButtons(StringBuilder html, DragnetPeerRecord peer)
    {
        if (!string.IsNullOrWhiteSpace(peer.LastError))
        {
            AppendPeerActionButton(html, peer, "ClearError", "Clear error", "ph-eraser");
        }

        if (!peer.IsBootstrap)
        {
            AppendPeerActionButton(html, peer, "Remove", "Remove", "ph-trash");
        }
    }

    private static void AppendPeerActionButton(
        StringBuilder html,
        DragnetPeerRecord peer,
        string peerAction,
        string label,
        string icon)
    {
        var meta = new Dictionary<string, string>
        {
            ["InteractionId"] = PeerInteractionId,
            ["ActionButtonLabel"] = label,
            ["Name"] = label,
            ["ShouldRefresh"] = "true",
            ["Inputs"] = BuildPeerInputs(peer, peerAction)
        };

        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer ml-2\" data-action=\"DynamicAction\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append(" mr-1\"></i>");
        html.Append(Encode(label));
        html.Append("</span></button>");
    }

    private void AppendPeerStatus(
        StringBuilder html,
        DragnetPeerRecord peer,
        DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(peer.LastError))
        {
            html.Append("<span class=\"text-danger\">");
            html.Append(Encode(peer.LastError));
            html.Append("</span>");
            return;
        }

        if (IsStalePeer(peer, now))
        {
            html.Append("<span class=\"text-warning\">Stale</span>");
            return;
        }

        html.Append("<span class=\"text-success\">Healthy</span>");
    }

    private void AppendTrustStatus(StringBuilder html, DragnetEventEnvelope envelope)
    {
        var trust = _trustService.Evaluate(envelope);
        if (!trust.IsTrusted)
        {
            html.Append("<span class=\"text-danger\">Untrusted</span>");
            return;
        }

        if (trust.AutoApprove)
        {
            html.Append("<span class=\"text-success\">Trusted + auto</span>");
            return;
        }

        html.Append("<span class=\"text-success\">Trusted</span>");
    }

    private static void AppendImportStatus(StringBuilder html, DragnetStoredEvent item)
    {
        if (item.ImportedAtUtc is not null)
        {
            html.Append("<span class=\"text-success\">Imported</span>");
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.ImportError))
        {
            if (item.ImportError.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase))
            {
                html.Append("<span class=\"text-warning\">Queued</span>");
                return;
            }

            html.Append("<span class=\"text-danger\">");
            html.Append(Encode(item.ImportError));
            html.Append("</span>");
            return;
        }

        html.Append("<span class=\"text-muted\">Pending</span>");
    }

    private static void AppendActionButton(
        StringBuilder html,
        string eventId,
        DragnetReviewAction action,
        string label,
        string icon,
        bool includeReason = false)
    {
        var meta = new Dictionary<string, string>
        {
            ["InteractionId"] = ReviewInteractionId,
            ["ActionButtonLabel"] = label,
            ["Name"] = label,
            ["ShouldRefresh"] = "true",
            ["Inputs"] = BuildReviewInputs(eventId, action, includeReason)
        };

        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer ml-2\" data-action=\"DynamicAction\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append(" mr-1\"></i>");
        html.Append(Encode(label));
        html.Append("</span></button>");
    }

    private static string BuildReviewInputs(
        string eventId,
        DragnetReviewAction action,
        bool includeReason)
    {
        return BuildReviewInputs(eventId, action.ToString(), includeReason);
    }

    private static string BuildReviewInputs(
        string eventId,
        string action,
        bool includeReason)
    {
        var inputs = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Name"] = "EventId",
                ["Type"] = "hidden",
                ["Value"] = eventId
            },
            new()
            {
                ["Name"] = "ReviewAction",
                ["Type"] = "hidden",
                ["Value"] = action
            }
        };

        if (includeReason)
        {
            inputs.Add(new Dictionary<string, object?>
            {
                ["Name"] = "Reason",
                ["Label"] = "Reason",
                ["Placeholder"] = "Optional local decision note"
            });
        }

        return JsonSerializer.Serialize(inputs);
    }

    private static string BuildTrustInputs(DragnetEventEnvelope envelope, string trustAction)
    {
        var inputs = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Name"] = "OriginId",
                ["Type"] = "hidden",
                ["Value"] = envelope.OriginId
            },
            new()
            {
                ["Name"] = "OriginName",
                ["Type"] = "hidden",
                ["Value"] = envelope.OriginName
            },
            new()
            {
                ["Name"] = "TrustAction",
                ["Type"] = "hidden",
                ["Value"] = trustAction
            }
        };

        return JsonSerializer.Serialize(inputs);
    }

    private static string BuildPeerInputs(DragnetPeerRecord peer, string peerAction)
    {
        var inputs = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Name"] = "OriginId",
                ["Type"] = "hidden",
                ["Value"] = peer.OriginId
            },
            new()
            {
                ["Name"] = "PeerAction",
                ["Type"] = "hidden",
                ["Value"] = peerAction
            }
        };

        return JsonSerializer.Serialize(inputs);
    }

    private static void AppendMetric(StringBuilder html, string label, string value)
    {
        html.AppendLine("<div class=\"rounded-lg border border-line bg-surface/50 p-4\">");
        html.Append("<div class=\"text-sm text-muted\">");
        html.Append(Encode(label));
        html.AppendLine("</div>");
        html.Append("<div class=\"mt-2 text-3xl font-semibold\">");
        html.Append(Encode(value));
        html.AppendLine("</div></div>");
    }

    private static DragnetEventFilter ParseFilter(IDictionary<string, string>? meta)
    {
        if (meta is not null &&
            meta.TryGetValue("filter", out var filterValue) &&
            Enum.TryParse<DragnetEventFilter>(filterValue, true, out var filter))
        {
            return filter;
        }

        return DragnetEventFilter.Pending;
    }

    private static IEnumerable<DragnetStoredEvent> FilterEvents(
        IReadOnlyList<DragnetStoredEvent> events,
        DragnetEventFilter filter) => filter switch
    {
        DragnetEventFilter.Pending => events.Where(item =>
            item.ReviewState is DragnetReviewState.PendingBan or DragnetReviewState.PendingLift),
        DragnetEventFilter.ImportFailed => events.Where(item => !string.IsNullOrWhiteSpace(item.ImportError)),
        DragnetEventFilter.Imported => events.Where(item => item.ImportedAtUtc is not null),
        DragnetEventFilter.Reviewed => events.Where(item =>
            item.ReviewState is not (DragnetReviewState.PendingBan or DragnetReviewState.PendingLift)),
        DragnetEventFilter.Denied => events.Where(item =>
            item.ReviewState is DragnetReviewState.DeniedBan or DragnetReviewState.DeniedLift),
        DragnetEventFilter.Ignored => events.Where(item =>
            item.ReviewState is DragnetReviewState.IgnoredBan or DragnetReviewState.IgnoredLift),
        DragnetEventFilter.All => events,
        _ => events
    };

    private static DragnetStoredEvent? ResolveSelectedEvent(
        IReadOnlyList<DragnetStoredEvent> events,
        string? selectedEventId)
    {
        if (string.IsNullOrWhiteSpace(selectedEventId))
        {
            return null;
        }

        return events.FirstOrDefault(item =>
            item.Event.EventId.StartsWith(selectedEventId, StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendFilterLinks(StringBuilder html, DragnetEventFilter activeFilter)
    {
        html.AppendLine("<div class=\"flex flex-wrap gap-2\">");
        foreach (var filter in Enum.GetValues<DragnetEventFilter>())
        {
            var activeClass = filter == activeFilter
                ? "bg-action-primary text-foreground border-action-primary"
                : "border-line text-muted hover:bg-surface-hover";
            html.Append("<a class=\"inline-flex items-center px-3 py-1.5 rounded-md border text-sm ");
            html.Append(activeClass);
            html.Append("\" href=\"");
            html.Append(BuildDashboardUri(filter));
            html.Append("\">");
            html.Append(Encode(FilterLabel(filter)));
            html.AppendLine("</a>");
        }

        html.AppendLine("</div>");
    }

    private static void AppendEventLink(
        StringBuilder html,
        string eventId,
        string label,
        DragnetEventFilter filter)
    {
        html.Append("<a class=\"text-primary hover:underline\" href=\"");
        html.Append(BuildDashboardUri(filter, eventId));
        html.Append("\">");
        html.Append(Encode(label));
        html.Append("</a>");
    }

    private static string BuildDashboardUri(
        DragnetEventFilter filter,
        string? eventId = null)
    {
        var uri = $"/Interaction/Render/{NavigationInteractionId}?filter={filter}";
        return string.IsNullOrWhiteSpace(eventId)
            ? uri
            : $"{uri}&eventId={Uri.EscapeDataString(eventId)}";
    }

    private static string FilterLabel(DragnetEventFilter filter) => filter switch
    {
        DragnetEventFilter.Pending => "Pending",
        DragnetEventFilter.ImportFailed => "Import failed",
        DragnetEventFilter.Imported => "Imported",
        DragnetEventFilter.Reviewed => "Reviewed",
        DragnetEventFilter.Denied => "Denied",
        DragnetEventFilter.Ignored => "Ignored",
        DragnetEventFilter.All => "All",
        _ => filter.ToString()
    };

    private static string DescribeImport(DragnetStoredEvent item)
    {
        if (item.ImportedAtUtc is not null)
        {
            return $"Imported {item.ImportedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        }

        if (!string.IsNullOrWhiteSpace(item.ImportError))
        {
            if (item.ImportError.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase))
            {
                return item.ImportError;
            }

            return $"Failed: {item.ImportError}";
        }

        return item.ReviewState is DragnetReviewState.ApprovedBan or DragnetReviewState.ApprovedLift
            ? "Approved without import"
            : "Not imported";
    }

    private static string DragnetIdentityFingerprint(string publicKeyPem)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(publicKeyPem));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private bool IsHealthyPeer(DragnetPeerRecord peer, DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(peer.LastError) && !IsStalePeer(peer, now);

    private bool IsStalePeer(DragnetPeerRecord peer, DateTimeOffset now) =>
        now - peer.LastSeenUtc > _configuration.PeerStaleAfter;

    private static string DescribeEventAge(DragnetEventEnvelope envelope, DateTimeOffset now) =>
        HasKnownCreatedAt(envelope)
            ? DescribeAge(now - envelope.CreatedAtUtc)
            : "Unknown";

    private static string DescribeEventCreatedAt(DragnetEventEnvelope envelope, DateTimeOffset now) =>
        HasKnownCreatedAt(envelope)
            ? $"{envelope.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC ({DescribeAge(now - envelope.CreatedAtUtc)})"
            : "Unknown";

    private static bool HasKnownCreatedAt(DragnetEventEnvelope envelope) =>
        envelope.CreatedAtUtc > DateTimeOffset.UnixEpoch;

    private static string DescribeAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60)
        {
            return "just now";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age.TotalHours < 48)
        {
            return $"{(int)age.TotalHours}h ago";
        }

        return $"{(int)age.TotalDays}d ago";
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static bool HasPermission(EFClient? client, EFClient.Permission permission) =>
        client?.Level >= permission;

    private static string GetReviewerName(EFClient? client) =>
        client?.CurrentAlias?.Name ??
        (client is null ? "Unknown reviewer" : $"Client #{client.ClientId}");
}

public enum DragnetEventFilter
{
    Pending,
    ImportFailed,
    Imported,
    Reviewed,
    Denied,
    Ignored,
    All
}
