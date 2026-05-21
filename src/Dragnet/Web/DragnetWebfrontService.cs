using System.Net;
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

    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetReviewService _reviewService;
    private readonly DragnetTrustService _trustService;
    private readonly IManager _manager;

    public DragnetWebfrontService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetReviewService reviewService,
        DragnetTrustService trustService,
        IManager manager)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _reviewService = reviewService;
        _trustService = trustService;
        _manager = manager;
    }

    public Task<IInteractionData> CreateNavigationInteractionAsync(CancellationToken token)
    {
        IInteractionData interaction = new InteractionData
        {
            Name = "Dragnet",
            Description = "Dragnet",
            DisplayMeta = "ph-network",
            InteractionId = NavigationInteractionId,
            MinimumPermission = EFClient.Permission.Administrator,
            InteractionType = InteractionType.TemplateContent,
            Source = "Dragnet",
            Action = async (_, _, _, _, actionToken) => await RenderDashboardAsync(actionToken)
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
            MinimumPermission = EFClient.Permission.Administrator,
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
            MinimumPermission = EFClient.Permission.Administrator,
            InteractionType = InteractionType.RawContent,
            Source = "Dragnet",
            Action = async (originId, _, _, meta, actionToken) =>
                await ProcessTrustActionAsync(originId, meta, actionToken)
        };

        return Task.FromResult(interaction);
    }

    private async Task<string> RenderDashboardAsync(CancellationToken token)
    {
        var events = await _eventStore.ListAsync(token);
        var peers = await _peerStore.ListAsync(token);
        var pendingBans = events.Count(item => item.ReviewState is DragnetReviewState.PendingBan);
        var pendingLifts = events.Count(item => item.ReviewState is DragnetReviewState.PendingLift);
        var healthyPeers = peers.Count(peer => string.IsNullOrWhiteSpace(peer.LastError));
        var now = DateTimeOffset.UtcNow;

        var html = new StringBuilder();
        html.AppendLine("<div class=\"space-y-6\">");
        html.AppendLine("<div class=\"grid grid-cols-1 md:grid-cols-4 gap-4\">");
        AppendMetric(html, "Pending bans", pendingBans.ToString());
        AppendMetric(html, "Pending lifts", pendingLifts.ToString());
        AppendMetric(html, "Known peers", peers.Count.ToString());
        AppendMetric(html, "Healthy peers", healthyPeers.ToString());
        html.AppendLine("</div>");

        html.AppendLine("<div class=\"rounded-lg border border-line bg-surface/50 overflow-hidden\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b border-line flex flex-col md:flex-row md:items-center md:justify-between gap-2\">");
        html.AppendLine("<h3 class=\"text-lg font-semibold\">Peer transport</h3>");
        html.Append("<span class=\"text-sm text-muted\">Endpoint: ");
        html.Append(Encode(_configuration.PublicEndpoint ?? "not configured"));
        html.AppendLine("</span>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted border-b border-line\"><tr><th class=\"px-4 py-3\">Origin</th><th class=\"px-4 py-3\">Endpoint</th><th class=\"px-4 py-3\">Last seen</th><th class=\"px-4 py-3\">Status</th></tr></thead><tbody>");

        if (peers.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"4\" class=\"px-4 py-6 text-center text-muted\">No peers discovered.</td></tr>");
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
                html.Append(Encode(DescribeAge(now - peer.LastSeenUtc)));
                html.AppendLine("</td>");
                html.Append("<td class=\"px-4 py-3\">");
                html.Append(string.IsNullOrWhiteSpace(peer.LastError)
                    ? "<span class=\"text-success\">Healthy</span>"
                    : $"<span class=\"text-danger\">{Encode(peer.LastError)}</span>");
                html.AppendLine("</td>");
                html.AppendLine("</tr>");
            }
        }

        html.AppendLine("</tbody></table></div></div>");
        html.AppendLine("<div class=\"rounded-lg border border-line bg-surface/50 overflow-hidden\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b border-line\"><h3 class=\"text-lg font-semibold\">Recent Dragnet events</h3></div>");
        html.AppendLine("<div class=\"overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted border-b border-line\"><tr><th class=\"px-4 py-3\">Player</th><th class=\"px-4 py-3\">Origin</th><th class=\"px-4 py-3\">Trust</th><th class=\"px-4 py-3\">Type</th><th class=\"px-4 py-3\">State</th><th class=\"px-4 py-3\">Import</th><th class=\"px-4 py-3\">Created</th><th class=\"px-4 py-3 text-right\">Actions</th></tr></thead><tbody>");

        foreach (var item in events.Take(25))
        {
            html.AppendLine("<tr class=\"border-b border-line/60\">");
            html.Append("<td class=\"px-4 py-3 font-medium\">");
            html.Append(Encode(item.Event.PlayerName));
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
            html.Append(Encode(DescribeAge(now - item.Event.CreatedAtUtc)));
            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3 text-right\">");
            AppendTrustButtons(html, item.Event);
            AppendReviewButtons(html, item);
            html.AppendLine("</td>");
            html.AppendLine("</tr>");
        }

        if (events.Count == 0)
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
        var origin = originId > 0 ? await _manager.GetClientService().Get(originId) : null;
        if (origin?.Level < EFClient.Permission.Administrator)
        {
            return "You are not authorized to review Dragnet events.";
        }

        if (meta is null ||
            !meta.TryGetValue("EventId", out var eventId) ||
            !meta.TryGetValue("ReviewAction", out var actionValue) ||
            !Enum.TryParse<DragnetReviewAction>(actionValue, true, out var action))
        {
            return "Invalid Dragnet review action.";
        }

        meta.TryGetValue("Reason", out var reason);
        var result = await _reviewService.ApplyActionAsync(eventId, action, reason, token);
        return result.Message;
    }

    private async Task<string> ProcessTrustActionAsync(
        int originId,
        IDictionary<string, string>? meta,
        CancellationToken token)
    {
        var origin = originId > 0 ? await _manager.GetClientService().Get(originId) : null;
        if (origin?.Level < EFClient.Permission.Administrator)
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

    private void AppendReviewButtons(StringBuilder html, DragnetStoredEvent item)
    {
        var isTrusted = _trustService.Evaluate(item.Event).IsTrusted;
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
                ["Value"] = action.ToString()
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
}
