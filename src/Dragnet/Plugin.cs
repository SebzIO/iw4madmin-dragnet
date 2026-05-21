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
using SharedLibraryCore.Interfaces;
using SharedLibraryCore.Interfaces.Events;

namespace Dragnet;

public sealed class Plugin : IPluginV2
{
    private readonly ILogger<Plugin> _logger;
    private readonly DragnetLocalEventService _localEventService;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetTransportService _transportService;
    private readonly DragnetWebfrontService _webfrontService;
    private readonly IInteractionRegistration _interactionRegistration;
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetIdentityDocument _identity;

    public string Name => "Dragnet";

    public string Author => "Sebz";

    public string Version => "0.1.0";

    public Plugin(
        ILogger<Plugin> logger,
        DragnetConfiguration configuration,
        DragnetLocalEventService localEventService,
        DragnetEventStore eventStore,
        DragnetPeerStore peerStore,
        DragnetTransportService transportService,
        DragnetWebfrontService webfrontService,
        IInteractionRegistration interactionRegistration,
        DragnetIdentityDocument identity)
    {
        _logger = logger;
        _configuration = configuration;
        _localEventService = localEventService;
        _eventStore = eventStore;
        _peerStore = peerStore;
        _transportService = transportService;
        _webfrontService = webfrontService;
        _interactionRegistration = interactionRegistration;
        _identity = identity;

        IManagementEventSubscriptions.Load += OnLoad;
        IManagementEventSubscriptions.ClientPenaltyAdministered += _localEventService.CapturePenaltyAsync;
        IManagementEventSubscriptions.ClientPenaltyRevoked += _localEventService.CapturePenaltyRevokeAsync;

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
            new DragnetConfiguration());
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
        serviceCollection.AddSingleton<DragnetLocalEventService>();
        serviceCollection.AddSingleton<DragnetTrustService>();
        serviceCollection.AddSingleton<Func<IManager>>(serviceProvider =>
            () => serviceProvider.GetRequiredService<IManager>());
        serviceCollection.AddSingleton<DragnetImportService>();
        serviceCollection.AddSingleton<DragnetReviewService>();
        serviceCollection.AddSingleton<DragnetTransportService>();
        serviceCollection.AddSingleton<DragnetWebfrontService>();
        serviceCollection.AddSingleton<IManagerCommand, DragnetCommand>();
    }

    private async Task OnLoad(IManager manager, CancellationToken token)
    {
        await _eventStore.LoadAsync(token);
        await _peerStore.LoadAsync(_configuration, token);
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.NavigationInteractionId,
            (_, _, interactionToken) => _webfrontService.CreateNavigationInteractionAsync(interactionToken));
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.ReviewInteractionId,
            (_, _, interactionToken) => _webfrontService.CreateReviewInteractionAsync(interactionToken));
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.TrustInteractionId,
            (_, _, interactionToken) => _webfrontService.CreateTrustInteractionAsync(interactionToken));
        _interactionRegistration.RegisterInteraction(
            DragnetWebfrontService.PeerInteractionId,
            (_, _, interactionToken) => _webfrontService.CreatePeerInteractionAsync(interactionToken));
        _transportService.Start();

        _logger.LogInformation(
            "Dragnet loaded for IW4MAdmin {Version} with {ServerCount} server(s)",
            manager.Version,
            manager.GetServers().Count);
    }

    public void Dispose()
    {
        IManagementEventSubscriptions.Load -= OnLoad;
        IManagementEventSubscriptions.ClientPenaltyAdministered -= _localEventService.CapturePenaltyAsync;
        IManagementEventSubscriptions.ClientPenaltyRevoked -= _localEventService.CapturePenaltyRevokeAsync;
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.NavigationInteractionId);
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.ReviewInteractionId);
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.TrustInteractionId);
        _interactionRegistration.UnregisterInteraction(DragnetWebfrontService.PeerInteractionId);
        _transportService.StopAsync().GetAwaiter().GetResult();
        _logger.LogInformation("Dragnet unloaded");
    }
}
