using Dragnet.Commands;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Services;
using Dragnet.Storage;
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
    private readonly DragnetIdentityDocument _identity;
    private readonly IManager _manager;

    public string Name => "Dragnet";

    public string Author => "Sebz";

    public string Version => "0.1.0";

    public Plugin(
        ILogger<Plugin> logger,
        DragnetLocalEventService localEventService,
        DragnetEventStore eventStore,
        DragnetIdentityDocument identity,
        IManager manager)
    {
        _logger = logger;
        _manager = manager;
        _localEventService = localEventService;
        _eventStore = eventStore;
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
        serviceCollection.AddSingleton<DragnetLocalEventService>();
        serviceCollection.AddSingleton<IManagerCommand, DragnetCommand>();
    }

    private async Task OnLoad(IManager manager, CancellationToken token)
    {
        await _eventStore.LoadAsync(token);

        _logger.LogInformation(
            "Dragnet loaded for IW4MAdmin {Version} with {ServerCount} server(s)",
            _manager.Version,
            _manager.GetServers().Count);
    }

    public void Dispose()
    {
        IManagementEventSubscriptions.Load -= OnLoad;
        IManagementEventSubscriptions.ClientPenaltyAdministered -= _localEventService.CapturePenaltyAsync;
        IManagementEventSubscriptions.ClientPenaltyRevoked -= _localEventService.CapturePenaltyRevokeAsync;
        _logger.LogInformation("Dragnet unloaded");
    }
}
