using Dragnet.Commands;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Services;
using Dragnet.Storage;
using Dragnet.Transport;
using Dragnet.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharedLibraryCore;
using SharedLibraryCore.Helpers;
using SharedLibraryCore.Interfaces;
using SharedLibraryCore.Interfaces.Events;

namespace Dragnet;

public sealed class Plugin : IPluginV2
{
    private readonly ILogger<Plugin> _logger;
    private readonly DragnetLocalEventService _localEventService;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetImportService _importService;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetTransportService _transportService;
    private readonly DragnetStatisticsService _statisticsService;
    private readonly DragnetAttestationService _attestationService;
    private readonly DragnetUpdateService _updateService;
    private readonly DragnetWebfrontService _webfrontService;
    private readonly DragnetNotificationStore _notificationStore;
    private readonly DragnetNotificationService _notificationService;
    private readonly IInteractionRegistration _interactionRegistration;
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetIdentityDocument _identity;

    public string Name => "Dragnet";

    public string Author => "Sebz";

    public string Version => DragnetBuildInfo.Version;

    public Plugin(
        ILogger<Plugin> logger,
        DragnetConfiguration configuration,
        DragnetLocalEventService localEventService,
        DragnetImportService importService,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetTransportService transportService,
        DragnetStatisticsService statisticsService,
        DragnetAttestationService attestationService,
        DragnetUpdateService updateService,
        DragnetWebfrontService webfrontService,
        DragnetNotificationStore notificationStore,
        DragnetNotificationService notificationService,
        IInteractionRegistration interactionRegistration,
        DragnetIdentityDocument identity)
    {
        _logger = logger;
        _configuration = configuration;
        _localEventService = localEventService;
        _importService = importService;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _transportService = transportService;
        _statisticsService = statisticsService;
        _attestationService = attestationService;
        _updateService = updateService;
        _webfrontService = webfrontService;
        _notificationStore = notificationStore;
        _notificationService = notificationService;
        _interactionRegistration = interactionRegistration;
        _identity = identity;

        IManagementEventSubscriptions.Load += OnLoad;
        IManagementEventSubscriptions.ClientPenaltyAdministered += _localEventService.CapturePenaltyAsync;
        IManagementEventSubscriptions.ClientPenaltyRevoked += _localEventService.CapturePenaltyRevokeAsync;
        IManagementEventSubscriptions.ClientStateInitialized += _importService.RetryQueuedForClientAsync;
        IManagementEventSubscriptions.ClientStateAuthorized += _importService.RetryQueuedForClientAsync;

        _logger.LogInformation(
            "Dragnet {Version} initialized as {OriginName} ({OriginId})",
            Version,
            _identity.OriginName,
            _identity.OriginId);
    }

