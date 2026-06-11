using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Data.Models.Client;
using Dragnet.Configuration;
using Dragnet.Identity;
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
    public const string SetupInteractionId = "Dragnet::Setup";

    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetReviewService _reviewService;
    private readonly DragnetTrustService _trustService;
    private readonly DragnetUpdateService _updateService;
    private readonly DragnetOnboardingService _onboardingService;
    private readonly DragnetDirectoryService _directoryService;
    private readonly DragnetIdentityDocument _identity;
    private readonly DragnetIdentityService _identityService;
    private readonly IConfigurationHandlerV2<DragnetConfiguration> _configurationHandler;
    private readonly Func<IManager> _managerFactory;

    public DragnetWebfrontService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetReviewService reviewService,
        DragnetTrustService trustService,
        DragnetUpdateService updateService,
        DragnetOnboardingService onboardingService,
        DragnetDirectoryService directoryService,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        IConfigurationHandlerV2<DragnetConfiguration> configurationHandler,
        Func<IManager> managerFactory)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _reviewService = reviewService;
        _trustService = trustService;
        _updateService = updateService;
        _onboardingService = onboardingService;
        _directoryService = directoryService;
        _identity = identity;
        _identityService = identityService;
        _configurationHandler = configurationHandler;
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

    public Task<IInteractionData> CreateSetupInteractionAsync(CancellationToken token)
    {
        IInteractionData interaction = new InteractionData
        {
            Name = "Configure Dragnet",
            Description = "Configure Dragnet",
            DisplayMeta = "ph-gear",
            InteractionId = SetupInteractionId,
            MinimumPermission = _configuration.PeerManagementPermission,
            InteractionType = InteractionType.RawContent,
            Source = "Dragnet",
            Action = async (originId, _, _, meta, actionToken) =>
                await ProcessSetupActionAsync(originId, meta, actionToken)
        };

        return Task.FromResult(interaction);
    }

    private async Task<string> RenderDashboardAsync(
        IDictionary<string, string>? meta,
        CancellationToken token)
    {
        await _updateService.RefreshForPageLoadAsync(token);
        var onboarding = await _onboardingService.GetStatusAsync(token);
        var directory = await _directoryService.ListAsync(token);
        var events = await _eventStore.ListAsync(token);
        var peers = await _peerStore.ListAsync(token);
        var filter = ParseFilter(meta);
        var selectedEventId = meta is not null && meta.TryGetValue("eventId", out var eventId)
            ? eventId
            : null;
        var pendingBans = events.Count(item => item.ReviewState is DragnetReviewState.PendingBan);
        var pendingLifts = events.Count(item => item.ReviewState is DragnetReviewState.PendingLift);
        var queuedImports = events.Count(item =>
            item.ImportError?.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase) == true);
        var importFailures = events.Count(item =>
            !string.IsNullOrWhiteSpace(item.ImportError) &&
            !item.ImportError.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase));
        var importedEvents = events.Count(item => item.ImportedAtUtc is not null);
        var now = DateTimeOffset.UtcNow;
        var healthyPeers = peers.Count(peer => IsHealthyPeer(peer, now));
        var stalePeers = peers.Count(peer => IsStalePeer(peer, now));
        var erroredPeers = peers.Count(peer => !string.IsNullOrWhiteSpace(peer.LastError));
        var degradedPeers = peers.Count(peer =>
            string.IsNullOrWhiteSpace(peer.LastError) &&
            !IsStalePeer(peer, now) &&
            peer.ConsecutiveFailures > 0);
        var eligibleGossipPeers = peers.Count(peer =>
            string.IsNullOrWhiteSpace(peer.LastError) &&
            !IsStalePeer(peer, now));
        var recentlyAdvertisedPeers = peers.Count(peer =>
            peer.LastAdvertisedAtUtc is { } advertisedAt &&
            now - advertisedAt <= _configuration.PeerStaleAfter);
        var verifiedPeers = peers.Count(peer => peer.IdentityVerified);
        var legacyPeers = peers.Count(peer => !peer.IdentityVerified);
        var deliverableEvents = GetDeliverableEvents(events, now);
        var acknowledgementPeers = peers
            .Where(peer => peer.SupportsDeliveryAcknowledgements)
            .ToList();
        var deliveryTargetCount = deliverableEvents.Count * acknowledgementPeers.Count;
        var acknowledgedDeliveryCount = acknowledgementPeers.Sum(peer =>
            (peer.EventDeliveries ?? []).Count(delivery =>
                delivery.AcknowledgedAtUtc is not null &&
                deliverableEvents.Any(item =>
                    item.Event.EventId.Equals(delivery.EventId, StringComparison.OrdinalIgnoreCase))));
        var pendingDeliveryCount = Math.Max(0, deliveryTargetCount - acknowledgedDeliveryCount);
        var updateStatus = _updateService.Status;
        var filteredEvents = FilterEvents(events, filter).Take(50).ToList();
        var bulkApprovableEvents = filteredEvents.Where(IsBulkApprovable).ToList();
        var selectedEvent = ResolveSelectedEvent(events, selectedEventId) ?? filteredEvents.FirstOrDefault();

        var html = new StringBuilder();
        html.AppendLine("<div class=\"space-y-6\">");
        AppendOperationalHeader(html, updateStatus, now);
        AppendOnboardingPanel(html, onboarding);
        AppendDeploymentGuide(html);
        AppendDirectoryPanel(html, directory, now);
        html.AppendLine("<div class=\"grid grid-cols-2 md:grid-cols-4 xl:grid-cols-5 gap-4\">");
        AppendMetric(html, "Pending bans", pendingBans.ToString());
        AppendMetric(html, "Pending lifts", pendingLifts.ToString());
        AppendMetric(html, "Queued imports", queuedImports.ToString());
        AppendMetric(html, "Import failures", importFailures.ToString());
        AppendMetric(html, "Imported", importedEvents.ToString());
        AppendMetric(html, "Known peers", peers.Count.ToString());
        AppendMetric(html, "Healthy peers", healthyPeers.ToString());
        AppendMetric(html, "Degraded peers", degradedPeers.ToString());
        AppendMetric(html, "Stale peers", stalePeers.ToString());
        AppendMetric(html, "Errored peers", erroredPeers.ToString());
        AppendMetric(html, "Gossip eligible", eligibleGossipPeers.ToString());
        AppendMetric(html, "Advertised recently", recentlyAdvertisedPeers.ToString());
        AppendMetric(html, "Verified identities", verifiedPeers.ToString());
        AppendMetric(html, "Legacy identities", legacyPeers.ToString());
        AppendMetric(html, "Acknowledged deliveries", acknowledgedDeliveryCount.ToString());
        AppendMetric(html, "Pending deliveries", pendingDeliveryCount.ToString());
        html.AppendLine("</div>");

        html.AppendLine("<div class=\"rounded-lg border border-line bg-surface/50 overflow-hidden\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b border-line flex flex-col md:flex-row md:items-center md:justify-between gap-2\">");
        html.AppendLine("<h3 class=\"text-lg font-semibold\">Peer transport</h3>");
        html.Append("<span class=\"text-sm text-muted\">Endpoint: ");
        html.Append(Encode(_configuration.PublicEndpoint ?? "not configured"));
        html.AppendLine("</span>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted border-b border-line\"><tr><th class=\"px-4 py-3\">Origin</th><th class=\"px-4 py-3\">Endpoint</th><th class=\"px-4 py-3\">Source</th><th class=\"px-4 py-3\">Last seen</th><th class=\"px-4 py-3\">Last advertised</th><th class=\"px-4 py-3\">Delivery</th><th class=\"px-4 py-3\">Status</th><th class=\"px-4 py-3 text-right\">Actions</th></tr></thead><tbody>");

        if (peers.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"8\" class=\"px-4 py-6 text-center text-muted\">No peers discovered.</td></tr>");
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
                html.Append(peer.LastAdvertisedAtUtc is null
                    ? "Never"
                    : Encode(DescribeAge(now - peer.LastAdvertisedAtUtc.Value)));
                html.AppendLine("</td>");
                html.Append("<td class=\"px-4 py-3 text-muted\">");
                AppendDeliveryStatus(html, peer, deliverableEvents, now);
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
        AppendBulkReviewControls(html, bulkApprovableEvents.Count);
        AppendFilterLinks(html, filter);
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted border-b border-line\"><tr><th class=\"px-4 py-3 w-10\"><input type=\"checkbox\" aria-label=\"Select all eligible bans\" onclick=\"document.querySelectorAll('.dragnet-bulk-ban').forEach(function(c){c.checked=this.checked;},this)\"></th><th class=\"px-4 py-3\">Player</th><th class=\"px-4 py-3\">Origin</th><th class=\"px-4 py-3\">Trust</th><th class=\"px-4 py-3\">Type</th><th class=\"px-4 py-3\">State</th><th class=\"px-4 py-3\">Import</th><th class=\"px-4 py-3\">Created</th><th class=\"px-4 py-3 text-right\">Actions</th></tr></thead><tbody>");

        foreach (var item in filteredEvents)
        {
            html.AppendLine("<tr class=\"border-b border-line/60\">");
            html.Append("<td class=\"px-4 py-3\">");
            if (IsBulkApprovable(item))
            {
                html.Append("<input type=\"checkbox\" class=\"dragnet-bulk-ban\" aria-label=\"Select ban for ");
                html.Append(Encode(item.Event.PlayerName));
                html.Append("\" value=\"");
                html.Append(Encode(item.Event.EventId));
                html.Append("\">");
            }

            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3 font-medium\">");
            AppendEventLink(html, item.Event.EventId, item.Event.PlayerName, filter);
            html.AppendLine("</td>");
            html.Append("<td class=\"px-4 py-3 text-muted\">");
            html.Append(Encode(IsLocalEvent(item.Event) ? "Local" : item.Event.OriginName));
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
            AppendImportStatus(html, item, IsLocalEvent(item.Event));
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
            html.AppendLine("<tr><td colspan=\"9\" class=\"px-4 py-6 text-center text-muted\">No Dragnet events stored.</td></tr>");
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
            !meta.TryGetValue("ReviewAction", out var actionValue))
        {
            return "Invalid Dragnet review action.";
        }

        if (string.Equals(actionValue, "BulkApproveBan", StringComparison.OrdinalIgnoreCase))
        {
            if (!meta.TryGetValue("EventIds", out var eventIdsJson))
            {
                return "No Dragnet events were selected.";
            }

            if (eventIdsJson.Length > 10_000)
            {
                return "Bulk Dragnet selection is too large.";
            }

            List<string>? eventIds;
            try
            {
                eventIds = JsonSerializer.Deserialize<List<string>>(eventIdsJson);
            }
            catch (JsonException)
            {
                return "Invalid bulk Dragnet selection.";
            }

            var bulkResult = await _reviewService.ApplyBulkActionAsync(
                eventIds ?? [],
                DragnetReviewAction.ApproveBan,
                "Bulk approval",
                GetReviewerName(origin),
                origin?.ClientId,
                token);
            if (bulkResult.Failures.Count == 0)
            {
                return bulkResult.Message;
            }

            return $"{bulkResult.Message} {string.Join(" | ", bulkResult.Failures.Take(5))}";
        }

        if (!meta.TryGetValue("EventId", out var eventId))
        {
            return "Invalid Dragnet review action.";
        }

        if (string.Equals(actionValue, "RetryImport", StringComparison.OrdinalIgnoreCase))
        {
            var retryResult = await _reviewService.RetryImportAsync(eventId, token);
            return retryResult.Message;
        }

        if (string.Equals(actionValue, "SetEvidence", StringComparison.OrdinalIgnoreCase))
        {
            meta.TryGetValue("EvidenceUrl", out var evidenceUrl);
            return await SetEvidenceAsync(
                eventId,
                evidenceUrl,
                GetReviewerName(origin),
                token);
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

            case "Resync":
                return await _peerStore.RequestResyncAsync(peerOriginId, token)
                    ? "Dragnet peer resync queued. Approved active events will replay on the next successful heartbeat."
                    : "That Dragnet peer was not found.";

            case "VerifySync":
            {
                var peer = (await _peerStore.ListAsync(token))
                    .FirstOrDefault(item =>
                        item.OriginId.Equals(peerOriginId, StringComparison.OrdinalIgnoreCase));
                if (peer is null)
                {
                    return "That Dragnet peer was not found.";
                }

                var deliverable = GetDeliverableEvents(
                    await _eventStore.ListAsync(token),
                    DateTimeOffset.UtcNow);
                if (!peer.SupportsDeliveryAcknowledgements)
                {
                    return $"Peer {peer.OriginName} is legacy/unknown delivery coverage. Upgrade both nodes for acknowledgements.";
                }

                var acknowledged = CountAcknowledgedDeliveries(peer, deliverable);
                var sentPending = CountSentPendingDeliveries(peer, deliverable);
                return $"Peer {peer.OriginName}: {acknowledged}/{deliverable.Count} acknowledged, " +
                       $"{sentPending} sent awaiting acknowledgement, " +
                       $"{Math.Max(0, deliverable.Count - acknowledged - sentPending)} not yet sent.";
            }

            default:
                return "Invalid Dragnet peer action.";
        }
    }

    private async Task<string> ProcessSetupActionAsync(
        int originId,
        IDictionary<string, string>? meta,
        CancellationToken token)
    {
        var manager = _managerFactory();
        var origin = originId > 0 ? await manager.GetClientService().Get(originId) : null;
        if (!HasPermission(origin, _configuration.PeerManagementPermission))
        {
            return "You are not authorized to configure Dragnet.";
        }

        if (meta is null ||
            !meta.TryGetValue("OriginName", out var originName) ||
            !meta.TryGetValue("PublicEndpoint", out var publicEndpoint))
        {
            return "Network name and public endpoint are required.";
        }

        originName = originName.Trim();
        publicEndpoint = publicEndpoint.Trim().TrimEnd('/');
        meta.TryGetValue("BootstrapEndpoint", out var bootstrapEndpoint);
        bootstrapEndpoint = bootstrapEndpoint?.Trim().TrimEnd('/');
        meta.TryGetValue("DirectoryListingEnabled", out var directoryListingValue);
        meta.TryGetValue("DirectoryRegion", out var directoryRegion);
        meta.TryGetValue("DirectoryWebsite", out var directoryWebsite);
        directoryRegion = directoryRegion?.Trim();
        directoryWebsite = directoryWebsite?.Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(originName))
        {
            return "Network name is required.";
        }

        if (originName.Length > 120)
        {
            return "Network name must be 120 characters or fewer.";
        }

        if (!Uri.TryCreate(publicEndpoint, UriKind.Absolute, out var publicUri) ||
            publicUri.Scheme != Uri.UriSchemeHttps)
        {
            return "Public endpoint must be an absolute HTTPS URL.";
        }

        var directoryListingEnabled = IsEnabledValue(directoryListingValue);
        if (directoryRegion?.Length > 80)
        {
            return "Directory region must be 80 characters or fewer.";
        }

        if (!string.IsNullOrWhiteSpace(directoryWebsite) &&
            (!Uri.TryCreate(directoryWebsite, UriKind.Absolute, out var directoryWebsiteUri) ||
             directoryWebsiteUri.Scheme != Uri.UriSchemeHttps))
        {
            return "Directory website must be an absolute HTTPS URL.";
        }

        if (!string.IsNullOrWhiteSpace(bootstrapEndpoint))
        {
            if (!Uri.TryCreate(bootstrapEndpoint, UriKind.Absolute, out var bootstrapUri) ||
                bootstrapUri.Scheme != Uri.UriSchemeHttps)
            {
                return "Bootstrap endpoint must be an absolute HTTPS URL.";
            }

            if (bootstrapEndpoint.Equals(publicEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                return "Bootstrap endpoint cannot be this Dragnet node's public endpoint.";
            }

            if (!_configuration.BootstrapPeers.Any(peer =>
                    peer.Endpoint.TrimEnd('/').Equals(bootstrapEndpoint, StringComparison.OrdinalIgnoreCase)))
            {
                _configuration.BootstrapPeers.Add(new DragnetPeerConfiguration
                {
                    Endpoint = bootstrapEndpoint,
                    Enabled = true
                });
            }

            await _peerStore.AddManualPeerAsync(bootstrapEndpoint, null, token);
        }

        _configuration.OriginName = originName;
        _configuration.PublicEndpoint = publicEndpoint;
        _configuration.DirectoryListingEnabled = directoryListingEnabled;
        _configuration.DirectoryRegion = string.IsNullOrWhiteSpace(directoryRegion) ? null : directoryRegion;
        _configuration.DirectoryWebsite = string.IsNullOrWhiteSpace(directoryWebsite) ? null : directoryWebsite;
        await _configurationHandler.Set(_configuration);
        _onboardingService.Invalidate();
        return "Dragnet configuration saved. Restart IW4MAdmin to apply the network identity and endpoint everywhere.";
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
        if (IsLocalEvent(envelope))
        {
            html.Append("<span class=\"text-muted\">Local outbound event</span>");
            if (envelope.EventType is DragnetEventType.BanCreated)
            {
                AppendEvidenceButton(html, item);
            }
        }
        else
        {
            AppendTrustButtons(html, envelope);
            AppendReviewButtons(html, item);
        }

        html.AppendLine("</div></div></div>");

        html.AppendLine("<div class=\"grid grid-cols-1 lg:grid-cols-3 gap-0 border-b border-line\">");
        AppendDetailCell(html, "Type", $"{envelope.EventType} / {envelope.PenaltyKind}");
        AppendDetailCell(html, "Review state", item.ReviewState.ToString());
        AppendDetailCell(html, "Import", DescribeImport(item));
        AppendDetailCell(
            html,
            "Origin",
            IsLocalEvent(envelope)
                ? $"Local / {envelope.OriginServerName}"
                : $"{envelope.OriginName} / {envelope.OriginServerName}");
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

        var evidenceUrl = item.EvidenceUpdate?.EvidenceUrl ?? envelope.EvidenceUrl;
        if (!string.IsNullOrWhiteSpace(evidenceUrl))
        {
            html.AppendLine("<div>");
            html.AppendLine("<div class=\"text-sm text-muted mb-1\">Evidence</div>");
            html.Append("<a class=\"text-primary hover:underline break-all\" href=\"");
            html.Append(Encode(evidenceUrl));
            html.Append("\" target=\"_blank\" rel=\"noopener noreferrer\">");
            html.Append(Encode(evidenceUrl));
            html.AppendLine("</a></div>");
            if (item.EvidenceUpdate is { } evidenceUpdate)
            {
                html.Append("<div class=\"text-xs text-muted\">Updated by ");
                html.Append(Encode(evidenceUpdate.SubmittedByName));
                html.Append(" at ");
                html.Append(Encode($"{evidenceUpdate.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC"));
                html.AppendLine("</div>");
            }
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
        if (IsLocalEvent(item.Event))
        {
            html.Append("<span class=\"text-muted\">Local</span>");
            return;
        }

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

    private bool IsBulkApprovable(DragnetStoredEvent item) =>
        !IsLocalEvent(item.Event) &&
        item.Event.EventType is DragnetEventType.BanCreated &&
        item.ReviewState is DragnetReviewState.PendingBan &&
        _trustService.Evaluate(item.Event).IsTrusted;

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

    private static void AppendBulkReviewControls(StringBuilder html, int eligibleCount)
    {
        if (eligibleCount == 0)
        {
            return;
        }

        html.Append("<div class=\"flex flex-wrap items-center gap-2\"><span class=\"text-xs text-muted\">");
        html.Append(Encode($"{eligibleCount} trusted pending ban{(eligibleCount == 1 ? "" : "s")} on this page"));
        html.Append("</span><button type=\"button\" class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\" onclick=\"document.querySelectorAll('.dragnet-bulk-ban').forEach(function(c){c.checked=true;})\"><i class=\"ph ph-check-square mr-1\"></i>Select all</button>");
        html.Append("<button type=\"button\" class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-action-primary bg-action-primary text-foreground text-sm\" onclick=\"(function(){var ids=Array.from(document.querySelectorAll('.dragnet-bulk-ban:checked')).map(function(c){return c.value;});if(ids.length===0){alert('Select at least one trusted pending ban.');return;}var inputs=[{Name:'ReviewAction',Type:'hidden',Value:'BulkApproveBan'},{Name:'EventIds',Type:'hidden',Value:JSON.stringify(ids)}];var meta={InteractionId:'");
        html.Append(ReviewInteractionId);
        html.Append("',ActionButtonLabel:'Approve selected',Name:'Approve selected bans',ShouldRefresh:'true',Inputs:JSON.stringify(inputs)};var trigger=document.getElementById('dragnet-bulk-trigger');trigger.dataset.actionMeta=encodeURIComponent(JSON.stringify(meta));trigger.click();})()\"><i class=\"ph ph-checks mr-1\"></i>Approve selected</button>");
        html.Append("<button id=\"dragnet-bulk-trigger\" type=\"button\" class=\"profile-action hidden\" data-action=\"DynamicAction\" data-action-meta=\"\"></button></div>");
    }

    private static void AppendEvidenceButton(StringBuilder html, DragnetStoredEvent item)
    {
        var label = item.EvidenceUpdate is null && string.IsNullOrWhiteSpace(item.Event.EvidenceUrl)
            ? "Add evidence"
            : "Update evidence";
        var inputs = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Name"] = "EventId",
                ["Type"] = "hidden",
                ["Value"] = item.Event.EventId
            },
            new()
            {
                ["Name"] = "ReviewAction",
                ["Type"] = "hidden",
                ["Value"] = "SetEvidence"
            },
            new()
            {
                ["Name"] = "EvidenceUrl",
                ["Label"] = "Evidence URL",
                ["Value"] = item.EvidenceUpdate?.EvidenceUrl ?? item.Event.EvidenceUrl ?? "",
                ["Placeholder"] = "https://www.youtube.com/watch?v=..."
            }
        };
        var meta = new Dictionary<string, string>
        {
            ["InteractionId"] = ReviewInteractionId,
            ["ActionButtonLabel"] = label,
            ["Name"] = label,
            ["ShouldRefresh"] = "true",
            ["Inputs"] = JsonSerializer.Serialize(inputs)
        };

        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer ml-2\" data-action=\"DynamicAction\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ph-link mr-1\"></i>");
        html.Append(Encode(label));
        html.Append("</span></button>");
    }

    private async Task<string> SetEvidenceAsync(
        string eventId,
        string? evidenceUrl,
        string submittedByName,
        CancellationToken token)
    {
        evidenceUrl = evidenceUrl?.Trim();
        if (!Uri.TryCreate(evidenceUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            evidenceUrl.Length > 2048)
        {
            return "Evidence must be an absolute HTTPS URL no longer than 2048 characters.";
        }

        var storedEvent = await _eventStore.GetAsync(eventId, token);
        if (storedEvent is null)
        {
            return "Dragnet event not found.";
        }

        if (!IsLocalEvent(storedEvent.Event) ||
            storedEvent.Event.EventType is not DragnetEventType.BanCreated)
        {
            return "Evidence can only be added by the network that originated the ban.";
        }

        var createdAtUtc = DateTimeOffset.UtcNow;
        var updateIdSource = $"{_identity.OriginId}:{eventId}:{evidenceUrl}:{createdAtUtc:O}";
        var updateId = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(updateIdSource)))
            .ToLowerInvariant();
        var unsigned = new DragnetEvidenceUpdate
        {
            UpdateId = updateId,
            EventId = eventId,
            OriginId = _identity.OriginId,
            OriginName = _identity.OriginName,
            OriginPublicKeyPem = _identity.PublicKeyPem,
            EvidenceUrl = evidenceUrl,
            SubmittedByName = submittedByName,
            CreatedAtUtc = createdAtUtc,
            Signature = ""
        };
        var signed = unsigned with
        {
            Signature = _identityService.Sign(_identity, unsigned.GetSigningPayload())
        };

        return await _eventStore.SetEvidenceUpdateAsync(signed, token)
            ? "Evidence saved and queued for Dragnet peers."
            : "Evidence could not be saved.";
    }

    private void AppendTrustButtons(StringBuilder html, DragnetEventEnvelope envelope)
    {
        if (IsLocalEvent(envelope))
        {
            return;
        }

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

        AppendPeerActionButton(html, peer, "VerifySync", "Verify sync", "ph-checks");
        AppendPeerActionButton(html, peer, "Resync", "Resync", "ph-arrows-clockwise");
    }

    private static void AppendDeliveryStatus(
        StringBuilder html,
        DragnetPeerRecord peer,
        IReadOnlyList<DragnetStoredEvent> deliverableEvents,
        DateTimeOffset now)
    {
        if (!peer.SupportsDeliveryAcknowledgements)
        {
            html.Append("<span class=\"text-muted\">Legacy / unknown</span>");
            return;
        }

        var acknowledged = CountAcknowledgedDeliveries(peer, deliverableEvents);
        var pending = CountSentPendingDeliveries(peer, deliverableEvents);
        html.Append("<span class=\"text-success\">");
        html.Append(Encode($"{acknowledged}/{deliverableEvents.Count} acknowledged"));
        html.Append("</span>");
        if (pending > 0)
        {
            var failed = !string.IsNullOrWhiteSpace(peer.LastError);
            html.Append(failed
                ? "<div class=\"text-xs text-danger\">"
                : "<div class=\"text-xs text-warning\">");
            html.Append(Encode(failed
                ? $"{pending} delivery attempts blocked"
                : $"{pending} awaiting acknowledgement"));
            html.Append("</div>");
        }

        if (peer.LastSyncVerifiedAtUtc is { } verifiedAt)
        {
            html.Append("<div class=\"text-xs text-muted\">Verified ");
            html.Append(Encode(DescribeAge(now - verifiedAt)));
            html.Append("</div>");
        }
    }

    private static int CountAcknowledgedDeliveries(
        DragnetPeerRecord peer,
        IReadOnlyList<DragnetStoredEvent> deliverableEvents) =>
        (peer.EventDeliveries ?? []).Count(delivery =>
            delivery.AcknowledgedAtUtc is not null &&
            deliverableEvents.Any(item =>
                item.Event.EventId.Equals(delivery.EventId, StringComparison.OrdinalIgnoreCase)));

    private static int CountSentPendingDeliveries(
        DragnetPeerRecord peer,
        IReadOnlyList<DragnetStoredEvent> deliverableEvents) =>
        (peer.EventDeliveries ?? []).Count(delivery =>
            delivery.AcknowledgedAtUtc is null &&
            deliverableEvents.Any(item =>
                item.Event.EventId.Equals(delivery.EventId, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<DragnetStoredEvent> GetDeliverableEvents(
        IReadOnlyList<DragnetStoredEvent> events,
        DateTimeOffset now) =>
        events
            .Where(item => item.ReviewState is DragnetReviewState.ApprovedBan or DragnetReviewState.ApprovedLift)
            .Where(item => item.Event.EventType is DragnetEventType.BanLifted || !item.Event.IsExpired(now))
            .ToList();

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

        if (peer.ConsecutiveFailures > 0)
        {
            html.Append("<span class=\"text-warning\">Retrying (");
            html.Append(Encode($"{peer.ConsecutiveFailures}/{Math.Max(1, _configuration.PeerFailureThreshold)}"));
            html.Append(")</span>");
            return;
        }

        html.Append("<span class=\"text-success\">Healthy</span>");
    }

    private void AppendTrustStatus(StringBuilder html, DragnetEventEnvelope envelope)
    {
        if (IsLocalEvent(envelope))
        {
            html.Append("<span class=\"text-primary\">Local</span>");
            return;
        }

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

    private static void AppendImportStatus(
        StringBuilder html,
        DragnetStoredEvent item,
        bool isLocal)
    {
        if (isLocal)
        {
            html.Append("<span class=\"text-primary\">Outbound</span>");
            return;
        }

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

    private void AppendOnboardingPanel(
        StringBuilder html,
        DragnetOnboardingStatus status)
    {
        html.AppendLine("<div class=\"border border-line bg-surface/50 overflow-hidden\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b border-line flex flex-col md:flex-row md:items-center md:justify-between gap-3\">");
        html.AppendLine("<div>");
        html.AppendLine("<h3 class=\"font-semibold\">Deployment readiness</h3>");
        html.Append("<div class=\"text-sm text-muted\">");
        html.Append(Encode($"{status.CompletedChecks}/{DragnetOnboardingStatus.TotalChecks} checks passing"));
        if (status.RestartRequired)
        {
            html.Append(" <span class=\"text-warning\">Restart required</span>");
        }

        html.AppendLine("</div></div>");
        AppendSetupButton(html);
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3\">");
        AppendOnboardingCheck(html, "Network identity", status.IdentityConfigured,
            status.IdentityConfigured ? _configuration.OriginName : "Choose a recognizable community name");
        AppendOnboardingCheck(html, "Public endpoint", status.EndpointConfigured,
            status.EndpointConfigured ? _configuration.PublicEndpoint! : "Configure the external /dragnet URL");
        AppendOnboardingCheck(html, "HTTPS", status.EndpointUsesHttps,
            status.EndpointUsesHttps ? "Endpoint uses HTTPS" : "A valid TLS endpoint is required");
        AppendOnboardingCheck(html, "Endpoint route", status.EndpointReachable,
            status.EndpointReachable
                ? "Public health route responds successfully"
                : Shorten(status.EndpointError ?? "Endpoint has not been verified", 100));
        AppendOnboardingCheck(html, "Identity match", status.EndpointIdentityMatched,
            status.EndpointIdentityMatched
                ? "Public endpoint reports this origin fingerprint"
                : "Public endpoint identity does not match this installation");
        AppendOnboardingCheck(html, "Signed proof", status.EndpointSignatureVerified,
            status.EndpointSignatureVerified
                ? "Public endpoint returned a valid identity signature"
                : "Install the current release and restart to publish signed health proof");
        AppendOnboardingCheck(html, "Peer connectivity", status.PeerConnected,
            status.PeerConnected
                ? "At least one live peer is connected"
                : status.SeedConfigured ? "Seed configured; waiting for heartbeat" : "Add a bootstrap peer");
        AppendOnboardingCheck(html, "Plugin version", status.UpdateCurrent,
            status.UpdateCurrent ? "Current release detected" : "Review release status above");
        html.AppendLine("</div></div>");
    }

    private void AppendDeploymentGuide(StringBuilder html)
    {
        var endpoint = _configuration.PublicEndpoint?.TrimEnd('/');
        html.AppendLine("<div class=\"border border-line bg-surface/50 overflow-hidden\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b border-line flex flex-col md:flex-row md:items-center md:justify-between gap-2\">");
        html.AppendLine("<div><h3 class=\"font-semibold\">Deployment guide</h3>");
        html.AppendLine("<div class=\"text-sm text-muted\">Endpoint-specific routes and reverse-proxy requirements.</div></div>");
        html.AppendLine("<a class=\"text-sm text-primary hover:underline\" target=\"_blank\" rel=\"noopener noreferrer\" href=\"/dragnet/setup-guide\">Open shareable guide</a>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"grid grid-cols-1 lg:grid-cols-2\">");
        html.AppendLine("<div class=\"p-4 border-b lg:border-r border-line/60 space-y-2 text-sm\">");
        AppendGuideValue(html, "Health", endpoint is null ? "Not configured" : $"{endpoint}/health");
        AppendGuideValue(html, "Heartbeat", endpoint is null ? "Not configured" : $"{endpoint}/heartbeat");
        AppendGuideValue(html, "Directory", endpoint is null ? "Not configured" : $"{endpoint}/directory");
        AppendGuideValue(html, "Public ledger", endpoint is null ? "Not configured" : $"{endpoint}/ledger");
        AppendGuideValue(html, "Bootstrap", DragnetConfiguration.OfficialBootstrapEndpoint);
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"p-4 text-sm space-y-2\">");
        AppendGuideCheck(html, "TLS certificate valid");
        AppendGuideCheck(html, "POST /dragnet/heartbeat forwarded");
        AppendGuideCheck(html, "X-Forwarded-Proto set to https");
        AppendGuideCheck(html, "WebSocket upgrades enabled");
        html.AppendLine("</div></div></div>");
    }

    private static void AppendGuideValue(StringBuilder html, string label, string value)
    {
        html.Append("<div><span class=\"text-muted\">");
        html.Append(Encode(label));
        html.Append(":</span> <span class=\"font-mono text-xs break-all\">");
        html.Append(Encode(value));
        html.AppendLine("</span></div>");
    }

    private static void AppendGuideCheck(StringBuilder html, string label)
    {
        html.Append("<div class=\"flex items-center gap-2\"><i class=\"ph ph-check-square text-muted\"></i><span>");
        html.Append(Encode(label));
        html.AppendLine("</span></div>");
    }

    private static void AppendOnboardingCheck(
        StringBuilder html,
        string label,
        bool passed,
        string detail)
    {
        html.AppendLine("<div class=\"px-4 py-3 border-b border-r border-line/60\">");
        html.Append("<div class=\"flex items-center gap-2 font-medium\"><i class=\"ph ");
        html.Append(passed ? "ph-check-circle text-success" : "ph-warning-circle text-warning");
        html.Append("\"></i>");
        html.Append(Encode(label));
        html.AppendLine("</div>");
        html.Append("<div class=\"mt-1 text-xs text-muted break-words\">");
        html.Append(Encode(detail));
        html.AppendLine("</div></div>");
    }

    private void AppendSetupButton(StringBuilder html)
    {
        var meta = new Dictionary<string, string>
        {
            ["InteractionId"] = SetupInteractionId,
            ["ActionButtonLabel"] = "Save configuration",
            ["Name"] = "Configure Dragnet",
            ["ShouldRefresh"] = "true",
            ["Inputs"] = BuildSetupInputs()
        };
        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer\" data-action=\"DynamicAction\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ph-gear mr-1\"></i>Configure</span></button>");
    }

    private string BuildSetupInputs()
    {
        var seed = _configuration.BootstrapPeers
            .FirstOrDefault(peer => peer.Enabled && !string.IsNullOrWhiteSpace(peer.Endpoint))
            ?.Endpoint ?? DragnetConfiguration.OfficialBootstrapEndpoint;
        return JsonSerializer.Serialize(new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Name"] = "OriginName",
                ["Label"] = "Network name",
                ["Value"] = _configuration.OriginName,
                ["Placeholder"] = "My IW4MAdmin Network"
            },
            new()
            {
                ["Name"] = "PublicEndpoint",
                ["Label"] = "Public Dragnet endpoint",
                ["Value"] = _configuration.PublicEndpoint ?? "",
                ["Placeholder"] = "https://admin.example.com/dragnet"
            },
            new()
            {
                ["Name"] = "BootstrapEndpoint",
                ["Label"] = "Bootstrap peer",
                ["Value"] = seed,
                ["Placeholder"] = DragnetConfiguration.OfficialBootstrapEndpoint
            },
            new()
            {
                ["Name"] = "DirectoryListingEnabled",
                ["Label"] = "Publish in community directory (yes/no)",
                ["Value"] = _configuration.DirectoryListingEnabled ? "yes" : "no",
                ["Placeholder"] = "no"
            },
            new()
            {
                ["Name"] = "DirectoryRegion",
                ["Label"] = "Directory region (optional)",
                ["Value"] = _configuration.DirectoryRegion ?? "",
                ["Placeholder"] = "Europe"
            },
            new()
            {
                ["Name"] = "DirectoryWebsite",
                ["Label"] = "Community website (optional HTTPS)",
                ["Value"] = _configuration.DirectoryWebsite ?? "",
                ["Placeholder"] = "https://community.example.com"
            }
        });
    }

    private static void AppendDirectoryPanel(
        StringBuilder html,
        IReadOnlyList<DragnetDirectoryEntry> entries,
        DateTimeOffset now)
    {
        html.AppendLine("<div class=\"border border-line bg-surface/50 overflow-hidden\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b border-line flex flex-col md:flex-row md:items-center md:justify-between gap-2\">");
        html.AppendLine("<div><h3 class=\"font-semibold\">Community directory</h3>");
        html.AppendLine("<div class=\"text-sm text-muted\">Public, opt-in network listings. Directory presence does not grant trust.</div></div>");
        html.Append("<a class=\"text-sm text-primary hover:underline\" target=\"_blank\" rel=\"noopener noreferrer\" href=\"/dragnet/directory\">");
        html.Append(Encode($"{entries.Count} live listing{(entries.Count == 1 ? "" : "s")}"));
        html.AppendLine("</a></div>");
        html.AppendLine("<div class=\"overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted border-b border-line\"><tr><th class=\"px-4 py-3\">Network</th><th class=\"px-4 py-3\">Verification</th><th class=\"px-4 py-3\">Region</th><th class=\"px-4 py-3\">Servers</th><th class=\"px-4 py-3\">Version</th><th class=\"px-4 py-3\">Seen</th></tr></thead><tbody>");
        if (entries.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"6\" class=\"px-4 py-5 text-center text-muted\">No live networks have opted into directory publication.</td></tr>");
        }
        else
        {
            foreach (var entry in entries)
            {
                html.AppendLine("<tr class=\"border-b border-line/60\">");
                html.Append("<td class=\"px-4 py-3 font-medium\">");
                if (!string.IsNullOrWhiteSpace(entry.Website))
                {
                    html.Append("<a class=\"text-primary hover:underline\" target=\"_blank\" rel=\"noopener noreferrer\" href=\"");
                    html.Append(Encode(entry.Website));
                    html.Append("\">");
                    html.Append(Encode(entry.OriginName));
                    html.Append("</a>");
                }
                else
                {
                    html.Append(Encode(entry.OriginName));
                }

                html.Append("</td><td class=\"px-4 py-3\">");
                html.Append(entry.Verified
                    ? "<span class=\"text-success\"><i class=\"ph ph-seal-check mr-1\"></i>Verified</span>"
                    : "<span class=\"text-warning\"><i class=\"ph ph-warning-circle mr-1\"></i>Unverified</span>");
                html.Append("<div class=\"text-xs text-muted\">");
                html.Append(Encode(entry.VerificationMethod));
                html.Append("</div></td><td class=\"px-4 py-3 text-muted\">");
                html.Append(Encode(entry.Region ?? "Not specified"));
                html.Append("</td><td class=\"px-4 py-3\">");
                html.Append(Encode(entry.ServerCount.ToString()));
                html.Append("</td><td class=\"px-4 py-3 text-muted\">");
                html.Append(Encode(entry.Version ?? "Unknown"));
                html.Append("</td><td class=\"px-4 py-3 text-muted\">");
                html.Append(Encode(DescribeAge(now - entry.LastSeenUtc)));
                html.AppendLine("</td></tr>");
            }
        }

        html.AppendLine("</tbody></table></div></div>");
    }

    private static bool IsEnabledValue(string? value) =>
        value is not null &&
        (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static void AppendOperationalHeader(
        StringBuilder html,
        DragnetUpdateStatus update,
        DateTimeOffset now)
    {
        html.AppendLine("<div class=\"border border-line bg-surface/50\">");
        html.AppendLine("<div class=\"grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4\">");
        html.AppendLine("<div class=\"px-4 py-3 border-b sm:border-r xl:border-b-0 border-line/60\">");
        html.Append("<div class=\"text-xs text-muted\">Deployed version</div><div class=\"mt-1 font-semibold\">Dragnet ");
        html.Append(Encode(update.CurrentVersion));
        html.AppendLine("</div></div>");

        html.AppendLine("<div class=\"px-4 py-3 border-b xl:border-r xl:border-b-0 border-line/60\">");
        html.Append("<div class=\"text-xs text-muted\">Release status</div><div class=\"mt-1 font-medium\">");
        if (update.UpdateAvailable)
        {
            html.Append("<span class=\"text-warning\">Update available: ");
            html.Append(Encode(update.LatestVersion ?? "new release"));
            html.Append("</span>");
        }
        else if (!string.IsNullOrWhiteSpace(update.LatestVersion))
        {
            html.Append("<span class=\"text-success\">Current</span>");
        }
        else if (!update.CheckEnabled)
        {
            html.Append("<span class=\"text-muted\">Disabled</span>");
        }
        else if (update.IsChecking)
        {
            html.Append("<span class=\"text-muted\">Checking</span>");
        }
        else
        {
            html.Append("<span class=\"text-warning\">Check failed</span>");
        }

        html.AppendLine("</div></div>");

        html.AppendLine("<div class=\"px-4 py-3 border-b sm:border-b-0 sm:border-r border-line/60\">");
        html.Append("<div class=\"text-xs text-muted\">Last checked</div><div class=\"mt-1 font-medium\">");
        html.Append(update.CheckedAtUtc is null
            ? "Not yet"
            : Encode(DescribeAge(now - update.CheckedAtUtc.Value)));
        if (!string.IsNullOrWhiteSpace(update.CheckError))
        {
            html.Append("<div class=\"mt-1 text-xs text-muted break-words\">");
            html.Append(Encode(Shorten(update.CheckError, 120)));
            html.Append("</div>");
        }

        html.AppendLine("</div></div>");
        html.AppendLine("<div class=\"px-4 py-3 flex flex-col justify-center\">");
        html.AppendLine("<div class=\"text-xs text-muted\">Release channel</div>");
        if (update.UpdateAvailable && !string.IsNullOrWhiteSpace(update.ReleaseUrl))
        {
            html.Append("<a class=\"mt-1 text-primary hover:underline break-words\" target=\"_blank\" rel=\"noopener noreferrer\" href=\"");
            html.Append(Encode(update.ReleaseUrl));
            html.AppendLine("\">View release</a>");
        }
        else
        {
            html.Append("<a class=\"mt-1 text-primary hover:underline\" target=\"_blank\" rel=\"noopener noreferrer\" href=\"");
            html.Append(Encode(DragnetBuildInfo.RepositoryUrl + "/releases"));
            html.AppendLine("\">GitHub releases</a>");
        }

        html.AppendLine("</div></div></div>");
    }

    private static string Shorten(string value, int maximumLength) =>
        value.Length <= maximumLength
            ? value
            : value[..Math.Max(0, maximumLength - 3)] + "...";

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

    private IEnumerable<DragnetStoredEvent> FilterEvents(
        IReadOnlyList<DragnetStoredEvent> events,
        DragnetEventFilter filter)
    {
        var remoteEvents = events.Where(item => !IsLocalEvent(item.Event));
        return filter switch
        {
        DragnetEventFilter.Pending => remoteEvents.Where(item =>
            item.ReviewState is DragnetReviewState.PendingBan or DragnetReviewState.PendingLift),
        DragnetEventFilter.ImportFailed => remoteEvents.Where(item => !string.IsNullOrWhiteSpace(item.ImportError)),
        DragnetEventFilter.Imported => remoteEvents.Where(item => item.ImportedAtUtc is not null),
        DragnetEventFilter.Reviewed => remoteEvents.Where(item =>
            item.ReviewState is not (DragnetReviewState.PendingBan or DragnetReviewState.PendingLift)),
        DragnetEventFilter.Denied => remoteEvents.Where(item =>
            item.ReviewState is DragnetReviewState.DeniedBan or DragnetReviewState.DeniedLift),
        DragnetEventFilter.Ignored => remoteEvents.Where(item =>
            item.ReviewState is DragnetReviewState.IgnoredBan or DragnetReviewState.IgnoredLift),
        DragnetEventFilter.Local => events.Where(item => IsLocalEvent(item.Event)),
        DragnetEventFilter.All => events,
        _ => events
        };
    }

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
            html.Append("<a data-enhance-nav=\"false\" class=\"inline-flex items-center px-3 py-1.5 rounded-md border text-sm ");
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
        html.Append("<a data-enhance-nav=\"false\" class=\"text-primary hover:underline\" href=\"");
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
        DragnetEventFilter.Local => "Local",
        DragnetEventFilter.All => "All events",
        _ => filter.ToString()
    };

    private bool IsLocalEvent(DragnetEventEnvelope envelope) =>
        string.Equals(envelope.OriginId, _identity.OriginId, StringComparison.OrdinalIgnoreCase);

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
        string.IsNullOrWhiteSpace(peer.LastError) &&
        peer.ConsecutiveFailures == 0 &&
        !IsStalePeer(peer, now);

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
    Local,
    All
}
