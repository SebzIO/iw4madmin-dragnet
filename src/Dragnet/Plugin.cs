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
    private readonly IManager _manager;

    public string Name => "Dragnet";

    public string Author => "Sebz";

    public string Version => "0.1.0";

    public Plugin(
        ILogger<Plugin> logger,
        ILogger<DragnetLocalEventService> localEventLogger,
        DragnetConfiguration configuration,
        IManager manager)
    {
        _logger = logger;
        _manager = manager;

        var dataDirectory = Path.GetFullPath(configuration.DataDirectory);
        var identityService = new DragnetIdentityService(dataDirectory);
        var identity = identityService.LoadOrCreate(configuration.OriginName);
        _eventStore = new DragnetEventStore(dataDirectory);
        _localEventService = new DragnetLocalEventService(
            configuration,
            _eventStore,
            identity,
            identityService,
            localEventLogger);

        IManagementEventSubscriptions.Load += OnLoad;
        IManagementEventSubscriptions.ClientPenaltyAdministered += _localEventService.CapturePenaltyAsync;
        IManagementEventSubscriptions.ClientPenaltyRevoked += _localEventService.CapturePenaltyRevokeAsync;

        _logger.LogInformation(
            "Dragnet {Version} initialized as {OriginName} ({OriginId})",
            Version,
            identity.OriginName,
            identity.OriginId);
    }

    public static void RegisterDependencies(IServiceCollection serviceCollection)
    {
        serviceCollection.AddConfiguration<DragnetConfiguration>(
            "DragnetSettings",
            new DragnetConfiguration());
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