    public static void RegisterDependencies(IServiceCollection serviceCollection)
    {
        serviceCollection.AddConfiguration<DragnetConfiguration>(
            "DragnetSettings",
            DragnetConfiguration.CreateDefault());
        serviceCollection.AddSingleton(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<DragnetConfiguration>();
            return new DragnetIdentityService(Path.GetFullPath(configuration.DataDirectory));
        });
        serviceCollection.AddSingleton(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<DragnetConfiguration>();
            var identityService = serviceProvider.GetRequiredService<DragnetIdentityService>();
            return identityService.LoadOrCreate(configuration.OriginName);
        });
        serviceCollection.AddSingleton(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<DragnetConfiguration>();
            return new DragnetEventStore(Path.GetFullPath(configuration.DataDirectory));
        });
        serviceCollection.AddSingleton(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<DragnetConfiguration>();
            return new DragnetPeerStore(Path.GetFullPath(configuration.DataDirectory));
        });
        serviceCollection.AddSingleton(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<DragnetConfiguration>();
            return new DragnetNotificationStore(Path.GetFullPath(configuration.DataDirectory));
        });
        serviceCollection.AddSingleton<DragnetLocalEventService>();
        serviceCollection.AddSingleton<DragnetTrustService>();
        serviceCollection.AddSingleton<Func<IManager>>(serviceProvider =>
            () => serviceProvider.GetRequiredService<IManager>());
        serviceCollection.AddSingleton<Func<int>>(serviceProvider =>
        {
            var managerFactory = serviceProvider.GetRequiredService<Func<IManager>>();
            return () => managerFactory().GetServers().Count;
        });
        serviceCollection.AddSingleton<Func<IReadOnlyList<string>>>(serviceProvider =>
        {
            var managerFactory = serviceProvider.GetRequiredService<Func<IManager>>();
            return () => managerFactory().GetServers()
                .Select(server => server.ServerName ?? server.Hostname)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        });
        serviceCollection.AddSingleton<DragnetAttestationService>();
        serviceCollection.AddSingleton<DragnetImportService>();
        serviceCollection.AddSingleton<DragnetReviewService>();
        serviceCollection.AddSingleton(serviceProvider =>
        {
            var eventStore = serviceProvider.GetRequiredService<DragnetEventStore>();
            var peerStore = serviceProvider.GetRequiredService<DragnetPeerStore>();
            var localServerCount = serviceProvider.GetRequiredService<Func<int>>();
            return new DragnetStatisticsService(
                eventStore,
                peerStore,
                localServerCount,
                serviceProvider.GetRequiredService<DragnetConfiguration>());
        });
        serviceCollection.AddSingleton<DragnetTransportService>();
        serviceCollection.AddSingleton<DragnetUpdateService>();
        serviceCollection.AddSingleton<DragnetOnboardingService>();
        serviceCollection.AddSingleton<DragnetDirectoryService>();
        serviceCollection.AddSingleton<DragnetLedgerService>();
        serviceCollection.AddSingleton<DragnetNetworkProfileService>();
        serviceCollection.AddSingleton<DragnetNotificationService>();
        serviceCollection.AddSingleton<DragnetWebfrontService>();
        serviceCollection.AddSingleton<IManagerCommand, DragnetCommand>();
    }

    private async Task OnLoad(IManager manager, CancellationToken token)
    {
        await _eventStore.LoadAsync(token);
        await _peerStore.LoadAsync(_configuration, token);
        await _notificationStore.LoadAsync(token);
        await _attestationService.BackfillAsync(token);
        RegisterMessageTokens(manager);
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.NavigationInteractionId,
            (_, _, interactionToken) => _webfrontService.CreateNavigationInteractionAsync(interactionToken));
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.LedgerNavigationInteractionId,
            (_, _, interactionToken) => _webfrontService.CreateLedgerNavigationInteractionAsync(interactionToken));
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.ReviewInteractionId,
            (_, _, interactionToken) => _webfrontService.CreateReviewInteractionAsync(interactionToken));
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.TrustInteractionId,
            (_, _, interactionToken) => _webfrontService.CreateTrustInteractionAsync(interactionToken));
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.PeerInteractionId,
            (_, _, interactionToken) => _webfrontService.CreatePeerInteractionAsync(interactionToken));
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.SetupInteractionId,
            (_, _, interactionToken) => _webfrontService.CreateSetupInteractionAsync(interactionToken));
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.NotificationInteractionId,
            (_, _, interactionToken) => _webfrontService.CreateNotificationInteractionAsync(interactionToken));
        _transportService.Start();
        _updateService.Start();
        _notificationService.Start();

        _logger.LogInformation(
            "Dragnet loaded for IW4MAdmin {Version} with {ServerCount} server(s)",
            manager.Version,
            manager.GetServers().Count);
    }

    private void RegisterMessageTokens(IManager manager)
    {
        var tokens = manager.GetMessageTokens();
        foreach (var existing in tokens
                     .Where(token => token.Name.StartsWith("DRAGNET", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            tokens.Remove(existing);
        }

        tokens.Add(new MessageToken("DRAGNETSERVERS", async _ =>
            (await _statisticsService.GetAsync(CancellationToken.None)).ParticipatingServerCount.ToString()));
        tokens.Add(new MessageToken("DRAGNETNODES", async _ =>
            (await _statisticsService.GetAsync(CancellationToken.None)).ParticipatingNodeCount.ToString()));
        tokens.Add(new MessageToken("DRAGNETBANS", async _ =>
            (await _statisticsService.GetAsync(CancellationToken.None)).SharedBanCount.ToString()));
        tokens.Add(new MessageToken("DRAGNETSTATS", async _ =>
        {
            var statistics = await _statisticsService.GetAsync(CancellationToken.None);
            return "(Color::Accent)DRAGNET (Color::White)connects " +
                   $"(Color::Accent){statistics.ParticipatingServerCount} (Color::White)servers across " +
                   $"(Color::Accent){statistics.ParticipatingNodeCount} (Color::White)networks and has shared " +
                   $"(Color::Accent){statistics.SharedBanCount} (Color::White)bans.";
        }));
    }

    public void Dispose()
    {
        IManagementEventSubscriptions.Load -= OnLoad;
        IManagementEventSubscriptions.ClientPenaltyAdministered -= _localEventService.CapturePenaltyAsync;
        IManagementEventSubscriptions.ClientPenaltyRevoked -= _localEventService.CapturePenaltyRevokeAsync;
        IManagementEventSubscriptions.ClientStateInitialized -= _importService.RetryQueuedForClientAsync;
        IManagementEventSubscriptions.ClientStateAuthorized -= _importService.RetryQueuedForClientAsync;
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.NavigationInteractionId);
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.ReviewInteractionId);
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.TrustInteractionId);
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.PeerInteractionId);
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.SetupInteractionId);
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.NotificationInteractionId);
        _transportService.StopAsync().GetAwaiter().GetResult();
        _notificationService.StopAsync().GetAwaiter().GetResult();
        _logger.LogInformation("Dragnet unloaded");
    }
}
