using Data.Models;
using Dragnet.Configuration;
using Dragnet.Models;
using Dragnet.Storage;
using Microsoft.Extensions.Logging;
using SharedLibraryCore;
using SharedLibraryCore.Database.Models;
using SharedLibraryCore.Interfaces;

namespace Dragnet.Services;

public sealed class DragnetImportService
{
    private const string ImportedReasonPrefix = "[Dragnet]";

    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _store;
    private readonly IManager _manager;
    private readonly ILogger<DragnetImportService> _logger;

    public DragnetImportService(
        DragnetConfiguration configuration,
        DragnetEventStore store,
        IManager manager,
        ILogger<DragnetImportService> logger)
    {
        _configuration = configuration;
        _store = store;
        _manager = manager;
        _logger = logger;
    }

    public async Task<DragnetImportResult> ImportApprovedAsync(
        DragnetStoredEvent storedEvent,
        CancellationToken token)
    {
        if (!_configuration.ImportApprovedEvents)
        {
            return DragnetImportResult.Skipped("Approved event import is disabled.");
        }

        if (storedEvent.ImportedAtUtc is not null)
        {
            return DragnetImportResult.Skipped("Dragnet event was already imported.");
        }

        var client = await ResolveClientAsync(storedEvent.Event);
        if (client is null)
        {
            var message = "No local IW4MAdmin client matched the Dragnet event network id and game.";
            await _store.SetImportResultAsync(storedEvent.Event.EventId, false, null, message, token);
            return DragnetImportResult.Failed(message);
        }

        var console = CreateConsoleClient();
        if (console is null)
        {
            var message = "No IW4MAdmin server is available to provide a console import origin.";
            await _store.SetImportResultAsync(storedEvent.Event.EventId, false, null, message, token);
            return DragnetImportResult.Failed(message);
        }

        try
        {
            switch (storedEvent.Event.EventType)
            {
                case DragnetEventType.BanCreated:
                    await ImportBanAsync(storedEvent.Event, client, console);
                    break;

                case DragnetEventType.BanLifted:
                    await ImportLiftAsync(storedEvent.Event, client, console);
                    break;

                default:
                    return DragnetImportResult.Failed($"Unsupported Dragnet event type {storedEvent.Event.EventType}.");
            }

            await _store.SetImportResultAsync(storedEvent.Event.EventId, true, null, null, token);
            return DragnetImportResult.ImportedEvent("Imported Dragnet event into IW4MAdmin.");
        }
        catch (Exception ex)
        {
            await _store.SetImportResultAsync(storedEvent.Event.EventId, false, null, ex.Message, token);
            _logger.LogWarning(ex, "Failed to import Dragnet event {EventId}", storedEvent.Event.EventId);
            return DragnetImportResult.Failed(ex.Message);
        }
    }

    private async Task ImportBanAsync(
        DragnetEventEnvelope envelope,
        EFClient client,
        EFClient console)
    {
        if (envelope.IsExpired(DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException("Cannot import an expired Dragnet temp-ban event.");
        }

        var reason = FormatImportedReason(envelope);
        var activeClient = _manager.FindActiveClient(client);
        if (activeClient is { IsIngame: true, CurrentServer: not null })
        {
            if (envelope.PenaltyKind is DragnetPenaltyKind.TempBan)
            {
                await activeClient.CurrentServer.TempBan(
                    reason,
                    envelope.ExpiresAtUtc!.Value - DateTimeOffset.UtcNow,
                    activeClient,
                    console);
                return;
            }

            await activeClient.CurrentServer.Ban(reason, activeClient, console);
            return;
        }

        var penalty = new EFPenalty
        {
            Type = envelope.PenaltyKind is DragnetPenaltyKind.TempBan
                ? EFPenalty.PenaltyType.TempBan
                : EFPenalty.PenaltyType.Ban,
            Expires = envelope.ExpiresAtUtc?.UtcDateTime,
            Offender = client,
            Punisher = console,
            Link = client.AliasLink,
            Offense = reason
        };

        await _manager.GetPenaltyService().Create(penalty);
    }

    private async Task ImportLiftAsync(
        DragnetEventEnvelope envelope,
        EFClient client,
        EFClient console)
    {
        var reason = FormatImportedReason(envelope);
        var activeClient = _manager.FindActiveClient(client);
        if (activeClient is { IsIngame: true, CurrentServer: not null })
        {
            await activeClient.CurrentServer.Unban(reason, activeClient, console);
            return;
        }

        await _manager.GetPenaltyService().RemoveActivePenalties(
            client.AliasLinkId,
            client.NetworkId,
            client.GameName,
            client.CurrentAlias?.IPAddress,
            [EFPenalty.PenaltyType.Ban, EFPenalty.PenaltyType.TempBan]);

        var penalty = new EFPenalty
        {
            Type = EFPenalty.PenaltyType.Unban,
            Expires = DateTime.UtcNow,
            Offender = client,
            Punisher = console,
            Link = client.AliasLink,
            Offense = reason,
            When = DateTime.UtcNow,
            Active = true
        };

        await _manager.GetPenaltyService().Create(penalty);
    }

    private async Task<EFClient?> ResolveClientAsync(DragnetEventEnvelope envelope)
    {
        if (!long.TryParse(envelope.PlayerNetworkId, out var networkId))
        {
            return null;
        }

        if (Enum.TryParse<Reference.Game>(envelope.PlayerGame, true, out var game))
        {
            var client = await _manager.GetClientService().GetUnique(networkId, game);
            if (client is not null)
            {
                return await _manager.GetClientService().Get(client.ClientId);
            }
        }

        var activeClient = _manager.GetActiveClients()
            .FirstOrDefault(client => client.NetworkId == networkId);
        if (activeClient is not null)
        {
            return await _manager.GetClientService().Get(activeClient.ClientId);
        }

        return null;
    }

    private EFClient? CreateConsoleClient()
    {
        var server = _manager.GetServers().FirstOrDefault();
        return server?.AsConsoleClient();
    }

    private static string FormatImportedReason(DragnetEventEnvelope envelope) =>
        $"{ImportedReasonPrefix} {envelope.OriginName}: {envelope.Reason}";
}

public sealed record DragnetImportResult(bool Success, bool Imported, string Message)
{
    public static DragnetImportResult ImportedEvent(string message) => new(true, true, message);

    public static DragnetImportResult Skipped(string message) => new(true, false, message);

    public static DragnetImportResult Failed(string message) => new(false, false, message);
}
