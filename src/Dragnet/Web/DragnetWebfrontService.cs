using System.Net;
using System.Globalization;
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
    public const string LedgerNavigationInteractionId = "Webfront::Nav::Main::DragnetLedger";
    public const string ReviewInteractionId = "Dragnet::Review";
    public const string TrustInteractionId = "Dragnet::Trust";
    public const string PeerInteractionId = "Dragnet::Peer";
    public const string SetupInteractionId = "Dragnet::Setup";
    public const string NotificationInteractionId = "Dragnet::Notification";

    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetReviewService _reviewService;
    private readonly DragnetTrustService _trustService;
    private readonly DragnetUpdateService _updateService;
    private readonly DragnetOnboardingService _onboardingService;
    private readonly DragnetDirectoryService _directoryService;
    private readonly DragnetLedgerService _ledgerService;
    private readonly DragnetNetworkProfileService _networkProfileService;
    private readonly DragnetIdentityDocument _identity;
    private readonly DragnetIdentityService _identityService;
    private readonly IConfigurationHandlerV2<DragnetConfiguration> _configurationHandler;
    private readonly Func<IManager> _managerFactory;
    private readonly DragnetNotificationService? _notificationService;

    public DragnetWebfrontService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetReviewService reviewService,
        DragnetTrustService trustService,
        DragnetUpdateService updateService,
        DragnetOnboardingService onboardingService,
        DragnetDirectoryService directoryService,
        DragnetLedgerService ledgerService,
        DragnetNetworkProfileService networkProfileService,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        IConfigurationHandlerV2<DragnetConfiguration> configurationHandler,
        Func<IManager> managerFactory,
        DragnetNotificationService? notificationService = null)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _reviewService = reviewService;
        _trustService = trustService;
        _updateService = updateService;
        _onboardingService = onboardingService;
        _directoryService = directoryService;
        _ledgerService = ledgerService;
        _networkProfileService = networkProfileService;
        _identity = identity;
        _identityService = identityService;
        _configurationHandler = configurationHandler;
        _managerFactory = managerFactory;
        _notificationService = notificationService;
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
            Action = async (originId, _, _, meta, actionToken) =>
                await RenderDashboardAsync(originId, meta, actionToken)
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

    public Task<IInteractionData> CreateNotificationInteractionAsync(CancellationToken token)
    {
        IInteractionData interaction = new InteractionData
        {
            Name = "Dragnet Notification",
            Description = "Acknowledge Dragnet notification",
            DisplayMeta = "ph-bell",
            InteractionId = NotificationInteractionId,
            MinimumPermission = _configuration.ReviewPermission,
            InteractionType = InteractionType.RawContent,
            Source = "Dragnet",
            Action = async (originId, _, _, meta, actionToken) =>
                await ProcessNotificationActionAsync(originId, meta, actionToken)
        };

        return Task.FromResult(interaction);
    }

    public Task<IInteractionData> CreateLedgerNavigationInteractionAsync(CancellationToken token)
    {
        IInteractionData interaction = new InteractionData
        {
            Name = "Dragnet Ledger",
            Description = "Dragnet Ledger",
            DisplayMeta = "ph-list-magnifying-glass",
            InteractionId = LedgerNavigationInteractionId,
            MinimumPermission = _configuration.WebfrontPermission,
            InteractionType = InteractionType.TemplateContent,
            Source = "Dragnet",
            Action = async (originId, _, _, meta, actionToken) =>
            {
                var mergedMeta = meta is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(meta, StringComparer.OrdinalIgnoreCase);
                mergedMeta["module"] = "ledger";
                return await RenderDashboardAsync(originId, mergedMeta, actionToken);
            }
        };

        return Task.FromResult(interaction);
    }

    public async Task<string> RenderPublicLedgerAsync(CancellationToken token)
    {
        var snapshot = await _ledgerService.GetSnapshotAsync(token);
        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Dragnet Public Ledger</title>");
        html.AppendLine("<link rel=\"stylesheet\" href=\"https://unpkg.com/@phosphor-icons/web@2.1.1/src/regular/style.css\">");
        AppendDashboardStyles(html);
        html.AppendLine("""
<style>
body.dragnet-public{margin:0;background:#100b15;color:#f6f2fb;font:14px system-ui,sans-serif;letter-spacing:0}
.dragnet-public *{box-sizing:border-box}
.dragnet-public button{font:inherit;color:inherit;background:transparent}
.dragnet-public a{color:#f72585;text-decoration:none}.dragnet-public a:hover{text-decoration:underline}
.dragnet-public-shell{min-height:100vh;padding:34px 24px}
.dragnet-public-card{box-shadow:0 24px 80px rgba(0,0,0,.32)}
.dragnet-public-title{font-size:30px;line-height:1.1;margin:0 0 8px}
.dragnet-public-sub{color:#c7bdd0}
.dragnet-public .dragnet-modal{background:#17111d}
.dragnet-public table{min-width:1040px}
.dragnet-public th{color:#bfb2cb;font-size:12px;text-transform:uppercase;letter-spacing:.04em}
.dragnet-public td,.dragnet-public th{vertical-align:middle}
</style>
""");
        html.AppendLine("</head><body class=\"dragnet-public\"><main class=\"dragnet-public-shell\">");
        html.AppendLine("<div class=\"max-w-6xl mx-auto\"><div class=\"mb-4 flex flex-col gap-2 md:flex-row md:items-end md:justify-between\"><div><h1 class=\"dragnet-public-title\">Dragnet Public Ledger</h1><div class=\"dragnet-public-sub\">Public peer moderation records from this Dragnet network.</div></div><a class=\"inline-flex items-center rounded-md border border-line px-3 py-1.5 hover:bg-surface-hover\" href=\"/\"><i class=\"ph ph-arrow-left mr-1\"></i>Back to IW4MAdmin</a></div>");
        html.AppendLine("<div class=\"dragnet-public-card rounded-lg border border-line bg-surface/50 p-3\">");
        html.AppendLine("<div class=\"flex items-center justify-between gap-3 pb-3\"><h2 class=\"text-lg font-semibold\"><i class=\"ph ph-list-magnifying-glass mr-2\"></i>Public ledger</h2>");
        html.Append(BuildLedgerModuleControls(snapshot, 1));
        html.AppendLine("</div>");
        AppendLedgerModule(html, snapshot, 1);
        html.AppendLine("</div></div>");
        AppendLedgerDetailModals(html, snapshot);
        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    private async Task<string> RenderDashboardAsync(
        int originId,
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
        var ledgerPage = meta is not null &&
                         meta.TryGetValue("ledgerPage", out var ledgerPageValue) &&
                         int.TryParse(ledgerPageValue, out var parsedLedgerPage)
            ? Math.Max(1, parsedLedgerPage)
            : 1;
        var pendingBans = events.Count(item => item.ReviewState is DragnetReviewState.PendingBan);
        var pendingLifts = events.Count(item => item.ReviewState is DragnetReviewState.PendingLift);
        var queuedImports = events.Count(item =>
            item.ImportError?.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase) == true);
        var importFailures = events.Count(item =>
            !string.IsNullOrWhiteSpace(item.ImportError) &&
            !item.ImportError.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase));
        var importedEvents = events.Count(item => item.ImportedAtUtc is not null);
        var now = DateTimeOffset.UtcNow;
        var activePeers = peers
            .Where(peer => DragnetPeerHealth.IsActive(
                peer,
                now,
                _configuration.PeerStaleAfter))
            .ToList();
        var quarantinedPeers = peers
            .Where(DragnetPeerHealth.IsQuarantined)
            .ToList();
        var displayedPeers = peers
            .Where(peer => !DragnetPeerHealth.IsQuarantined(peer))
            .ToList();
        var peerTableRows = displayedPeers
            .Concat(quarantinedPeers)
            .GroupBy(peer => peer.OriginId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var healthyPeers = activePeers.Count(peer => peer.ConsecutiveFailures == 0);
        var stalePeers = peers.Count(peer =>
            !DragnetPeerHealth.IsQuarantined(peer) &&
            IsStalePeer(peer, now));
        var erroredPeers = peers.Count(peer =>
            !DragnetPeerHealth.IsQuarantined(peer) &&
            !string.IsNullOrWhiteSpace(peer.LastError));
        var degradedPeers = activePeers.Count(peer => peer.ConsecutiveFailures > 0);
        var eligibleGossipPeers = activePeers.Count;
        var recentlyAdvertisedPeers = activePeers.Count(peer =>
            peer.LastAdvertisedAtUtc is { } advertisedAt &&
            now - advertisedAt <= _configuration.PeerStaleAfter);
        var verifiedPeers = activePeers.Count(peer => peer.IdentityVerified);
        var legacyPeers = activePeers.Count(peer => !peer.IdentityVerified);
        var deliverableEvents = GetDeliverableEvents(events, now);
        var acknowledgementPeers = peers
            .Where(peer =>
                peer.SupportsDeliveryAcknowledgements &&
                DragnetPeerHealth.IsActive(peer, now, _configuration.PeerStaleAfter))
            .ToList();
        var deliveryTargetCount = deliverableEvents.Count * acknowledgementPeers.Count;
        var acknowledgedDeliveryCount = acknowledgementPeers.Sum(peer =>
            (peer.EventDeliveries ?? []).Count(delivery =>
                delivery.AcknowledgedAtUtc is not null &&
                deliverableEvents.Any(item =>
                    item.Event.EventId.Equals(delivery.EventId, StringComparison.OrdinalIgnoreCase))));
        var pendingDeliveryCount = Math.Max(0, deliveryTargetCount - acknowledgedDeliveryCount);
        var updateStatus = _updateService.Status;
        var updateHistory = _updateService.History;
        var diagnostics = DragnetDiagnosticsService.Create(
            _configuration,
            peers,
            events,
            updateStatus,
            now);
        var diagnosticsAttentionCount = diagnostics.Peers.Count(peer =>
            peer.Active && peer.HealthScore < 70);
        var targetVersion = updateStatus.LatestVersion ?? updateStatus.CurrentVersion;
        var outdatedPeers = activePeers.Count(peer =>
            !string.IsNullOrWhiteSpace(peer.Version) &&
            DragnetUpdateService.CompareVersions(peer.Version, targetVersion) < 0);
        var unknownVersionPeers = activePeers.Count(peer => string.IsNullOrWhiteSpace(peer.Version));
        var updateAttentionCount =
            (updateStatus.RestartRequired || updateStatus.UpdateAvailable ||
             !string.IsNullOrWhiteSpace(updateStatus.CheckError) ||
             !string.IsNullOrWhiteSpace(updateStatus.InstallError)
                ? 1
                : 0) +
            outdatedPeers +
            unknownVersionPeers;
        var filteredEvents = FilterEvents(events, filter).Take(50).ToList();
        var eventRows = events.Take(50).ToList();
        var bulkApprovableEvents = filteredEvents.Where(IsBulkApprovable).ToList();
        var selectedEvent = ResolveSelectedEvent(events, selectedEventId) ?? filteredEvents.FirstOrDefault();
        IReadOnlyList<DragnetNotification> unreadNotifications = [];
        if (_notificationService is not null)
        {
            await _notificationService.SyncStaleReviewsAsync(token);
            unreadNotifications = await _notificationService.ListForClientAsync(originId, token);
        }
        var networkProfileIds = directory
            .Select(entry => entry.OriginId)
            .Concat(displayedPeers.Select(peer => peer.OriginId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var networkProfiles = new List<DragnetNetworkProfile>();
        foreach (var profileId in networkProfileIds)
        {
            if (await _networkProfileService.GetAsync(profileId, token) is { } profile)
            {
                networkProfiles.Add(profile);
            }
        }

        var html = new StringBuilder();
        html.AppendLine("<div class=\"space-y-6\">");
        AppendDashboardStyles(html);
        AppendDashboardNavigation(
            html,
            unreadNotifications.Count,
            directory.Count,
            peerTableRows.Count,
            filteredEvents.Count,
            updateAttentionCount,
            diagnosticsAttentionCount);
        AppendOperationalHeader(html, updateStatus, now);
        AppendOnboardingPanel(html, onboarding);
        html.AppendLine("<div class=\"grid grid-cols-2 md:grid-cols-4 xl:grid-cols-5 gap-4\">");
        AppendMetric(html, "Pending bans", pendingBans.ToString());
        AppendMetric(html, "Pending lifts", pendingLifts.ToString());
        AppendMetric(html, "Queued imports", queuedImports.ToString());
        AppendMetric(html, "Import failures", importFailures.ToString());
        AppendMetric(html, "Imported", importedEvents.ToString());
        AppendMetric(html, "Active peers", activePeers.Count.ToString());
        AppendMetric(html, "Healthy peers", healthyPeers.ToString());
        AppendMetric(html, "Degraded peers", degradedPeers.ToString());
        AppendMetric(html, "Stale peers", stalePeers.ToString());
        AppendMetric(html, "Errored peers", erroredPeers.ToString());
        AppendMetric(html, "Quarantined", quarantinedPeers.Count.ToString());
        AppendMetric(html, "Gossip eligible", eligibleGossipPeers.ToString());
        AppendMetric(html, "Advertised recently", recentlyAdvertisedPeers.ToString());
        AppendMetric(html, "Verified identities", verifiedPeers.ToString());
        AppendMetric(html, "Legacy identities", legacyPeers.ToString());
        AppendMetric(html, "Acknowledged deliveries", acknowledgedDeliveryCount.ToString());
        AppendMetric(html, "Pending deliveries", pendingDeliveryCount.ToString());
        html.AppendLine("</div>");

        if (selectedEvent is not null)
        {
            AppendEventDetail(html, selectedEvent, now);
        }

        AppendModalStart(
            html,
            "dragnet-notification-modal",
            "Notification inbox",
            "ph-bell",
            BuildNotificationModuleControls(unreadNotifications));
        AppendNotificationInbox(html, unreadNotifications, filter, now);
        AppendModalEnd(html);
        AppendModalStart(html, "dragnet-updates-modal", "Update rollout", "ph-cloud-arrow-down");
        AppendUpdateOperationsPanel(html, updateStatus, updateHistory, activePeers, now);
        AppendModalEnd(html);
        AppendModalStart(html, "dragnet-diagnostics-modal", "Network diagnostics", "ph-activity");
        AppendDiagnosticsPanel(html, diagnostics, now);
        AppendModalEnd(html);
        AppendModalStart(html, "dragnet-guide-modal", "Deployment guide", "ph-clipboard-text");
        AppendDeploymentGuide(html);
        AppendModalEnd(html);
        AppendModalStart(
            html,
            "dragnet-directory-modal",
            "Community directory",
            "ph-address-book");
        AppendDirectoryPanel(html, directory, now);
        AppendModalEnd(html);
        AppendNetworkProfileModals(html, networkProfiles);
        var ledgerSnapshot = await _ledgerService.GetSnapshotAsync(token);
        AppendModalStart(
            html,
            "dragnet-ledger-modal",
            "Public ledger",
            "ph-list-magnifying-glass",
            BuildLedgerModuleControls(ledgerSnapshot, ledgerPage));
        AppendLedgerModule(html, ledgerSnapshot, ledgerPage);
        AppendModalEnd(html);
        AppendLedgerDetailModals(html, ledgerSnapshot);

        AppendModalStart(html, "dragnet-peer-modal", "Peer transport", "ph-plugs");
        html.AppendLine("<div id=\"peer-transport\">");
        html.AppendLine("<div class=\"dragnet-peer-list\">");

        if (peerTableRows.Count == 0)
        {
            html.AppendLine("<div class=\"dragnet-peer-empty\">No active or recovering peers.</div>");
        }
        else
        {
            foreach (var peer in peerTableRows
                         .OrderBy(peer => DragnetPeerHealth.IsQuarantined(peer) ? 1 : 0)
                         .ThenByDescending(peer => peer.LastSeenUtc))
            {
                var quarantined = DragnetPeerHealth.IsQuarantined(peer);
                html.Append("<div id=\"peer-row-");
                html.Append(Encode(peer.OriginId));
                html.AppendLine("\" class=\"dragnet-peer-row\">");
                html.Append("<div class=\"dragnet-peer-cell dragnet-peer-identity\"><span class=\"dragnet-peer-label\">Origin</span><div class=\"font-medium\">");
                if (quarantined)
                {
                    html.Append(Encode(peer.OriginName));
                    html.Append("<div class=\"text-xs text-muted\">Recovery probe ");
                    html.Append(peer.LastRecoveryProbeAtUtc is null
                        ? "pending"
                        : Encode(DescribeAge(now - peer.LastRecoveryProbeAtUtc.Value)));
                    html.Append("</div>");
                }
                else
                {
                    html.Append("<button type=\"button\" class=\"inline-flex items-center gap-2 text-primary hover:underline\" onclick=\"(function(row,icon){var open=row.classList.toggle('open');if(icon)icon.classList.toggle('open',open);})(document.getElementById('peer-row-");
                    html.Append(Encode(peer.OriginId));
                    html.Append("'),this.querySelector('.dragnet-chevron'))\"><span>");
                    html.Append(Encode(peer.OriginName));
                    html.Append("</span><span class=\"dragnet-chevron\" aria-hidden=\"true\"></span></button>");
                }
                html.Append("</div><div class=\"dragnet-peer-endpoint\">");
                html.Append(Encode(peer.Endpoint));
                html.AppendLine("</div></div>");
                html.Append("<div class=\"dragnet-peer-cell\"><span class=\"dragnet-peer-label\">Discovery</span><div>");
                html.Append(quarantined ? "Quarantined" : peer.IsBootstrap ? "Bootstrap" : "Discovered");
                html.Append("</div><div class=\"dragnet-peer-meta\">Seen ");
                html.Append(quarantined && peer.QuarantinedAtUtc is not null
                    ? Encode($"Quarantined {DescribeAge(now - peer.QuarantinedAtUtc.Value)}")
                    : Encode(DescribeAge(now - peer.LastSeenUtc)));
                html.Append("</div><div class=\"dragnet-peer-meta\">Last advertised ");
                html.Append(peer.LastAdvertisedAtUtc is null
                    ? "Never"
                    : Encode(DescribeAge(now - peer.LastAdvertisedAtUtc.Value)));
                html.AppendLine("</div></div>");
                html.Append("<div class=\"dragnet-peer-cell\"><span class=\"dragnet-peer-label\">Delivery</span>");
                AppendDeliveryStatus(html, peer, deliverableEvents, now);
                html.AppendLine("</div>");
                html.Append("<div class=\"dragnet-peer-cell\"><span class=\"dragnet-peer-label\">Status</span>");
                if (quarantined)
                {
                    AppendPeerStatusBadge(html, "Quarantined", "ph-lock-key", "text-warning", "Peer is held until it sends a valid signed heartbeat.");
                }
                else
                {
                    AppendPeerStatus(html, peer, now);
                }
                html.AppendLine("</div>");
                html.Append("<div class=\"dragnet-peer-cell dragnet-peer-actions\"><span class=\"dragnet-peer-label\">Actions</span><div class=\"flex flex-wrap items-center justify-end gap-1\">");
                AppendPeerButtons(html, peer);
                html.AppendLine("</div></div>");
                if (!quarantined)
                {
                    AppendPeerGraphRow(
                        html,
                        peer,
                        deliverableEvents,
                        now,
                        _configuration.PeerStaleAfter);
                }
                html.AppendLine("</div>");
            }
        }

        html.AppendLine("</div>");
        html.AppendLine("</div>");
        AppendModalEnd(html);

        var eventControls = new StringBuilder();
        AppendBulkReviewControls(eventControls, bulkApprovableEvents.Count);
        AppendFilterLinks(eventControls, filter);
        AppendModalStart(html, "dragnet-events-modal", "Dragnet events", "ph-list-checks", eventControls.ToString());
        html.AppendLine("<div id=\"dragnet-events\" class=\"space-y-3\">");
        html.AppendLine("<div class=\"flex justify-end\"><label class=\"inline-flex items-center gap-2 text-xs text-muted\"><input type=\"checkbox\" aria-label=\"Select all eligible bans\" onclick=\"document.querySelectorAll('.dragnet-bulk-ban').forEach(function(c){c.checked=this.checked;},this)\">Select eligible</label></div>");
        html.AppendLine("<div class=\"space-y-2\">");

        foreach (var item in eventRows)
        {
            html.Append("<div class=\"rounded-md border border-line bg-surface-alt/20 px-3 py-2 hover:bg-surface-alt/30\" data-event-filters=\"");
            html.Append(Encode(GetEventFilterClasses(item)));
            html.Append("\"");
            if (!EventMatchesFilter(item, filter))
            {
                html.Append(" hidden");
            }
            html.AppendLine("><div class=\"flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between\">");
            html.Append("<div class=\"flex items-start gap-3 min-w-0\">");
            if (IsBulkApprovable(item))
            {
                html.Append("<input type=\"checkbox\" class=\"dragnet-bulk-ban mt-1\" aria-label=\"Select ban for ");
                html.Append(Encode(item.Event.PlayerName));
                html.Append("\" value=\"");
                html.Append(Encode(item.Event.EventId));
                html.Append("\">");
            }

            html.Append("<div class=\"min-w-0\"><div class=\"font-medium truncate\">");
            AppendEventLink(html, item.Event.EventId, item.Event.PlayerName, filter);
            html.Append("</div><div class=\"mt-1 text-xs text-muted truncate\">");
            html.Append(Encode(IsLocalEvent(item.Event) ? "Local" : item.Event.OriginName));
            html.Append(" · ");
            html.Append(Encode(DescribeEventAge(item.Event, now)));
            if (item.ReviewedAtUtc is not null)
            {
                html.Append(" · reviewed by ");
                html.Append(Encode(item.ReviewedByName ?? "Unknown"));
            }
            html.Append("</div></div></div>");
            html.Append("<div class=\"flex flex-wrap items-center gap-2 lg:justify-center\">");
            AppendEventTypeBadge(html, item.Event.EventType);
            AppendReviewStateBadge(html, item.ReviewState);
            AppendImportStatus(html, item, IsLocalEvent(item.Event));
            html.Append("</div><div class=\"flex items-center justify-end gap-1 whitespace-nowrap\">");
            AppendTrustButtons(html, item.Event);
            AppendReviewButtons(html, item);
            html.AppendLine("</div></div></div>");
        }

        if (filteredEvents.Count == 0)
        {
            html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/20 px-4 py-6 text-center text-muted\">No Dragnet events stored.</div>");
        }

        html.AppendLine("</div></div>");
        AppendModalEnd(html);
        AppendRequestedModuleScript(html, meta);
        html.AppendLine("</div>");
        return html.ToString();
    }

    private async Task<string> ProcessNotificationActionAsync(
        int originId,
        IDictionary<string, string>? meta,
        CancellationToken token)
    {
        var manager = _managerFactory();
        var origin = originId > 0 ? await manager.GetClientService().Get(originId) : null;
        if (!HasPermission(origin, _configuration.ReviewPermission) ||
            _notificationService is null)
        {
            return "You are not authorized to manage Dragnet notifications.";
        }

        if (meta is null ||
            !meta.TryGetValue("NotificationAction", out var action))
        {
            return "Invalid Dragnet notification action.";
        }

        if (action.Equals("AcknowledgeAll", StringComparison.OrdinalIgnoreCase))
        {
            var count = await _notificationService.AcknowledgeAllAsync(originId, token);
            return $"Acknowledged {count} Dragnet notification(s).";
        }

        if (action.Equals("Acknowledge", StringComparison.OrdinalIgnoreCase) &&
            meta.TryGetValue("NotificationId", out var notificationId))
        {
            return await _notificationService.AcknowledgeAsync(notificationId, originId, token)
                ? "Dragnet notification acknowledged."
                : "Dragnet notification was not found.";
        }

        return "Invalid Dragnet notification action.";
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

            case "RefreshCoverage":
            {
                var activeOriginBanIds = (await _eventStore.ListAsync(token))
                    .Where(item =>
                        item.Event.EventType is DragnetEventType.BanCreated &&
                        item.Event.OriginId.Equals(_identity.OriginId, StringComparison.OrdinalIgnoreCase) &&
                        !item.Event.IsExpired(DateTimeOffset.UtcNow))
                    .Select(item => item.Event.EventId)
                    .ToList();
                if (activeOriginBanIds.Count == 0)
                {
                    return "This network has no active originated bans to refresh.";
                }

                return await _peerStore.QueueAttestationRefreshAsync(
                    peerOriginId,
                    activeOriginBanIds,
                    token)
                    ? $"Queued coverage refresh for {activeOriginBanIds.Count} active originated ban(s)."
                    : "That Dragnet peer was not found.";
            }

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
        meta.TryGetValue("NotificationsEnabled", out var notificationsEnabledValue);
        meta.TryGetValue("StalePendingReviewHours", out var staleReviewHoursValue);
        meta.TryGetValue("PeerQuarantineMinutes", out var peerQuarantineMinutesValue);
        meta.TryGetValue("QuarantinedPeerProbeMinutes", out var quarantinedPeerProbeMinutesValue);
        meta.TryGetValue("AutoUpdateEnabled", out var autoUpdateEnabledValue);
        meta.TryGetValue("InGameNotificationSummariesEnabled", out var inGameSummariesValue);
        meta.TryGetValue("InGameNotificationSummaryMinutes", out var summaryMinutesValue);
        meta.TryGetValue("NotificationWebhookUrl", out var notificationWebhookUrl);
        directoryRegion = directoryRegion?.Trim();
        directoryWebsite = directoryWebsite?.Trim().TrimEnd('/');
        notificationWebhookUrl = notificationWebhookUrl?.Trim();

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

        if (!int.TryParse(staleReviewHoursValue, out var staleReviewHours) ||
            staleReviewHours is < 1 or > 8760)
        {
            return "Stale review threshold must be between 1 and 8760 hours.";
        }

        if (!int.TryParse(peerQuarantineMinutesValue, out var peerQuarantineMinutes) ||
            peerQuarantineMinutes is < 5 or > 10080)
        {
            return "Peer quarantine delay must be between 5 and 10080 minutes.";
        }

        if (!int.TryParse(quarantinedPeerProbeMinutesValue, out var quarantinedPeerProbeMinutes) ||
            quarantinedPeerProbeMinutes is < 1 or > 1440)
        {
            return "Quarantined peer probe interval must be between 1 and 1440 minutes.";
        }

        if (!int.TryParse(summaryMinutesValue, out var summaryMinutes) ||
            summaryMinutes is < 1 or > 1440)
        {
            return "In-game notification interval must be between 1 and 1440 minutes.";
        }

        if (!string.IsNullOrWhiteSpace(notificationWebhookUrl) &&
            (!Uri.TryCreate(notificationWebhookUrl, UriKind.Absolute, out var webhookUri) ||
             webhookUri.Scheme != Uri.UriSchemeHttps ||
             notificationWebhookUrl.Length > 2048))
        {
            return "Notification webhook must be an absolute HTTPS URL no longer than 2048 characters.";
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
        _configuration.NotificationsEnabled = IsEnabledValue(notificationsEnabledValue);
        _configuration.StalePendingReviewAfter = TimeSpan.FromHours(staleReviewHours);
        _configuration.PeerQuarantineAfter = TimeSpan.FromMinutes(peerQuarantineMinutes);
        _configuration.QuarantinedPeerProbeInterval = TimeSpan.FromMinutes(quarantinedPeerProbeMinutes);
        _configuration.AutoUpdateEnabled = IsEnabledValue(autoUpdateEnabledValue);
        _configuration.InGameNotificationSummariesEnabled = IsEnabledValue(inGameSummariesValue);
        _configuration.InGameNotificationSummaryInterval = TimeSpan.FromMinutes(summaryMinutes);
        _configuration.NotificationWebhookUrl = string.IsNullOrWhiteSpace(notificationWebhookUrl)
            ? null
            : notificationWebhookUrl;
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
        html.AppendLine("<div class=\"rounded-lg border border-line bg-surface/50 p-2\">");
        html.AppendLine("<div class=\"px-3 py-2 flex flex-col gap-2 lg:flex-row lg:items-start lg:justify-between\">");
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

        html.AppendLine("<div class=\"grid grid-cols-1 lg:grid-cols-3 gap-2 p-2\">");
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
            html.AppendLine("<div class=\"overflow-x-auto rounded-md bg-surface-alt/20\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted\"><tr><th class=\"px-3 py-2\">When</th><th class=\"px-3 py-2\">Reviewer</th><th class=\"px-3 py-2\">Change</th><th class=\"px-3 py-2\">Reason</th></tr></thead><tbody>");

            foreach (var entry in item.AuditTrail.OrderByDescending(entry => entry.ReviewedAtUtc))
            {
                html.AppendLine("<tr class=\"hover:bg-surface-alt/30\">");
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
        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/30 p-4\">");
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
            case DragnetReviewState.DeniedBan:
            case DragnetReviewState.IgnoredBan:
                if (isTrusted)
                {
                    AppendActionButton(html, item.Event.EventId, DragnetReviewAction.ApproveBan, "Approve", "ph-check");
                }

                if (item.ReviewState is not DragnetReviewState.DeniedBan)
                {
                    AppendActionButton(html, item.Event.EventId, DragnetReviewAction.DenyBan, "Deny", "ph-x", includeReason: true);
                }

                if (item.ReviewState is not DragnetReviewState.IgnoredBan)
                {
                    AppendActionButton(html, item.Event.EventId, DragnetReviewAction.IgnoreBan, "Ignore", "ph-eye-slash");
                }
                break;

            case DragnetReviewState.PendingLift:
            case DragnetReviewState.DeniedLift:
            case DragnetReviewState.IgnoredLift:
                if (isTrusted)
                {
                    AppendActionButton(html, item.Event.EventId, DragnetReviewAction.ApproveLift, "Approve lift", "ph-check");
                }

                if (item.ReviewState is not DragnetReviewState.DeniedLift)
                {
                    AppendActionButton(html, item.Event.EventId, DragnetReviewAction.DenyLift, "Deny lift", "ph-x", includeReason: true);
                }

                if (item.ReviewState is not DragnetReviewState.IgnoredLift)
                {
                    AppendActionButton(html, item.Event.EventId, DragnetReviewAction.IgnoreLift, "Ignore", "ph-eye-slash");
                }
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
            ["ShouldRefresh"] = "false",
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
        html.Append("',ActionButtonLabel:'Approve selected',Name:'Approve selected bans',ShouldRefresh:'false',Inputs:JSON.stringify(inputs)};var trigger=document.getElementById('dragnet-bulk-trigger');trigger.dataset.actionMeta=encodeURIComponent(JSON.stringify(meta));trigger.click();})()\"><i class=\"ph ph-checks mr-1\"></i>Approve selected</button>");
        html.Append("<button id=\"dragnet-bulk-trigger\" type=\"button\" class=\"profile-action hidden\" data-action=\"DynamicAction\" data-action-meta=\"\"></button></div>");
    }

    private static void AppendNotificationInbox(
        StringBuilder html,
        IReadOnlyList<DragnetNotification> notifications,
        DragnetEventFilter filter,
        DateTimeOffset now)
    {
        if (notifications.Count == 0)
        {
            html.AppendLine("<div class=\"rounded-md bg-surface-alt/20 px-4 py-5 text-center text-muted\">No unread Dragnet notifications.</div>");
            return;
        }

        html.AppendLine("<div class=\"space-y-2\">");
        foreach (var notification in notifications.Take(20))
        {
            html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/30 px-4 py-2 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between\">");
            html.Append("<div><div class=\"font-medium\">");
            html.Append(Encode(notification.Title));
            html.Append("</div><div class=\"text-sm\">");
            html.Append(Encode(notification.Message));
            html.Append("</div><div class=\"text-xs text-muted mt-1\">");
            html.Append(Encode(DescribeAge(now - notification.CreatedAtUtc)));
            html.Append(" · ");
            html.Append(Encode(notification.Type.ToString()));
            html.Append("</div></div><div class=\"flex items-center gap-2\">");
            if (!string.IsNullOrWhiteSpace(notification.EventId))
            {
                html.Append("<button type=\"button\" class=\"inline-flex items-center justify-center w-10 px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\" title=\"Open events\" aria-label=\"Open events\" onclick=\"dragnetOpenModal('dragnet-events-modal')\"><i class=\"ph ph-arrow-square-out\"></i></button>");
            }
            AppendNotificationActionButton(
                html,
                notification.NotificationId,
                "Acknowledge",
                "Acknowledge",
                "ph-check");
            html.AppendLine("</div></div>");
        }
        html.AppendLine("</div>");
    }

    private static string BuildNotificationModuleControls(IReadOnlyList<DragnetNotification> notifications)
    {
        if (notifications.Count == 0)
        {
            return "";
        }

        var html = new StringBuilder();
        AppendNotificationActionButton(html, null, "AcknowledgeAll", "Acknowledge all", "ph-checks");
        return html.ToString();
    }

    private static void AppendPeerGraphRow(
        StringBuilder html,
        DragnetPeerRecord peer,
        IReadOnlyList<DragnetStoredEvent> deliverableEvents,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        var acknowledged = peer.EventDeliveries?.Count(delivery =>
            delivery.AcknowledgedAtUtc is not null &&
            deliverableEvents.Any(item =>
                item.Event.EventId.Equals(delivery.EventId, StringComparison.OrdinalIgnoreCase))) ?? 0;
        var pending = Math.Max(0, deliverableEvents.Count - acknowledged);
        var sentPending = CountSentPendingDeliveries(peer, deliverableEvents);
        var pendingDeliveries = (peer.EventDeliveries ?? [])
            .Where(delivery =>
                delivery.AcknowledgedAtUtc is null &&
                deliverableEvents.Any(item =>
                    item.Event.EventId.Equals(delivery.EventId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(delivery => delivery.FirstSentAtUtc)
            .ToList();
        var health = DragnetPeerHealth.Assess(
            peer,
            now,
            staleAfter,
            pendingDeliveries.Count,
            pendingDeliveries.FirstOrDefault()?.FirstSentAtUtc);
        double? successRate = peer.HeartbeatAttemptCount == 0
            ? null
            : peer.HeartbeatSuccessCount * 100d / peer.HeartbeatAttemptCount;
        var acknowledgementPercent = deliverableEvents.Count == 0
            ? 100
            : (int)Math.Round(acknowledged * 100d / deliverableEvents.Count);
        var healthPhase = peer.ConsecutiveFailures == 0
            ? 0
            : Math.Min(18, peer.ConsecutiveFailures * 3);

        html.AppendLine("<div class=\"dragnet-peer-detail\">");
        html.AppendLine("<div class=\"p-4\">");
        html.AppendLine("<div class=\"rounded-md p-3\" style=\"background:linear-gradient(135deg,rgba(69,163,255,.16),rgba(82,210,115,.10) 46%,rgba(240,184,75,.10));border:1px solid rgba(143,199,255,.28)\">");
        html.AppendLine("<div class=\"flex flex-col gap-2 md:flex-row md:items-center md:justify-between text-xs text-muted\"><span>Heartbeat, delivery acknowledgement, and retry pressure</span><span>live peer signal</span></div>");
        html.AppendLine("<div class=\"mt-2 flex flex-wrap gap-2 text-xs\"><span class=\"inline-flex items-center gap-1\"><span style=\"width:10px;height:10px;border-radius:999px;background:#45a3ff\"></span>Heartbeat</span><span class=\"inline-flex items-center gap-1\"><span style=\"width:10px;height:10px;border-radius:999px;background:#52d273\"></span>Acknowledged</span><span class=\"inline-flex items-center gap-1\"><span style=\"width:10px;height:10px;border-radius:999px;background:#f0b84b\"></span>Pending</span><span class=\"inline-flex items-center gap-1\"><span style=\"width:10px;height:10px;border-radius:999px;background:#ff6b6b\"></span>Failure pressure</span></div>");
        html.AppendLine("<svg class=\"dragnet-sine\" viewBox=\"0 0 420 74\" role=\"img\" aria-label=\"Peer activity wave\">");
        html.AppendLine("<path d=\"M0 61 H420 M0 37 H420 M0 13 H420\" stroke=\"currentColor\" stroke-opacity=\"0.08\" stroke-width=\"1\"/>");
        html.AppendLine("<path d=\"M0 37 C 35 12, 70 12, 105 37 S 175 62, 210 37 S 280 12, 315 37 S 385 62, 420 37\" fill=\"none\" stroke=\"#45a3ff\" stroke-opacity=\"0.30\" stroke-width=\"3\"/>");
        html.AppendLine("<path d=\"M0 54 C 35 44, 70 44, 105 54 S 175 64, 210 54 S 280 44, 315 54 S 385 64, 420 54\" fill=\"none\" stroke=\"#52d273\" stroke-opacity=\"0.70\" stroke-width=\"2\"/>");
        html.Append("<path d=\"M0 ");
        html.Append(37 + healthPhase);
        html.Append(" C 35 ");
        html.Append(12 + healthPhase);
        html.Append(", 70 ");
        html.Append(12 + healthPhase);
        html.Append(", 105 ");
        html.Append(37 + healthPhase);
        html.Append(" S 175 ");
        html.Append(62 - healthPhase);
        html.Append(", 210 37 S 280 ");
        html.Append(12 + healthPhase);
        html.Append(", 315 37 S 385 ");
        html.Append(62 - healthPhase);
        html.AppendLine(", 420 37\" fill=\"none\" stroke=\"#f0b84b\" stroke-width=\"3\"/>");
        html.AppendLine("</svg>");
        html.Append("<div class=\"grid grid-cols-2 md:grid-cols-5 gap-2 text-xs\"><div><span class=\"text-muted\">Health</span><div class=\"font-semibold\">");
        html.Append(health.Score);
        html.Append("/100</div></div><div><span class=\"text-muted\">Latency</span><div class=\"font-semibold\">");
        html.Append(peer.AverageHeartbeatLatencyMs is null
            ? "Unknown"
            : Encode($"{peer.AverageHeartbeatLatencyMs:0} ms avg"));
        html.Append("</div></div><div><span class=\"text-muted\">Success</span><div class=\"font-semibold\">");
        html.Append(successRate is null ? "Unknown" : Encode($"{successRate:0.#}%"));
        html.Append("</div></div><div><span class=\"text-muted\">Ack</span><div class=\"font-semibold\">");
        html.Append(acknowledged);
        html.Append(" / ");
        html.Append(deliverableEvents.Count);
        html.Append("</div></div><div><span class=\"text-muted\">Reliability</span><div class=\"font-semibold\">");
        html.Append(acknowledgementPercent);
        html.Append("%</div></div><div><span class=\"text-muted\">Sent pending</span><div class=\"font-semibold\">");
        html.Append(sentPending);
        html.Append("</div></div><div><span class=\"text-muted\">Pending</span><div class=\"font-semibold\">");
        html.Append(pending);
        html.Append("</div></div><div><span class=\"text-muted\">Servers</span><div class=\"font-semibold\">");
        html.Append(peer.ServerCount);
        html.Append("</div></div><div><span class=\"text-muted\">Failures</span><div class=\"font-semibold\">");
        html.Append(peer.ConsecutiveFailures);
        html.AppendLine("</div></div></div>");
        if (health.Causes.Count > 0)
        {
            html.Append("<div class=\"mt-2 text-xs text-warning\">");
            html.Append(Encode(string.Join(" · ", health.Causes)));
            html.AppendLine("</div>");
        }
        html.Append("<div class=\"mt-2\"><button type=\"button\" class=\"text-primary hover:underline\" onclick=\"dragnetOpenModal('network-profile-");
        html.Append(Encode(peer.OriginId));
        html.Append("')\">Open network profile</button></div>");
        html.AppendLine("</div></div></div>");
    }

    private static void AppendNotificationActionButton(
        StringBuilder html,
        string? notificationId,
        string action,
        string label,
        string icon)
    {
        var inputs = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Name"] = "NotificationAction",
                ["Type"] = "hidden",
                ["Value"] = action
            }
        };
        if (!string.IsNullOrWhiteSpace(notificationId))
        {
            inputs.Add(new Dictionary<string, object?>
            {
                ["Name"] = "NotificationId",
                ["Type"] = "hidden",
                ["Value"] = notificationId
            });
        }

        var meta = new Dictionary<string, string>
        {
            ["InteractionId"] = NotificationInteractionId,
            ["ActionButtonLabel"] = label,
            ["Name"] = label,
            ["ShouldRefresh"] = "true",
            ["Inputs"] = JsonSerializer.Serialize(inputs)
        };
        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer\" data-action=\"DynamicAction\" onpointerdown=\"dragnetPrepareDynamicAction(this)\" onclick=\"dragnetPrepareDynamicAction(this)\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append(" mr-1\"></i>");
        html.Append(Encode(label));
        html.Append("</span></button>");
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
            ["ShouldRefresh"] = "false",
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
            ["ShouldRefresh"] = "false",
            ["Inputs"] = BuildTrustInputs(envelope, trustAction)
        };

        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer\" title=\"");
        html.Append(Encode(label));
        html.Append("\" aria-label=\"");
        html.Append(Encode(label));
        html.Append("\" data-action=\"DynamicAction\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center justify-center w-10 px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append("\"></i></span></button>");
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
        if (peer.SupportsAttestationRefreshRequests)
        {
            AppendPeerActionButton(
                html,
                peer,
                "RefreshCoverage",
                "Refresh coverage",
                "ph-broadcast");
        }
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
            ["ShouldRefresh"] = "false",
            ["Inputs"] = BuildPeerInputs(peer, peerAction)
        };

        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer ml-2\" title=\"");
        html.Append(Encode(label));
        html.Append("\" aria-label=\"");
        html.Append(Encode(label));
        html.Append("\" data-action=\"DynamicAction\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center justify-center w-10 px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append("\"></i></span></button>");
    }

    private void AppendPeerStatus(
        StringBuilder html,
        DragnetPeerRecord peer,
        DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(peer.LastError))
        {
            AppendPeerStatusBadge(html, "Errored", "ph-x-circle", "text-danger", Shorten(peer.LastError, 120));
            return;
        }

        if (IsStalePeer(peer, now))
        {
            AppendPeerStatusBadge(html, "Stale", "ph-clock-counter-clockwise", "text-warning", "Last heartbeat is older than the configured stale window.");
            return;
        }

        if (peer.ConsecutiveFailures > 0)
        {
            AppendPeerStatusBadge(html, $"Retrying {peer.ConsecutiveFailures}/{Math.Max(1, _configuration.PeerFailureThreshold)}", "ph-warning-circle", "text-warning", "Peer has recent transport failures but is still being probed.");
            return;
        }

        AppendPeerStatusBadge(html, "Healthy", "ph-check-circle", "text-success", "Peer heartbeat and transport state are healthy.");
    }

    private static void AppendPeerStatusBadge(
        StringBuilder html,
        string label,
        string icon,
        string css,
        string title)
    {
        html.Append("<span class=\"dragnet-status-badge ");
        html.Append(css);
        html.Append("\" title=\"");
        html.Append(Encode(title));
        html.Append("\"><i class=\"ph ");
        html.Append(icon);
        html.Append("\"></i><span>");
        html.Append(Encode(label));
        html.Append("</span></span>");
    }

    private void AppendTrustStatus(StringBuilder html, DragnetEventEnvelope envelope)
    {
        if (IsLocalEvent(envelope))
        {
            AppendCompactBadge(html, "Local", "ph-house", "text-primary", "Local outbound event");
            return;
        }

        var trust = _trustService.Evaluate(envelope);
        if (!trust.IsTrusted)
        {
            AppendCompactBadge(html, "Untrusted", "ph-shield-warning", "text-danger", "This origin is not trusted by this network.");
            return;
        }

        if (trust.AutoApprove)
        {
            AppendCompactBadge(html, "Auto", "ph-shield-check", "text-success", "Trusted origin with automatic approval enabled.");
            return;
        }

        AppendCompactBadge(html, "Trusted", "ph-shield-check", "text-success", "Trusted origin.");
    }

    private static void AppendImportStatus(
        StringBuilder html,
        DragnetStoredEvent item,
        bool isLocal)
    {
        if (isLocal)
        {
            AppendCompactBadge(html, "Outbound", "ph-arrow-square-out", "text-primary", "Local event sent to peers.");
            return;
        }

        if (item.ImportedAtUtc is not null)
        {
            AppendCompactBadge(html, "Imported", "ph-check-circle", "text-success", "Imported into local IW4MAdmin state.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.ImportError))
        {
            if (item.ImportError.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase))
            {
                AppendCompactBadge(html, "Queued", "ph-clock", "text-warning", "Import is queued.");
                return;
            }

            AppendCompactBadge(html, "Failed", "ph-x-circle", "text-danger", Shorten(item.ImportError, 120));
            return;
        }

        AppendCompactBadge(html, "Pending", "ph-hourglass", "text-muted", "Awaiting import.");
    }

    private static void AppendEventTypeBadge(StringBuilder html, DragnetEventType eventType)
    {
        var (label, icon, css, title) = eventType switch
        {
            DragnetEventType.BanCreated => ("Ban", "ph-gavel", "text-danger", "Ban issued by an origin network."),
            DragnetEventType.BanLifted => ("Lift", "ph-arrow-u-up-left", "text-success", "Ban lift issued by an origin network."),
            _ => (eventType.ToString(), "ph-dot-outline", "text-muted", eventType.ToString())
        };
        AppendCompactBadge(html, label, icon, css, title);
    }

    private static void AppendReviewStateBadge(StringBuilder html, DragnetReviewState state)
    {
        var (icon, css, title) = state switch
        {
            DragnetReviewState.PendingBan or DragnetReviewState.PendingLift => ("ph-hourglass", "text-warning", "Needs local review."),
            DragnetReviewState.ApprovedBan or DragnetReviewState.ApprovedLift => ("ph-check-circle", "text-success", "Approved locally."),
            DragnetReviewState.DeniedBan or DragnetReviewState.DeniedLift => ("ph-x-circle", "text-danger", "Denied locally."),
            DragnetReviewState.IgnoredBan or DragnetReviewState.IgnoredLift => ("ph-eye-slash", "text-muted", "Ignored locally."),
            DragnetReviewState.ExpiredBan => ("ph-clock-counter-clockwise", "text-muted", "Expired before review."),
            _ => ("ph-dot-outline", "text-muted", state.ToString())
        };
        AppendCompactBadge(html, ShortReviewState(state), icon, css, title);
    }

    private static string ShortReviewState(DragnetReviewState state) => state switch
    {
        DragnetReviewState.PendingBan => "Pending ban",
        DragnetReviewState.PendingLift => "Pending lift",
        DragnetReviewState.ApprovedBan => "Approved ban",
        DragnetReviewState.ApprovedLift => "Approved lift",
        DragnetReviewState.DeniedBan => "Denied ban",
        DragnetReviewState.DeniedLift => "Denied lift",
        DragnetReviewState.IgnoredBan => "Ignored ban",
        DragnetReviewState.IgnoredLift => "Ignored lift",
        DragnetReviewState.ExpiredBan => "Expired ban",
        _ => state.ToString()
    };

    private static void AppendCompactBadge(
        StringBuilder html,
        string label,
        string icon,
        string css,
        string title)
    {
        html.Append("<span class=\"dragnet-status-badge ");
        html.Append(css);
        html.Append("\" title=\"");
        html.Append(Encode(title));
        html.Append("\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append("\"></i><span>");
        html.Append(Encode(label));
        html.Append("</span></span>");
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
            ["ShouldRefresh"] = "false",
            ["Inputs"] = BuildReviewInputs(eventId, action, includeReason)
        };

        var encodedMeta = Uri.EscapeDataString(JsonSerializer.Serialize(meta));
        html.Append("<button type=\"button\" class=\"profile-action cursor-pointer ml-2\" title=\"");
        html.Append(Encode(label));
        html.Append("\" aria-label=\"");
        html.Append(Encode(label));
        html.Append("\" data-action=\"DynamicAction\" data-action-meta=\"");
        html.Append(Encode(encodedMeta));
        html.Append("\"><span class=\"inline-flex items-center justify-center w-10 px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover text-sm\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append("\"></i></span></button>");
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

    private static void AppendEventMetric(StringBuilder html, string label, int value, string icon)
    {
        html.Append("<div class=\"rounded-md border border-line bg-surface-alt/30 px-3 py-2\"><div class=\"flex items-center gap-2 text-xs text-muted\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append("\"></i>");
        html.Append(Encode(label));
        html.Append("</div><div class=\"mt-1 text-xl font-semibold\">");
        html.Append(value);
        html.AppendLine("</div></div>");
    }

    private static void AppendDashboardStyles(StringBuilder html)
    {
        html.AppendLine("""
<style>
:root{color-scheme:dark;--color-line:#3a3042;--color-muted:#a9a1b5;--color-foreground:#f6f2fb}
.mx-auto{margin-left:auto;margin-right:auto}.mb-4{margin-bottom:1rem}.mr-1{margin-right:.25rem}.mr-2{margin-right:.5rem}.mt-1{margin-top:.25rem}.mt-2{margin-top:.5rem}.p-3{padding:.75rem}.p-4{padding:1rem}.px-2{padding-left:.5rem;padding-right:.5rem}.px-3{padding-left:.75rem;padding-right:.75rem}.px-4{padding-left:1rem;padding-right:1rem}.py-1\.5{padding-top:.375rem;padding-bottom:.375rem}.py-2{padding-top:.5rem;padding-bottom:.5rem}.py-6{padding-top:1.5rem;padding-bottom:1.5rem}.pb-3{padding-bottom:.75rem}
.flex{display:flex}.inline-flex{display:inline-flex}.grid{display:grid}.hidden{display:none}.items-center{align-items:center}.items-start{align-items:flex-start}.items-end{align-items:flex-end}.justify-between{justify-content:space-between}.justify-end{justify-content:flex-end}.justify-center{justify-content:center}.gap-1{gap:.25rem}.gap-2{gap:.5rem}.gap-3{gap:.75rem}.space-y-2>*+*{margin-top:.5rem}.space-y-3>*+*{margin-top:.75rem}.space-y-4>*+*{margin-top:1rem}.flex-col{flex-direction:column}.flex-wrap{flex-wrap:wrap}
.w-full{width:100%}.w-10{width:2.5rem}.max-w-5xl{max-width:64rem}.max-w-6xl{max-width:72rem}.min-w-0{min-width:0}.overflow-x-auto{overflow-x:auto}.truncate{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.break-all{word-break:break-all}.whitespace-nowrap{white-space:nowrap}.whitespace-pre-wrap{white-space:pre-wrap}
.rounded-md{border-radius:6px}.rounded-lg{border-radius:8px}.rounded-full{border-radius:999px}.border{border-width:1px;border-style:solid}.border-line{border-color:var(--color-line,#3a3042)}.bg-surface\/50{background:rgba(255,255,255,.035)}.bg-surface-alt\/20{background:rgba(255,255,255,.025)}.bg-surface-alt\/30{background:rgba(255,255,255,.04)}.hover\:bg-surface-hover:hover,.hover\:bg-surface-alt\/30:hover{background:rgba(255,255,255,.06)}
.text-left{text-align:left}.text-center{text-align:center}.text-right{text-align:right}.text-xs{font-size:12px}.text-sm{font-size:14px}.text-base{font-size:16px}.text-lg{font-size:18px}.text-2xl{font-size:24px}.font-medium{font-weight:600}.font-semibold{font-weight:700}.uppercase{text-transform:uppercase}.tracking-wide{letter-spacing:.04em}.text-muted{color:var(--color-muted,#a9a1b5)}.text-foreground{color:var(--color-foreground,#f6f2fb)}.text-primary{color:#f72585}.text-success{color:#52d273}.text-warning{color:#f0b84b}.text-danger{color:#ff6b6b}.text-info{color:#45a3ff}.hover\:underline:hover{text-decoration:underline}table{border-collapse:collapse}
@media(min-width:768px){.md\:flex-row{flex-direction:row}.md\:items-end{align-items:flex-end}.md\:grid-cols-2{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media(min-width:1024px){.lg\:flex-row{flex-direction:row}.lg\:items-center{align-items:center}.lg\:justify-between{justify-content:space-between}.lg\:justify-center{justify-content:center}.lg\:grid-cols-2{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media(min-width:1280px){.xl\:grid-cols-3{grid-template-columns:repeat(3,minmax(0,1fr))}.xl\:col-span-3{grid-column:span 3/span 3}}
.dragnet-top-nav{position:sticky;top:4rem}
.dragnet-modal{position:fixed;inset:0;margin:auto;width:fit-content;min-width:min(620px,calc(100vw - 32px));max-width:min(1120px,calc(100vw - 32px));max-height:86vh;overflow:visible;border:1px solid var(--color-line,#3a3042);border-radius:12px;background:#17111d;color:inherit;padding:0;opacity:0;transform:scale(.94);transition:opacity .16s ease,transform .16s ease,width .18s ease,max-height .22s ease}
.dragnet-modal[open]{opacity:1;transform:scale(1);animation:dragnetZoomIn .16s ease}
.dragnet-modal.closing{opacity:0;transform:scale(.94)}
.dragnet-modal::backdrop{background:rgba(0,0,0,.66)}
.dragnet-modal-head{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:14px 16px;border-bottom:1px solid var(--color-line,#3a3042);cursor:move;user-select:none}
.dragnet-modal-body{padding:16px;overflow:auto;max-height:calc(86vh - 58px)}
#dragnet-peer-modal{width:min(1040px,calc(100vw - 32px));min-width:0}
#dragnet-peer-modal .dragnet-modal-body{overflow-x:hidden}
.dragnet-modal-close{border:1px solid var(--color-line,#3a3042);border-radius:8px;padding:6px 10px}
.dragnet-icon-button{position:relative;width:38px;height:34px;justify-content:center}
.dragnet-icon-button[data-tip]:hover:after{content:attr(data-tip);position:absolute;right:0;top:calc(100% + 8px);z-index:30;white-space:nowrap;border:1px solid var(--color-line,#3a3042);border-radius:6px;background:#0b0d10;color:inherit;padding:5px 8px;font-size:12px}
.dragnet-modal table tbody tr{border-bottom:1px solid rgba(255,255,255,.08)}
.dragnet-modal table tbody tr:last-child{border-bottom:0}
.dragnet-modal table th,.dragnet-modal table td{border-right:1px solid rgba(255,255,255,.07)}
.dragnet-modal table th:last-child,.dragnet-modal table td:last-child{border-right:0}
.dragnet-status-badge{display:inline-flex;align-items:center;gap:6px;border:1px solid currentColor;border-radius:999px;padding:3px 8px;font-size:12px;font-weight:700;white-space:nowrap}
.dragnet-detail-card{border:1px solid var(--color-line,#3a3042);border-radius:8px;background:rgba(255,255,255,.035);padding:12px}
.dragnet-detail-label{display:flex;align-items:center;gap:6px;color:var(--color-muted,#a9a1b5);font-size:12px}
.dragnet-detail-value{margin-top:6px;color:var(--color-foreground,#f6f2fb);font-weight:600;line-height:1.35;word-break:break-word}
.dragnet-chevron{display:inline-block;width:8px;height:8px;border:solid currentColor;border-width:0 2px 2px 0;transform:rotate(-135deg);transition:transform .18s ease}
details[open] > summary .dragnet-chevron,.dragnet-chevron.open{transform:rotate(45deg)}
.dragnet-peer-list{display:grid;gap:8px;min-width:0}
.dragnet-peer-empty{padding:24px;text-align:center;color:var(--color-muted,#a9a1b5);border-radius:8px;background:rgba(255,255,255,.025)}
.dragnet-peer-row{display:grid;grid-template-columns:minmax(220px,1.6fr) minmax(135px,1fr) minmax(170px,1.2fr) minmax(120px,.8fr) minmax(120px,.8fr);gap:12px;align-items:start;min-width:0;padding:12px;border-radius:8px;background:rgba(255,255,255,.025)}
.dragnet-peer-row:hover{background:rgba(255,255,255,.045)}
.dragnet-peer-cell{min-width:0;color:var(--color-foreground,#f6f2fb);font-size:14px}
.dragnet-peer-label{display:block;margin-bottom:5px;color:var(--color-muted,#a9a1b5);font-size:11px;font-weight:700;text-transform:uppercase}
.dragnet-peer-endpoint,.dragnet-peer-meta{margin-top:4px;color:var(--color-muted,#a9a1b5);font-size:12px;overflow-wrap:anywhere}
.dragnet-peer-actions{text-align:right}
.dragnet-peer-actions .profile-action{margin-left:0}
.dragnet-peer-detail{grid-column:1/-1;max-height:0;overflow:hidden;transition:max-height .24s ease}
.dragnet-peer-row.open .dragnet-peer-detail{max-height:760px}
.dragnet-sine{width:100%;height:74px}
.dragnet-diagnostics-peer-grid{display:grid;grid-template-columns:minmax(0,1.5fr) repeat(4,minmax(90px,.7fr));gap:12px}
@media(max-width:900px){.dragnet-peer-row{grid-template-columns:minmax(0,1fr) minmax(0,1fr)}.dragnet-peer-actions{text-align:left}.dragnet-peer-actions>div{justify-content:flex-start}}
@media(max-width:900px){.dragnet-diagnostics-peer-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.dragnet-diagnostics-peer-grid>div:first-child{grid-column:1/-1}}
@media(max-width:560px){.dragnet-modal{min-width:calc(100vw - 16px);max-width:calc(100vw - 16px)}.dragnet-modal-head{align-items:flex-start}.dragnet-peer-row,.dragnet-diagnostics-peer-grid{grid-template-columns:minmax(0,1fr)}.dragnet-diagnostics-peer-grid>div:first-child{grid-column:auto}}
@keyframes dragnetZoomIn{from{opacity:0;transform:scale(.94)}to{opacity:1;transform:scale(1)}}
</style>
<script>
function dragnetOpenModal(id){var d=document.getElementById(id);if(!d)return;d.classList.remove('closing');if(d.showModal)d.showModal();else d.setAttribute('open','open');}
function dragnetCloseDialog(d){if(!d)return;d.classList.add('closing');setTimeout(function(){if(d.close)d.close();else d.removeAttribute('open');d.classList.remove('closing');d.style.left='';d.style.top='';d.style.margin='auto';},160);}
function dragnetCloseModal(button){dragnetCloseDialog(button.closest('dialog'));}
function dragnetPrepareDynamicAction(button){var d=button.closest('dialog');if(!d)return;if(d.close)d.close();else d.removeAttribute('open');d.classList.remove('closing');d.style.left='';d.style.top='';d.style.margin='auto';}
function dragnetLedgerPage(page){document.querySelectorAll('[data-ledger-page]').forEach(function(row){row.hidden=row.getAttribute('data-ledger-page')!==String(page);});document.querySelectorAll('[data-ledger-current]').forEach(function(el){el.textContent=page;});}
function dragnetFilterEvents(filter){document.querySelectorAll('[data-event-filters]').forEach(function(row){var filters=row.getAttribute('data-event-filters').split(' ');row.hidden=filters.indexOf(filter)<0;});document.querySelectorAll('[data-dragnet-filter]').forEach(function(btn){var active=btn.getAttribute('data-dragnet-filter')===filter;btn.classList.toggle('bg-action-primary',active);btn.classList.toggle('text-foreground',active);btn.classList.toggle('border-action-primary',active);btn.classList.toggle('text-muted',!active);});}
document.addEventListener('mousedown',function(e){var head=e.target.closest('.dragnet-modal-head');if(!head||e.target.closest('button'))return;var d=head.closest('dialog');if(!d)return;var r=d.getBoundingClientRect();var x=e.clientX-r.left;var y=e.clientY-r.top;d.style.margin='0';d.style.left=r.left+'px';d.style.top=r.top+'px';function move(ev){d.style.left=Math.max(8,Math.min(window.innerWidth-r.width-8,ev.clientX-x))+'px';d.style.top=Math.max(8,Math.min(window.innerHeight-r.height-8,ev.clientY-y))+'px';}function up(){document.removeEventListener('mousemove',move);document.removeEventListener('mouseup',up);}document.addEventListener('mousemove',move);document.addEventListener('mouseup',up);});
document.addEventListener('click',function(e){var d=e.target;if(d instanceof HTMLDialogElement&&d.classList.contains('dragnet-modal'))dragnetCloseDialog(d);});
</script>
""");
    }

    private static void AppendModalStart(
        StringBuilder html,
        string id,
        string title,
        string icon,
        string? controls = null)
    {
        html.Append("<dialog id=\"");
        html.Append(Encode(id));
        html.Append("\" class=\"dragnet-modal\"><div class=\"dragnet-modal-head\"><h3 class=\"text-lg font-semibold\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append(" mr-2\"></i>");
        html.Append(Encode(title));
        html.Append("</h3><div class=\"flex flex-wrap items-center gap-2\">");
        if (!string.IsNullOrWhiteSpace(controls))
        {
            html.Append(controls);
        }
        html.Append("<button type=\"button\" class=\"dragnet-modal-close\" onclick=\"dragnetCloseModal(this)\">Close</button></div></div><div class=\"dragnet-modal-body\">");
    }

    private static void AppendModalEnd(StringBuilder html)
    {
        html.AppendLine("</div></dialog>");
    }

    private static string BuildLedgerModuleControls(DragnetLedgerSnapshot snapshot, int page)
    {
        const int pageSize = 8;
        var pageCount = Math.Max(1, (int)Math.Ceiling(snapshot.Bans.Count / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);
        var html = new StringBuilder();
        html.Append("<div class=\"inline-flex items-center gap-1 text-sm\"><button type=\"button\" class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover\" onclick=\"dragnetLedgerPage(Math.max(1,parseInt(document.querySelector('[data-ledger-current]').textContent)-1))\">Prev</button><span class=\"text-muted px-2\"><span data-ledger-current>");
        html.Append(page);
        html.Append("</span> / ");
        html.Append(pageCount);
        html.Append("</span><button type=\"button\" class=\"inline-flex items-center px-3 py-1.5 rounded-md border border-line hover:bg-surface-hover\" onclick=\"dragnetLedgerPage(Math.min(");
        html.Append(pageCount);
        html.Append(",parseInt(document.querySelector('[data-ledger-current]').textContent)+1))\">Next</button></div>");
        return html.ToString();
    }

    private static void AppendLedgerModule(StringBuilder html, DragnetLedgerSnapshot snapshot, int page)
    {
        const int pageSize = 8;
        var pageCount = Math.Max(1, (int)Math.Ceiling(snapshot.Bans.Count / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);
        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/20 overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted\"><tr><th class=\"px-4 py-2\">Player</th><th class=\"px-4 py-2\">Origin</th><th class=\"px-4 py-2\">Type</th><th class=\"px-4 py-2\">Platform</th><th class=\"px-4 py-2\">Status</th><th class=\"px-4 py-2\">Accepted</th><th class=\"px-4 py-2\">Servers</th><th class=\"px-4 py-2\">Issued</th></tr></thead><tbody>");
        var index = 0;
        foreach (var ban in snapshot.Bans)
        {
            var rowPage = index++ / pageSize + 1;
            html.Append("<tr class=\"hover:bg-surface-alt/30\" data-ledger-page=\"");
            html.Append(rowPage);
            html.Append("\"");
            if (rowPage != page)
            {
                html.Append(" hidden");
            }
            html.Append("><td class=\"px-4 py-2 font-medium\"><button type=\"button\" class=\"text-primary hover:underline\" onclick=\"dragnetOpenModal('ledger-detail-");
            html.Append(Encode(ban.EventId));
            html.Append("')\">");
            html.Append(Encode(ban.PlayerName));
            html.Append("</button><div class=\"text-xs text-muted\">");
            html.Append(Encode(ban.PlayerNetworkId));
            html.Append("</div></td><td class=\"px-4 py-2\">");
            html.Append("<div class=\"font-medium text-foreground\"><i class=\"ph ph-globe mr-1 text-muted\"></i>");
            html.Append(Encode(ban.OriginName));
            html.Append("</div><div class=\"mt-1 text-xs text-muted\"><i class=\"ph ph-server mr-1\"></i>");
            html.Append(Encode(ban.OriginServerName));
            html.Append("</div></td><td class=\"px-4 py-2\">");
            html.Append(Encode(ban.PenaltyKind));
            html.Append("</td><td class=\"px-4 py-2\">");
            html.Append(Encode(string.IsNullOrWhiteSpace(ban.PlayerGame) ? "Unknown" : ban.PlayerGame));
            html.Append("</td><td class=\"px-4 py-2\">");
            AppendLedgerStatusIcon(html, ban.Status);
            html.Append("</td><td class=\"px-4 py-2\">");
            html.Append(Encode($"{ban.AcceptedNetworkCount} / {ban.EligibleNetworkCount}"));
            html.Append("</td><td class=\"px-4 py-2\">");
            html.Append(ban.EnforcedServerCount);
            html.Append("</td><td class=\"px-4 py-2 text-muted\">");
            html.Append(Encode(FormatLedgerIssuedTime(ban.CreatedAtUtc)));
            html.AppendLine("</td></tr>");
        }
        if (snapshot.Bans.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"8\" class=\"px-4 py-6 text-center text-muted\">No bans in the public ledger.</td></tr>");
        }
        html.AppendLine("</tbody></table></div>");
    }

    private static void AppendLedgerDetailModals(StringBuilder html, DragnetLedgerSnapshot snapshot)
    {
        foreach (var ban in snapshot.Bans)
        {
            AppendLedgerDetailModal(html, ban);
        }
    }

    private static void AppendLedgerStatusIcon(StringBuilder html, string status)
    {
        var (icon, label, css, description) = status switch
        {
            "Active" => ("ph-check-circle", "Active", "text-success", "Ban is currently active in the public ledger."),
            "Lifted" => ("ph-arrow-u-up-left", "Lifted", "text-info", "Ban has been lifted by the origin network."),
            "Expired" => ("ph-clock-counter-clockwise", "Expired", "text-warning", "Temporary ban has elapsed."),
            _ => ("ph-question", string.IsNullOrWhiteSpace(status) ? "Unknown" : status, "text-muted", "Ledger status is unknown.")
        };
        html.Append("<span title=\"");
        html.Append(Encode(description));
        html.Append("\" aria-label=\"");
        html.Append(Encode(label));
        html.Append("\" class=\"dragnet-status-badge ");
        html.Append(css);
        html.Append("\"><i class=\"ph ");
        html.Append(icon);
        html.Append("\"></i><span>");
        html.Append(Encode(label));
        html.Append("</span></span>");
    }

    private static void AppendLedgerDetailModal(StringBuilder html, DragnetLedgerBan ban)
    {
        AppendModalStart(html, $"ledger-detail-{ban.EventId}", "Ledger ban details", "ph-identification-card");
        html.AppendLine("<div class=\"space-y-4 max-w-5xl\">");
        html.Append("<div class=\"flex flex-col gap-3 md:flex-row md:items-start md:justify-between\"><div><div class=\"text-xs uppercase tracking-wide text-muted\">Banned player</div><div class=\"text-2xl font-semibold text-foreground\">");
        html.Append(Encode(ban.PlayerName));
        html.Append("</div><div class=\"mt-1 text-sm text-muted\">");
        html.Append(Encode(ban.PlayerNetworkId));
        html.Append("</div></div><div>");
        AppendLedgerStatusIcon(html, ban.Status);
        html.Append("</div></div>");
        html.Append("<div class=\"rounded-md border border-line bg-surface-alt/20 p-4\"><div class=\"flex items-center gap-2 text-sm text-muted\"><i class=\"ph ph-warning-circle\"></i>Ban reason</div><div class=\"mt-2 text-base text-foreground\">");
        html.Append(Encode(ban.Reason));
        html.Append("</div></div>");
        html.AppendLine("<div class=\"grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-3\">");
        AppendLedgerDetailField(html, "ph-game-controller", "Platform", string.IsNullOrWhiteSpace(ban.PlayerGame) ? "Unknown game platform" : ban.PlayerGame, "The game platform this ledger entry was issued against.");
        AppendLedgerDetailField(html, "ph-globe", "Origin network", ban.OriginName, "The Dragnet network that created the ban.");
        AppendLedgerDetailField(html, "ph-server", "Origin server", ban.OriginServerName, "The server that first reported this penalty.");
        AppendLedgerDetailField(html, "ph-user-gear", "Issued by", ban.AdminName ?? "Unknown administrator", "The administrator name supplied by the origin network.");
        AppendLedgerDetailField(html, "ph-shield-check", "Reconciliation", ban.ReconciliationStatus, "How this node matched the ban against local records.");
        AppendLedgerDetailField(html, "ph-users-three", "Peer acceptance", $"{ban.AcceptedNetworkCount} of {ban.EligibleNetworkCount} networks", "How many eligible peers have reported compatible coverage.");
        AppendLedgerDetailField(html, "ph-hard-drives", "Enforced servers", $"{ban.EnforcedServerCount} servers", "Number of servers currently reporting enforcement.");
        AppendLedgerDetailField(html, "ph-calendar-plus", "Created", FormatLedgerDetailTime(ban.CreatedAtUtc), "When the origin network issued this ledger event.");
        AppendLedgerDetailField(html, "ph-calendar-x", "Expires", ban.ExpiresAtUtc is null ? "Permanent ban" : FormatLedgerDetailTime(ban.ExpiresAtUtc.Value), "When the ban expires, if it is temporary.");
        if (!string.IsNullOrWhiteSpace(ban.EvidenceUrl))
        {
            html.Append("<div class=\"dragnet-detail-card xl:col-span-3\"><div class=\"dragnet-detail-label\"><i class=\"ph ph-link\"></i>Evidence</div><a class=\"dragnet-detail-value text-primary hover:underline break-all\" target=\"_blank\" rel=\"noopener noreferrer\" href=\"");
            html.Append(Encode(ban.EvidenceUrl));
            html.Append("\">");
            html.Append(Encode(ban.EvidenceUrl));
            html.AppendLine("</a></div>");
        }
        html.AppendLine("</div>");
        AppendModalEnd(html);
    }

    private static string FormatLedgerDetailTime(DateTimeOffset value) =>
        value.ToString("MMMM d, 'at' HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string FormatLedgerIssuedTime(DateTimeOffset value) =>
        value.ToString("ddd, MMMM ", CultureInfo.InvariantCulture) +
        value.Day +
        OrdinalSuffix(value.Day) +
        value.ToString(" 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string OrdinalSuffix(int day)
    {
        if (day % 100 is >= 11 and <= 13)
        {
            return "th";
        }

        return (day % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        };
    }

    private static void AppendLedgerDetailField(
        StringBuilder html,
        string icon,
        string label,
        string value,
        string description)
    {
        html.Append("<div class=\"dragnet-detail-card\"><div class=\"dragnet-detail-label\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append("\"></i>");
        html.Append(Encode(label));
        html.Append("</div><div class=\"dragnet-detail-value\">");
        html.Append(Encode(value));
        html.Append("</div><div class=\"mt-2 text-xs text-muted\">");
        html.Append(Encode(description));
        html.AppendLine("</div></div>");
    }

    private static void AppendRequestedModuleScript(
        StringBuilder html,
        IDictionary<string, string>? meta)
    {
        if (meta is null ||
            !meta.TryGetValue("module", out var module))
        {
            return;
        }

        var id = module.ToLowerInvariant() switch
        {
            "events" => "dragnet-events-modal",
            "peers" => "dragnet-peer-modal",
            "notifications" => "dragnet-notification-modal",
            "guide" => "dragnet-guide-modal",
            "directory" => "dragnet-directory-modal",
            "ledger" => "dragnet-ledger-modal",
            "updates" => "dragnet-updates-modal",
            "diagnostics" => "dragnet-diagnostics-modal",
            _ => null
        };
        if (id is null)
        {
            return;
        }

        html.Append("<script>setTimeout(function(){dragnetOpenModal('");
        html.Append(Encode(id));
        html.AppendLine("')},0);</script>");
    }

    private void AppendDashboardNavigation(
        StringBuilder html,
        int notificationCount,
        int directoryCount,
        int peerCount,
        int eventCount,
        int updateAttentionCount,
        int diagnosticsAttentionCount)
    {
        html.AppendLine("<nav class=\"dragnet-top-nav rounded-lg bg-surface/90 p-3\" aria-label=\"Dragnet navigation\">");
        html.AppendLine("<div class=\"flex flex-col gap-2 lg:flex-row lg:items-center lg:justify-between\">");
        html.AppendLine("<div class=\"flex flex-wrap items-center gap-2\">");
        AppendNavLink(html, "Back", "/", "ph-arrow-left");
        AppendScrollButton(html, "Dashboard", "dragnet-status", "ph-gauge");
        AppendScrollButton(html, "Readiness", "deployment-readiness", "ph-check-circle");
        html.AppendLine("</div><div class=\"flex flex-wrap items-center gap-2 lg:justify-end\">");
        AppendModalButton(html, "Public ledger", "dragnet-ledger-modal", "ph-list-magnifying-glass");
        AppendModalButton(html, "Notifications", "dragnet-notification-modal", "ph-bell", notificationCount);
        AppendModalButton(
            html,
            "Updates",
            "dragnet-updates-modal",
            "ph-cloud-arrow-down",
            updateAttentionCount > 0 ? updateAttentionCount : null);
        AppendModalButton(
            html,
            "Diagnostics",
            "dragnet-diagnostics-modal",
            "ph-activity",
            diagnosticsAttentionCount > 0 ? diagnosticsAttentionCount : null);
        AppendModalButton(html, "Guide", "dragnet-guide-modal", "ph-clipboard-text");
        AppendModalButton(html, "Directory", "dragnet-directory-modal", "ph-address-book", directoryCount);
        AppendModalButton(html, "Peers", "dragnet-peer-modal", "ph-plugs", peerCount);
        AppendModalButton(html, "Events", "dragnet-events-modal", "ph-list-checks", eventCount);

        html.AppendLine("</div></div></nav>");
    }

    private static void AppendModalButton(
        StringBuilder html,
        string label,
        string targetId,
        string icon,
        int? count = null)
    {
        html.Append("<button type=\"button\" class=\"dragnet-icon-button inline-flex items-center rounded-md border border-line text-sm hover:bg-surface-hover\" title=\"");
        html.Append(Encode(label));
        html.Append("\" aria-label=\"");
        html.Append(Encode(label));
        html.Append("\" data-tip=\"");
        html.Append(Encode(label));
        html.Append("\" onclick=\"dragnetOpenModal('");
        html.Append(Encode(targetId));
        html.Append("')\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append("\"></i>");
        if (count is not null)
        {
            html.Append("<span class=\"rounded-full bg-surface-alt px-1.5 text-xs text-muted\">");
            html.Append(count.Value);
            html.Append("</span>");
        }
        html.AppendLine("</button>");
    }

    private static void AppendNavLink(
        StringBuilder html,
        string label,
        string href,
        string icon,
        bool external = false)
    {
        html.Append("<a class=\"inline-flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm hover:bg-surface-hover\" href=\"");
        html.Append(Encode(href));
        html.Append("\"");
        if (external)
        {
            html.Append(" target=\"_blank\" rel=\"noopener noreferrer\"");
        }
        html.Append("><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append("\"></i><span>");
        html.Append(Encode(label));
        html.AppendLine("</span></a>");
    }

    private static void AppendScrollButton(
        StringBuilder html,
        string label,
        string targetId,
        string icon)
    {
        html.Append("<button type=\"button\" class=\"inline-flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm hover:bg-surface-hover\" onclick=\"document.getElementById('");
        html.Append(Encode(targetId));
        html.Append("')?.scrollIntoView({block:'start',behavior:'smooth'})\"><i class=\"ph ");
        html.Append(Encode(icon));
        html.Append("\"></i><span>");
        html.Append(Encode(label));
        html.AppendLine("</span></button>");
    }

    private void AppendOnboardingPanel(
        StringBuilder html,
        DragnetOnboardingStatus status)
    {
        html.AppendLine("<div id=\"deployment-readiness\" class=\"rounded-lg border border-line bg-surface/50 p-2\">");
        html.AppendLine("<div class=\"px-3 py-2 flex flex-col md:flex-row md:items-center md:justify-between gap-3\">");
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
        html.AppendLine("<div class=\"grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-2\">");
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
        html.AppendLine("<div id=\"deployment-guide\">");
        html.AppendLine("<div class=\"px-3 py-2 flex flex-col md:flex-row md:items-center md:justify-between gap-2\">");
        html.AppendLine("<div><h3 class=\"font-semibold\">Deployment guide</h3>");
        html.AppendLine("<div class=\"text-sm text-muted\">Endpoint-specific routes and reverse-proxy requirements.</div></div>");
        html.AppendLine("<a class=\"text-sm text-primary hover:underline\" target=\"_blank\" rel=\"noopener noreferrer\" href=\"/dragnet/setup-guide\">Open shareable guide</a>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"grid grid-cols-1 lg:grid-cols-2 gap-2\">");
        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/30 p-4 space-y-2 text-sm\">");
        AppendGuideValue(html, "Health", endpoint is null ? "Not configured" : $"{endpoint}/health");
        AppendGuideValue(html, "Heartbeat", endpoint is null ? "Not configured" : $"{endpoint}/heartbeat");
        AppendGuideValue(html, "Directory", endpoint is null ? "Not configured" : $"{endpoint}/directory");
        AppendGuideValue(html, "Bootstrap", DragnetConfiguration.OfficialBootstrapEndpoint);
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/30 p-4 text-sm space-y-2\">");
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
        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/30 px-4 py-2\">");
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
            },
            new()
            {
                ["Name"] = "NotificationsEnabled",
                ["Label"] = "Enable notification inbox (yes/no)",
                ["Value"] = _configuration.NotificationsEnabled ? "yes" : "no",
                ["Placeholder"] = "yes"
            },
            new()
            {
                ["Name"] = "AutoUpdateEnabled",
                ["Label"] = "Automatically install official Dragnet updates (yes/no)",
                ["Value"] = _configuration.AutoUpdateEnabled ? "yes" : "no",
                ["Placeholder"] = "yes"
            },
            new()
            {
                ["Name"] = "StalePendingReviewHours",
                ["Label"] = "Stale review threshold (hours)",
                ["Value"] = Math.Max(1, (int)_configuration.StalePendingReviewAfter.TotalHours).ToString(),
                ["Placeholder"] = "24"
            },
            new()
            {
                ["Name"] = "PeerQuarantineMinutes",
                ["Label"] = "Quarantine continuously failing peers after (minutes)",
                ["Value"] = Math.Max(5, (int)_configuration.PeerQuarantineAfter.TotalMinutes).ToString(),
                ["Placeholder"] = "30"
            },
            new()
            {
                ["Name"] = "QuarantinedPeerProbeMinutes",
                ["Label"] = "Probe quarantined peers every (minutes)",
                ["Value"] = Math.Max(1, (int)_configuration.QuarantinedPeerProbeInterval.TotalMinutes).ToString(),
                ["Placeholder"] = "10"
            },
            new()
            {
                ["Name"] = "InGameNotificationSummariesEnabled",
                ["Label"] = "Enable in-game admin summaries (yes/no)",
                ["Value"] = _configuration.InGameNotificationSummariesEnabled ? "yes" : "no",
                ["Placeholder"] = "yes"
            },
            new()
            {
                ["Name"] = "InGameNotificationSummaryMinutes",
                ["Label"] = "In-game summary interval (minutes)",
                ["Value"] = Math.Max(1, (int)_configuration.InGameNotificationSummaryInterval.TotalMinutes).ToString(),
                ["Placeholder"] = "15"
            },
            new()
            {
                ["Name"] = "NotificationWebhookUrl",
                ["Label"] = "Notification webhook (optional HTTPS)",
                ["Value"] = _configuration.NotificationWebhookUrl ?? "",
                ["Placeholder"] = "https://discord.com/api/webhooks/..."
            }
        });
    }

    private static void AppendDirectoryPanel(
        StringBuilder html,
        IReadOnlyList<DragnetDirectoryEntry> entries,
        DateTimeOffset now)
    {
        html.AppendLine("<div id=\"community-directory\">");
        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/20 overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted\"><tr><th class=\"px-4 py-2\">Network</th><th class=\"px-4 py-2 text-center\">Verification</th><th class=\"px-4 py-2\">Region</th><th class=\"px-4 py-2 text-center\">Servers</th><th class=\"px-4 py-2\">Version</th><th class=\"px-4 py-2\">Seen</th></tr></thead><tbody>");
        if (entries.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"6\" class=\"px-4 py-5 text-center text-muted\">No live networks have opted into directory publication.</td></tr>");
        }
        else
        {
            foreach (var entry in entries)
            {
                html.AppendLine("<tr class=\"hover:bg-surface-alt/30\">");
                html.Append("<td class=\"px-4 py-2 font-medium\">");
                html.Append("<button type=\"button\" class=\"text-primary hover:underline\" onclick=\"dragnetOpenModal('network-profile-");
                html.Append(Encode(entry.OriginId));
                html.Append("')\">");
                html.Append(Encode(entry.OriginName));
                html.Append("</button>");
                html.Append("</td><td class=\"px-4 py-2 text-center\">");
                html.Append(entry.Verified
                    ? "<span class=\"text-success\" title=\"Verified\"><i class=\"ph ph-check-circle\"></i></span>"
                    : "<span class=\"text-warning\" title=\"Unverified\"><i class=\"ph ph-x-circle\"></i></span>");
                html.Append("</td><td class=\"px-4 py-2 text-muted\"><span class=\"inline-flex items-center gap-2\"><span>");
                html.Append(Encode(RegionFlag(entry.Region)));
                html.Append("</span><span>");
                html.Append(Encode(entry.Region ?? "Not specified"));
                html.Append("</span></span></td><td class=\"px-4 py-2 text-center\">");
                html.Append(Encode(entry.ServerCount.ToString()));
                html.Append("</td><td class=\"px-4 py-2 text-muted\">");
                html.Append(Encode(entry.Version ?? "Unknown"));
                html.Append("</td><td class=\"px-4 py-2 text-muted\">");
                html.Append(Encode(DescribeAge(now - entry.LastSeenUtc)));
                html.AppendLine("</td></tr>");
            }
        }

        html.AppendLine("</tbody></table></div></div>");
    }

    private static void AppendNetworkProfileModals(StringBuilder html, IReadOnlyList<DragnetNetworkProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            AppendNetworkProfileModal(html, profile);
        }
    }

    private static string RegionFlag(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return "🌐";
        }

        var trimmed = region.Trim();
        if (trimmed.Length >= 2 &&
            char.IsLetter(trimmed[0]) &&
            char.IsLetter(trimmed[1]) &&
            (trimmed.Length == 2 || trimmed[2] is '-' or '_' or ' '))
        {
            var first = char.ToUpperInvariant(trimmed[0]) - 'A' + 0x1F1E6;
            var second = char.ToUpperInvariant(trimmed[1]) - 'A' + 0x1F1E6;
            if (first is >= 0x1F1E6 and <= 0x1F1FF &&
                second is >= 0x1F1E6 and <= 0x1F1FF)
            {
                return char.ConvertFromUtf32(first) + char.ConvertFromUtf32(second);
            }
        }

        return "🏳️";
    }

    private static void AppendNetworkProfileModal(StringBuilder html, DragnetNetworkProfile profile)
    {
        AppendModalStart(html, $"network-profile-{profile.OriginId}", "Network profile", "ph-globe-hemisphere-west");
        html.AppendLine("<div class=\"space-y-4 max-w-5xl\">");
        html.Append("<div class=\"flex flex-col gap-3 md:flex-row md:items-start md:justify-between\"><div><div class=\"text-xs uppercase tracking-wide text-muted\">Dragnet origin</div><div class=\"text-2xl font-semibold text-foreground\">");
        html.Append(Encode(profile.OriginName));
        html.Append("</div><div class=\"mt-1 text-sm text-muted break-all\">");
        html.Append(Encode(profile.OriginId));
        html.Append("</div></div>");
        AppendNetworkHealthBadge(html, profile.Health);
        html.Append("</div>");

        html.AppendLine("<div class=\"grid grid-cols-2 md:grid-cols-4 gap-3\">");
        AppendProfileMetric(html, "ph-gavel", profile.SubmittedBanCount.ToString(), "Submitted bans");
        AppendProfileMetric(html, "ph-shield-warning", profile.ActiveBanCount.ToString(), "Active bans");
        AppendProfileMetric(html, "ph-hard-drives", profile.ServerCount.ToString(), "Reported servers");
        AppendProfileMetric(html, "ph-link", $"{profile.EvidenceRatePercent}%", "Evidence coverage");
        html.AppendLine("</div>");

        html.AppendLine("<div class=\"grid grid-cols-1 lg:grid-cols-2 gap-3\">");
        html.AppendLine("<section class=\"dragnet-detail-card\"><h4 class=\"font-semibold mb-3\"><i class=\"ph ph-fingerprint mr-2\"></i>Identity and transport</h4><div class=\"grid grid-cols-1 md:grid-cols-2 gap-3\">");
        AppendProfileField(html, "Endpoint", string.IsNullOrWhiteSpace(profile.Endpoint) ? "Not advertised" : profile.Endpoint, "ph-plugs");
        AppendProfileField(html, "Version", profile.Version ?? "Unknown", "ph-package");
        AppendProfileField(html, "Region", profile.Region ?? "Not specified", "ph-map-pin");
        AppendProfileField(html, "Last heartbeat", DescribeNullableTime(profile.LastSeenUtc), "ph-pulse");
        AppendProfileField(html, "Identity proof", profile.IdentityVerified ? "Verified" : "Unverified", "ph-seal-check");
        AppendProfileField(html, "Endpoint proof", profile.EndpointVerified ? "Verified" : "Pending or stale", "ph-shield-check");
        html.AppendLine("</div></section>");

        html.AppendLine("<section class=\"dragnet-detail-card\"><h4 class=\"font-semibold mb-3\"><i class=\"ph ph-scales mr-2\"></i>Local trust review</h4><div class=\"grid grid-cols-1 md:grid-cols-2 gap-3\">");
        AppendProfileField(html, "Trusted here", profile.TrustedByThisNetwork ? "Yes" : "No", "ph-handshake");
        AppendProfileField(html, "Auto approve bans", profile.AutoApproveBans ? "Enabled" : "Disabled", "ph-check-circle");
        AppendProfileField(html, "Auto approve lifts", profile.AutoApproveLifts ? "Enabled" : "Disabled", "ph-arrow-u-up-left");
        AppendProfileField(html, "Pending reviews", profile.PendingBanCount.ToString(), "ph-hourglass");
        AppendProfileField(html, "Approved bans", $"{profile.ApprovedBanCount} ({profile.ApprovalRatePercent}%)", "ph-thumbs-up");
        AppendProfileField(html, "Denied bans", $"{profile.DeniedBanCount} ({profile.DenialRatePercent}%)", "ph-thumbs-down");
        html.AppendLine("</div></section>");

        html.AppendLine("<section class=\"dragnet-detail-card\"><h4 class=\"font-semibold mb-3\"><i class=\"ph ph-share-network mr-2\"></i>Propagation</h4><div class=\"grid grid-cols-1 md:grid-cols-2 gap-3\">");
        AppendProfileField(html, "Enforced coverage", $"{profile.EnforcementCoveragePercent}% ({profile.EnforcedCoverageSlots} slots)", "ph-broadcast");
        AppendProfileField(html, "Reported coverage", $"{profile.ReportedCoverageSlots} / {profile.EligibleCoverageSlots}", "ph-chart-line-up");
        AppendProfileField(html, "Acknowledged deliveries", $"{profile.AcknowledgedDeliveryCount} ({profile.DeliveryAcknowledgementPercent}%)", "ph-checks");
        AppendProfileField(html, "Tracked deliveries", profile.TrackedDeliveryCount.ToString(), "ph-truck");
        html.AppendLine("</div></section>");

        html.AppendLine("<section class=\"dragnet-detail-card\"><h4 class=\"font-semibold mb-3\"><i class=\"ph ph-cpu mr-2\"></i>Protocol support</h4><div class=\"flex flex-wrap gap-2\">");
        AppendCapabilityBadge(html, "Delivery acknowledgements", profile.SupportsDeliveryAcknowledgements);
        AppendCapabilityBadge(html, "Evidence updates", profile.SupportsEvidenceUpdates);
        AppendCapabilityBadge(html, "Ban attestations", profile.SupportsBanAttestations);
        AppendCapabilityBadge(html, "Attestation refresh", profile.SupportsAttestationRefreshRequests);
        html.AppendLine("</div></section></div>");

        html.AppendLine("<section class=\"rounded-md border border-line bg-surface-alt/20 overflow-x-auto\"><table class=\"w-full text-left text-sm\"><thead class=\"text-muted\"><tr><th class=\"px-4 py-2\">Recent player</th><th class=\"px-4 py-2\">Reason</th><th class=\"px-4 py-2\">Evidence</th><th class=\"px-4 py-2\">Local review</th><th class=\"px-4 py-2\">Issued</th></tr></thead><tbody>");
        if (profile.RecentBans.Count == 0)
        {
            html.AppendLine("<tr><td colspan=\"5\" class=\"px-4 py-5 text-center text-muted\">No bans from this network are stored on this node.</td></tr>");
        }
        else
        {
            foreach (var ban in profile.RecentBans)
            {
                html.Append("<tr class=\"hover:bg-surface-alt/30\"><td class=\"px-4 py-2 font-medium\">");
                html.Append(Encode(ban.PlayerName));
                html.Append("</td><td class=\"px-4 py-2\">");
                html.Append(Encode(ban.Reason));
                html.Append("</td><td class=\"px-4 py-2\">");
                html.Append(ban.HasEvidence ? "<span class=\"text-success\">Available</span>" : "<span class=\"text-muted\">None</span>");
                html.Append("</td><td class=\"px-4 py-2\">");
                html.Append(Encode(ban.ReviewState));
                html.Append("</td><td class=\"px-4 py-2 text-muted\">");
                html.Append(Encode($"{ban.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC"));
                html.AppendLine("</td></tr>");
            }
        }

        html.AppendLine("</tbody></table></section></div>");
        AppendModalEnd(html);
    }

    private static void AppendNetworkHealthBadge(StringBuilder html, string health)
    {
        var (icon, css) = health switch
        {
            "Healthy" => ("ph-check-circle", "text-success"),
            "Degraded" or "Stale" or "Quarantined" => ("ph-warning-circle", "text-warning"),
            "Errored" => ("ph-x-circle", "text-danger"),
            _ => ("ph-clock-counter-clockwise", "text-muted")
        };
        html.Append("<span class=\"dragnet-status-badge ");
        html.Append(css);
        html.Append("\" title=\"Current network transport health\"><i class=\"ph ");
        html.Append(icon);
        html.Append("\"></i><span>");
        html.Append(Encode(health));
        html.Append("</span></span>");
    }

    private static void AppendProfileMetric(StringBuilder html, string icon, string value, string label)
    {
        html.Append("<div class=\"dragnet-detail-card\"><div class=\"dragnet-detail-label\"><i class=\"ph ");
        html.Append(icon);
        html.Append("\"></i>");
        html.Append(Encode(label));
        html.Append("</div><div class=\"mt-2 text-2xl font-semibold text-foreground\">");
        html.Append(Encode(value));
        html.AppendLine("</div></div>");
    }

    private static void AppendProfileField(StringBuilder html, string label, string value, string icon)
    {
        html.Append("<div><div class=\"dragnet-detail-label\"><i class=\"ph ");
        html.Append(icon);
        html.Append("\"></i>");
        html.Append(Encode(label));
        html.Append("</div><div class=\"dragnet-detail-value text-sm\">");
        html.Append(Encode(value));
        html.AppendLine("</div></div>");
    }

    private static void AppendCapabilityBadge(StringBuilder html, string label, bool supported)
    {
        html.Append("<span class=\"dragnet-status-badge ");
        html.Append(supported ? "text-success" : "text-muted");
        html.Append("\"><i class=\"ph ");
        html.Append(supported ? "ph-check" : "ph-minus");
        html.Append("\"></i><span>");
        html.Append(Encode(label));
        html.Append("</span></span>");
    }

    private static string DescribeNullableTime(DateTimeOffset? value) =>
        value is null ? "Never" : $"{value:yyyy-MM-dd HH:mm:ss} UTC";

    private static bool IsEnabledValue(string? value) =>
        value is not null &&
        (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static void AppendDiagnosticsPanel(
        StringBuilder html,
        DragnetDiagnosticsReport report,
        DateTimeOffset now)
    {
        var healthClass = report.NetworkHealthScore >= 90
            ? "text-success"
            : report.NetworkHealthScore >= 70
                ? "text-warning"
                : "text-danger";
        var recentEvents = report.Peers
            .SelectMany(peer => peer.RecentTelemetry.Select(item => (Peer: peer, Event: item)))
            .OrderByDescending(item => item.Event.OccurredAtUtc)
            .Take(20)
            .ToList();

        html.AppendLine("<div class=\"space-y-4\">");
        html.AppendLine("<div class=\"flex flex-col gap-3 md:flex-row md:items-center md:justify-between\">");
        html.Append("<div><div class=\"text-xs text-muted\">Network health</div><div class=\"mt-1 text-2xl font-semibold ");
        html.Append(healthClass);
        html.Append("\">");
        html.Append(report.NetworkHealthScore);
        html.Append("/100 · ");
        html.Append(Encode(report.NetworkHealthState));
        html.AppendLine("</div></div>");
        html.AppendLine("<a class=\"inline-flex items-center gap-2 rounded-md border border-line px-3 py-2 text-sm hover:bg-surface-hover\" href=\"/api/dragnet/diagnostics\" download><i class=\"ph ph-download-simple\"></i><span>Download diagnostics</span></a>");
        html.AppendLine("</div>");

        html.AppendLine("<div class=\"grid grid-cols-2 md:grid-cols-4 gap-2\">");
        AppendUpdateSummaryCard(html, "Active peers", report.ActivePeerCount.ToString(), "ph-plugs", "text-info");
        AppendUpdateSummaryCard(html, "Known peers", report.TotalPeerCount.ToString(), "ph-address-book", "text-foreground");
        AppendUpdateSummaryCard(html, "Pending delivery", report.PendingDeliveryCount.ToString(), "ph-hourglass", report.PendingDeliveryCount > 0 ? "text-warning" : "text-success");
        AppendUpdateSummaryCard(html, "Deliverable events", report.DeliverableEventCount.ToString(), "ph-paper-plane-tilt", "text-foreground");
        html.AppendLine("</div>");

        html.AppendLine("<section><div class=\"flex items-center justify-between gap-2 mb-2\"><h4 class=\"font-semibold\">Peer health</h4><span class=\"text-xs text-muted\">Latency and delivery pressure</span></div><div class=\"space-y-2\">");
        if (report.Peers.Count == 0)
        {
            html.AppendLine("<div class=\"rounded-md bg-surface-alt/20 p-3 text-sm text-muted\">No peer telemetry is available yet.</div>");
        }
        else
        {
            foreach (var peer in report.Peers)
            {
                var peerClass = peer.HealthScore >= 90
                    ? "text-success"
                    : peer.HealthScore >= 70
                        ? "text-warning"
                        : "text-danger";
                html.Append("<div class=\"rounded-md bg-surface-alt/20 p-3\"><div class=\"dragnet-diagnostics-peer-grid\"><div class=\"min-w-0\"><div class=\"font-medium break-words\">");
                html.Append(Encode(peer.OriginName));
                html.Append("</div><div class=\"mt-1 text-xs text-muted break-all\">");
                html.Append(Encode(peer.Endpoint));
                html.Append("</div>");
                if (peer.HealthCauses.Count > 0)
                {
                    html.Append("<div class=\"mt-1 text-xs text-warning\">");
                    html.Append(Encode(string.Join(" · ", peer.HealthCauses)));
                    html.Append("</div>");
                }
                html.Append("</div><div><div class=\"text-xs text-muted\">Health</div><div class=\"mt-1 font-semibold ");
                html.Append(peerClass);
                html.Append("\">");
                html.Append(peer.HealthScore);
                html.Append("/100</div></div><div><div class=\"text-xs text-muted\">Latency</div><div class=\"mt-1 font-medium\">");
                html.Append(peer.AverageHeartbeatLatencyMs is null
                    ? "Unknown"
                    : Encode($"{peer.AverageHeartbeatLatencyMs:0} ms"));
                html.Append("</div></div><div><div class=\"text-xs text-muted\">Success</div><div class=\"mt-1 font-medium\">");
                html.Append(peer.HeartbeatSuccessRate is null
                    ? "Unknown"
                    : Encode($"{peer.HeartbeatSuccessRate:0.#}%"));
                html.Append("</div></div><div><div class=\"text-xs text-muted\">Backlog</div><div class=\"mt-1 font-medium\">");
                html.Append(peer.PendingDeliveryCount);
                html.Append("</div>");
                if (peer.OldestPendingDeliveryAtUtc is { } oldest)
                {
                    html.Append("<div class=\"text-xs text-muted\">oldest ");
                    html.Append(Encode(DescribeAge(now - oldest)));
                    html.Append("</div>");
                }
                html.AppendLine("</div></div></div>");
            }
        }
        html.AppendLine("</div></section>");

        html.AppendLine("<section><div class=\"flex items-center justify-between gap-2 mb-2\"><h4 class=\"font-semibold\">Connection timeline</h4><span class=\"text-xs text-muted\">Recent peer transitions</span></div><div class=\"space-y-2\">");
        if (recentEvents.Count == 0)
        {
            html.AppendLine("<div class=\"rounded-md bg-surface-alt/20 p-3 text-sm text-muted\">No connection transitions have been recorded yet.</div>");
        }
        else
        {
            foreach (var item in recentEvents)
            {
                var (icon, stateClass) = PeerTelemetryPresentation(item.Event.Type);
                html.Append("<div class=\"rounded-md bg-surface-alt/20 p-3 flex items-start gap-3\"><i class=\"ph ");
                html.Append(icon);
                html.Append(" mt-1 ");
                html.Append(stateClass);
                html.Append("\"></i><div class=\"min-w-0\"><div class=\"font-medium\">");
                html.Append(Encode(item.Peer.OriginName));
                html.Append(" · ");
                html.Append(Encode(PeerTelemetryLabel(item.Event.Type)));
                html.Append("</div><div class=\"mt-1 text-xs text-muted\">");
                html.Append(Encode(DescribeAge(now - item.Event.OccurredAtUtc)));
                if (item.Event.LatencyMs is { } latency)
                {
                    html.Append(" · ");
                    html.Append(Encode($"{latency:0} ms"));
                }
                html.Append("</div>");
                if (!string.IsNullOrWhiteSpace(item.Event.Detail))
                {
                    html.Append("<div class=\"mt-1 text-sm text-muted break-words\">");
                    html.Append(Encode(Shorten(item.Event.Detail, 240)));
                    html.Append("</div>");
                }
                html.AppendLine("</div></div>");
            }
        }
        html.AppendLine("</div></section>");
        html.AppendLine("<div class=\"text-xs text-muted\">The downloaded report excludes webhook URLs, private keys, trust details, player identities, and ban contents.</div>");
        html.AppendLine("</div>");
    }

    private static (string Icon, string StateClass) PeerTelemetryPresentation(
        DragnetPeerTelemetryEventType type) =>
        type switch
        {
            DragnetPeerTelemetryEventType.Connected => ("ph-link", "text-success"),
            DragnetPeerTelemetryEventType.Recovered => ("ph-arrow-counter-clockwise", "text-success"),
            DragnetPeerTelemetryEventType.Failed => ("ph-warning-circle", "text-warning"),
            DragnetPeerTelemetryEventType.Quarantined => ("ph-lock-key", "text-danger"),
            _ => ("ph-info", "text-muted")
        };

    private static string PeerTelemetryLabel(DragnetPeerTelemetryEventType type) =>
        type switch
        {
            DragnetPeerTelemetryEventType.Connected => "Connected",
            DragnetPeerTelemetryEventType.Recovered => "Recovered",
            DragnetPeerTelemetryEventType.Failed => "Heartbeat failed",
            DragnetPeerTelemetryEventType.Quarantined => "Quarantined",
            _ => type.ToString()
        };

    private static void AppendUpdateOperationsPanel(
        StringBuilder html,
        DragnetUpdateStatus update,
        IReadOnlyList<DragnetUpdateHistoryEntry> history,
        IReadOnlyList<DragnetPeerRecord> activePeers,
        DateTimeOffset now)
    {
        var targetVersion = update.LatestVersion ?? update.CurrentVersion;
        var knownPeerVersions = activePeers
            .Where(peer => !string.IsNullOrWhiteSpace(peer.Version))
            .GroupBy(peer => peer.Version!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unknownPeers = activePeers
            .Where(peer => string.IsNullOrWhiteSpace(peer.Version))
            .ToList();
        var outdatedPeers = activePeers
            .Where(peer =>
                !string.IsNullOrWhiteSpace(peer.Version) &&
                DragnetUpdateService.CompareVersions(peer.Version, targetVersion) < 0)
            .ToList();
        var aheadPeers = activePeers
            .Where(peer =>
                !string.IsNullOrWhiteSpace(peer.Version) &&
                DragnetUpdateService.CompareVersions(peer.Version, targetVersion) > 0)
            .ToList();
        var networkVersions = knownPeerVersions
            .Select(group => group.Key)
            .Append(update.CurrentVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fragmented = networkVersions.Count > 1 || unknownPeers.Count > 0;

        html.AppendLine("<div class=\"space-y-4\">");
        html.AppendLine("<div class=\"grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-2\">");
        AppendUpdateSummaryCard(
            html,
            "Running",
            update.CurrentVersion,
            "ph-play-circle",
            "text-success");
        AppendUpdateSummaryCard(
            html,
            update.RestartRequired ? "Staged" : "Target",
            update.RestartRequired
                ? update.InstalledVersion ?? targetVersion
                : targetVersion,
            update.RestartRequired ? "ph-arrow-clockwise" : "ph-crosshair",
            update.RestartRequired ? "text-warning" : "text-info");
        AppendUpdateSummaryCard(
            html,
            "Outdated peers",
            outdatedPeers.Count.ToString(),
            "ph-warning-circle",
            outdatedPeers.Count > 0 ? "text-warning" : "text-success");
        AppendUpdateSummaryCard(
            html,
            "Version health",
            fragmented ? "Fragmented" : "Aligned",
            fragmented ? "ph-git-fork" : "ph-check-circle",
            fragmented ? "text-warning" : "text-success");
        html.AppendLine("</div>");

        if (update.RestartRequired)
        {
            html.Append("<div class=\"rounded-md bg-surface-alt/30 p-3 text-sm text-warning\"><i class=\"ph ph-arrow-clockwise mr-2\"></i>Dragnet ");
            html.Append(Encode(update.InstalledVersion ?? targetVersion));
            html.AppendLine(" is staged. Restart IW4MAdmin to load the new DLL.</div>");
        }
        else if (!string.IsNullOrWhiteSpace(update.InstallError))
        {
            html.Append("<div class=\"rounded-md bg-surface-alt/30 p-3 text-sm text-danger\"><i class=\"ph ph-x-circle mr-2\"></i>Installation failed: ");
            html.Append(Encode(update.InstallError));
            html.AppendLine("</div>");
        }
        else if (!string.IsNullOrWhiteSpace(update.CheckError))
        {
            html.Append("<div class=\"rounded-md bg-surface-alt/30 p-3 text-sm text-warning\"><i class=\"ph ph-warning-circle mr-2\"></i>Release check failed: ");
            html.Append(Encode(update.CheckError));
            html.AppendLine("</div>");
        }

        html.AppendLine("<section><div class=\"flex items-center justify-between gap-2 mb-2\"><h4 class=\"font-semibold\">Network versions</h4><span class=\"text-xs text-muted\">Active peers only</span></div>");
        html.AppendLine("<div class=\"space-y-2\">");
        AppendVersionRow(
            html,
            label: "This network",
            version: update.CurrentVersion,
            detail: update.RestartRequired ? "Restart required" : "Running",
            stateClass: update.RestartRequired ? "text-warning" : "text-success");
        foreach (var group in knownPeerVersions)
        {
            var comparison = DragnetUpdateService.CompareVersions(group.Key, targetVersion);
            var state = comparison < 0 ? "Outdated" : comparison > 0 ? "Ahead" : "Current";
            var stateClass = comparison < 0 ? "text-warning" : comparison > 0 ? "text-info" : "text-success";
            AppendVersionRow(
                html,
                string.Join(", ", group.Select(peer => peer.OriginName).OrderBy(name => name)),
                group.Key,
                $"{group.Count()} network{(group.Count() == 1 ? "" : "s")} · {state}",
                stateClass);
        }
        if (unknownPeers.Count > 0)
        {
            AppendVersionRow(
                html,
                string.Join(", ", unknownPeers.Select(peer => peer.OriginName).OrderBy(name => name)),
                "Unknown",
                $"{unknownPeers.Count} peer{(unknownPeers.Count == 1 ? "" : "s")} did not advertise a version",
                "text-muted");
        }
        if (activePeers.Count == 0)
        {
            html.AppendLine("<div class=\"rounded-md bg-surface-alt/20 p-3 text-sm text-muted\">No active peers are currently reporting rollout status.</div>");
        }
        html.AppendLine("</div></section>");

        html.AppendLine("<section><div class=\"flex items-center justify-between gap-2 mb-2\"><h4 class=\"font-semibold\">Rollout history</h4><span class=\"text-xs text-muted\">Newest first</span></div>");
        html.AppendLine("<div class=\"space-y-2\">");
        if (history.Count == 0)
        {
            html.AppendLine("<div class=\"rounded-md bg-surface-alt/20 p-3 text-sm text-muted\">No update lifecycle events have been recorded yet.</div>");
        }
        else
        {
            foreach (var entry in history.Take(20))
            {
                var (icon, stateClass) = UpdateStagePresentation(entry.Stage);
                html.Append("<div class=\"rounded-md bg-surface-alt/20 p-3\"><div class=\"flex items-start gap-3\"><i class=\"ph ");
                html.Append(icon);
                html.Append(" mt-1 ");
                html.Append(stateClass);
                html.Append("\"></i><div class=\"min-w-0\"><div class=\"flex flex-wrap items-center gap-2\"><span class=\"font-medium\">");
                html.Append(Encode(UpdateStageLabel(entry.Stage)));
                html.Append("</span><span class=\"text-xs text-muted\">");
                html.Append(Encode(entry.Version));
                html.Append(" · ");
                html.Append(Encode(DescribeAge(now - entry.OccurredAtUtc)));
                html.Append("</span></div><div class=\"mt-1 text-sm text-muted\">");
                html.Append(Encode(entry.Message));
                html.Append("</div>");
                if (!string.IsNullOrWhiteSpace(entry.Error))
                {
                    html.Append("<div class=\"mt-1 text-xs text-danger break-all\">");
                    html.Append(Encode(entry.Error));
                    html.Append("</div>");
                }
                html.AppendLine("</div></div></div>");
            }
        }
        html.AppendLine("</div></section></div>");
    }

    private static void AppendUpdateSummaryCard(
        StringBuilder html,
        string label,
        string value,
        string icon,
        string stateClass)
    {
        html.Append("<div class=\"rounded-md bg-surface-alt/30 p-3\"><div class=\"flex items-center gap-2 text-xs text-muted\"><i class=\"ph ");
        html.Append(icon);
        html.Append("\"></i>");
        html.Append(Encode(label));
        html.Append("</div><div class=\"mt-1 font-semibold ");
        html.Append(stateClass);
        html.Append("\">");
        html.Append(Encode(value));
        html.AppendLine("</div></div>");
    }

    private static void AppendVersionRow(
        StringBuilder html,
        string label,
        string version,
        string detail,
        string stateClass)
    {
        html.Append("<div class=\"rounded-md bg-surface-alt/20 p-3 flex flex-col gap-2 md:flex-row md:items-center md:justify-between\"><div class=\"min-w-0\"><div class=\"font-medium break-words\">");
        html.Append(Encode(label));
        html.Append("</div><div class=\"mt-1 text-xs text-muted\">");
        html.Append(Encode(detail));
        html.Append("</div></div><span class=\"dragnet-status-badge ");
        html.Append(stateClass);
        html.Append("\"><i class=\"ph ph-tag\"></i><span>");
        html.Append(Encode(version));
        html.AppendLine("</span></span></div>");
    }

    private static (string Icon, string StateClass) UpdateStagePresentation(DragnetUpdateStage stage) =>
        stage switch
        {
            DragnetUpdateStage.Available => ("ph-megaphone", "text-info"),
            DragnetUpdateStage.Downloading => ("ph-download-simple", "text-info"),
            DragnetUpdateStage.Staged => ("ph-package", "text-warning"),
            DragnetUpdateStage.Applied => ("ph-check-circle", "text-success"),
            DragnetUpdateStage.CheckFailed => ("ph-cloud-x", "text-warning"),
            DragnetUpdateStage.InstallFailed => ("ph-x-circle", "text-danger"),
            _ => ("ph-info", "text-muted")
        };

    private static string UpdateStageLabel(DragnetUpdateStage stage) =>
        stage switch
        {
            DragnetUpdateStage.Available => "Update detected",
            DragnetUpdateStage.Downloading => "Download started",
            DragnetUpdateStage.Staged => "Update staged",
            DragnetUpdateStage.Applied => "Update applied",
            DragnetUpdateStage.CheckFailed => "Check failed",
            DragnetUpdateStage.InstallFailed => "Install failed",
            _ => stage.ToString()
        };

    private static void AppendOperationalHeader(
        StringBuilder html,
        DragnetUpdateStatus update,
        DateTimeOffset now)
    {
        html.AppendLine("<div id=\"dragnet-status\" class=\"rounded-lg border border-line bg-surface/50 p-2\">");
        html.AppendLine("<div class=\"grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-2\">");
        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/30 px-4 py-2\">");
        html.Append("<div class=\"text-xs text-muted\">Deployed version</div><div class=\"mt-1 font-semibold\">Dragnet ");
        html.Append(Encode(update.CurrentVersion));
        html.AppendLine("</div></div>");

        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/30 px-4 py-2\">");
        html.Append("<div class=\"text-xs text-muted\">Release status</div><div class=\"mt-1 font-medium\">");
        if (update.RestartRequired)
        {
            html.Append("<span class=\"text-warning\">Installed ");
            html.Append(Encode(update.InstalledVersion ?? "update"));
            html.Append("; restart required</span>");
        }
        else if (update.UpdateAvailable)
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

        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/30 px-4 py-2\">");
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
        if (!string.IsNullOrWhiteSpace(update.InstallError))
        {
            html.Append("<div class=\"mt-1 text-xs text-warning break-words\">Auto-update failed: ");
            html.Append(Encode(Shorten(update.InstallError, 120)));
            html.Append("</div>");
        }

        html.AppendLine("</div></div>");
        html.AppendLine("<div class=\"rounded-md border border-line bg-surface-alt/30 px-4 py-2 flex flex-col justify-center\">");
        html.AppendLine("<div class=\"text-xs text-muted\">Release channel</div>");
        html.Append("<div class=\"mt-1 text-xs text-muted\">Auto-update ");
        html.Append(update.AutoUpdateEnabled ? "enabled" : "disabled");
        html.AppendLine("</div>");
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

    private string GetEventFilterClasses(DragnetStoredEvent item)
    {
        var filters = new List<string> { DragnetEventFilter.All.ToString() };
        if (IsLocalEvent(item.Event))
        {
            filters.Add(DragnetEventFilter.Local.ToString());
        }
        else
        {
            if (item.ReviewState is DragnetReviewState.PendingBan or DragnetReviewState.PendingLift)
            {
                filters.Add(DragnetEventFilter.Pending.ToString());
            }
            else
            {
                filters.Add(DragnetEventFilter.Reviewed.ToString());
            }

            if (!string.IsNullOrWhiteSpace(item.ImportError))
            {
                filters.Add(DragnetEventFilter.ImportFailed.ToString());
            }

            if (item.ImportedAtUtc is not null)
            {
                filters.Add(DragnetEventFilter.Imported.ToString());
            }

            if (item.ReviewState is DragnetReviewState.DeniedBan or DragnetReviewState.DeniedLift)
            {
                filters.Add(DragnetEventFilter.Denied.ToString());
            }

            if (item.ReviewState is DragnetReviewState.IgnoredBan or DragnetReviewState.IgnoredLift)
            {
                filters.Add(DragnetEventFilter.Ignored.ToString());
            }
        }

        return string.Join(' ', filters.Distinct());
    }

    private bool EventMatchesFilter(DragnetStoredEvent item, DragnetEventFilter filter) =>
        GetEventFilterClasses(item)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(filter.ToString(), StringComparer.OrdinalIgnoreCase);

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
            html.Append("<button type=\"button\" data-dragnet-filter=\"");
            html.Append(Encode(filter.ToString()));
            html.Append("\" class=\"inline-flex items-center px-3 py-1.5 rounded-md border text-sm ");
            html.Append(activeClass);
            html.Append("\" onclick=\"dragnetFilterEvents('");
            html.Append(Encode(filter.ToString()));
            html.Append("')\">");
            html.Append(Encode(FilterLabel(filter)));
            html.AppendLine("</button>");
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
        var uri = BuildModuleUri("events", filter);
        return string.IsNullOrWhiteSpace(eventId)
            ? uri
            : $"{uri}&eventId={Uri.EscapeDataString(eventId)}";
    }

    private static string BuildModuleUri(
        string module,
        DragnetEventFilter? filter = null,
        int? ledgerPage = null)
    {
        var uri = $"/Interaction/Render/{NavigationInteractionId}?module={Uri.EscapeDataString(module)}";
        if (filter is not null)
        {
            uri += $"&filter={filter}";
        }

        if (ledgerPage is not null)
        {
            uri += $"&ledgerPage={ledgerPage.Value}";
        }

        return uri;
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
