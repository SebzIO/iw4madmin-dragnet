using Data.Models.Client;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Storage;
using SharedLibraryCore;
using SharedLibraryCore.Commands;
using SharedLibraryCore.Configuration;
using SharedLibraryCore.Interfaces;

namespace Dragnet.Commands;

public sealed class DragnetCommand : Command
{
    private readonly DragnetEventStore _store;
    private readonly DragnetIdentityDocument _identity;

    public DragnetCommand(
        CommandConfiguration config,
        ITranslationLookup translationLookup,
        DragnetEventStore store,
        DragnetIdentityDocument identity)
        : base(config, translationLookup)
    {
        _store = store;
        _identity = identity;
        Name = "dragnet";
        Alias = "dn";
        Description = "Review and manage Dragnet ban exchange events";
        Permission = EFClient.Permission.Moderator;
        RequiresTarget = false;
        Arguments =
        [
            new CommandArgument
            {
                Name = "action",
                Required = false
            },
            new CommandArgument
            {
                Name = "eventId/reason",
                Required = false
            }
        ];
    }

    public override async Task ExecuteAsync(GameEvent gameEvent)
    {
        var args = SplitArgs(gameEvent.Data);
        if (args.Length == 0)
        {
            TellHelp(gameEvent);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "identity":
                gameEvent.Origin.Tell($"Dragnet origin: {_identity.OriginName}");
                gameEvent.Origin.Tell($"Dragnet origin id: {_identity.OriginId}");
                return;

            case "pending":
                await ListPendingAsync(gameEvent, DragnetReviewState.PendingBan);
                return;

            case "lifts":
                await ListPendingAsync(gameEvent, DragnetReviewState.PendingLift);
                return;

            case "info":
                await ShowInfoAsync(gameEvent, args);
                return;

            case "approve":
                await SetStateAsync(gameEvent, args, DragnetReviewState.PendingBan, DragnetReviewState.ApprovedBan);
                return;

            case "deny":
                await SetStateAsync(gameEvent, args, DragnetReviewState.PendingBan, DragnetReviewState.DeniedBan);
                return;

            case "ignore":
                await SetStateAsync(gameEvent, args, DragnetReviewState.PendingBan, DragnetReviewState.IgnoredBan);
                return;

            case "liftapprove":
                await SetStateAsync(gameEvent, args, DragnetReviewState.PendingLift, DragnetReviewState.ApprovedLift);
                return;

            case "liftdeny":
                await SetStateAsync(gameEvent, args, DragnetReviewState.PendingLift, DragnetReviewState.DeniedLift);
                return;

            case "liftignore":
                await SetStateAsync(gameEvent, args, DragnetReviewState.PendingLift, DragnetReviewState.IgnoredLift);
                return;

            default:
                TellHelp(gameEvent);
                return;
        }
    }

    private async Task ListPendingAsync(GameEvent gameEvent, DragnetReviewState state)
    {
        await _store.ExpireElapsedTempBansAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        var events = (await _store.ListAsync(CancellationToken.None))
            .Where(item => item.ReviewState == state)
            .Take(5)
            .ToList();

        if (events.Count == 0)
        {
            gameEvent.Origin.Tell(state == DragnetReviewState.PendingBan
                ? "No pending Dragnet bans."
                : "No pending Dragnet lift events.");
            return;
        }

        foreach (var item in events)
        {
            var expires = item.Event.ExpiresAtUtc is null ? "permanent" : $"expires {item.Event.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC";
            gameEvent.Origin.Tell(
                $"{ShortId(item.Event.EventId)} | {item.Event.PlayerName} | {item.Event.OriginName} | {expires} | {item.Event.Reason}");
        }
    }

    private async Task ShowInfoAsync(GameEvent gameEvent, string[] args)
    {
        var item = await FindEventAsync(gameEvent, args);
        if (item is null)
        {
            return;
        }

        var dragnetEvent = item.Event;
        gameEvent.Origin.Tell($"Dragnet {ShortId(dragnetEvent.EventId)}: {dragnetEvent.EventType} / {item.ReviewState}");
        gameEvent.Origin.Tell($"Player: {dragnetEvent.PlayerName} ({dragnetEvent.PlayerNetworkId})");
        gameEvent.Origin.Tell($"Origin: {dragnetEvent.OriginName} / {dragnetEvent.OriginServerName}");
        gameEvent.Origin.Tell($"Penalty: {dragnetEvent.PenaltyKind}, IW4MAdmin #{dragnetEvent.Iw4mAdminPenaltyId}");
        gameEvent.Origin.Tell($"Reason: {dragnetEvent.Reason}");
        if (!string.IsNullOrWhiteSpace(dragnetEvent.EvidenceUrl))
        {
            gameEvent.Origin.Tell($"Evidence: {dragnetEvent.EvidenceUrl}");
        }
    }

    private async Task SetStateAsync(
        GameEvent gameEvent,
        string[] args,
        DragnetReviewState expectedState,
        DragnetReviewState targetState)
    {
        var item = await FindEventAsync(gameEvent, args);
        if (item is null)
        {
            return;
        }

        if (item.ReviewState != expectedState)
        {
            gameEvent.Origin.Tell($"Dragnet event is {item.ReviewState}, not {expectedState}.");
            return;
        }

        var reason = args.Length > 2 ? string.Join(' ', args.Skip(2)) : null;
        await _store.SetReviewStateAsync(item.Event.EventId, targetState, reason, CancellationToken.None);
        gameEvent.Origin.Tell($"Dragnet event {ShortId(item.Event.EventId)} marked {targetState}.");
    }

    private async Task<DragnetStoredEvent?> FindEventAsync(GameEvent gameEvent, string[] args)
    {
        if (args.Length < 2)
        {
            gameEvent.Origin.Tell("Provide a Dragnet event id.");
            return null;
        }

        var idPrefix = args[1];
        var matches = (await _store.ListAsync(CancellationToken.None))
            .Where(item => item.Event.EventId.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        gameEvent.Origin.Tell(matches.Count == 0
            ? "No Dragnet event matched that id."
            : "Multiple Dragnet events matched that id. Use a longer id prefix.");
        return null;
    }

    private void TellHelp(GameEvent gameEvent)
    {
        gameEvent.Origin.Tell("Dragnet commands: pending, lifts, info <id>, approve <id>, deny <id> [reason], ignore <id>, liftapprove <id>, liftdeny <id> [reason], identity");
    }

    private static string[] SplitArgs(string? data)
    {
        return string.IsNullOrWhiteSpace(data)
            ? []
            : data.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ShortId(string eventId)
    {
        return eventId.Length <= 12 ? eventId : eventId[..12];
    }
}
