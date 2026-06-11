using Data.Models.Client;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Services;
using Dragnet.Storage;
using Dragnet.Transport;
using SharedLibraryCore;
using SharedLibraryCore.Commands;
using SharedLibraryCore.Configuration;
using SharedLibraryCore.Interfaces;

namespace Dragnet.Commands;

public sealed class DragnetCommand : Command
{
    private readonly DragnetEventStore _store;
    private readonly DragnetPeerStore _peerStore;
    private readonly DragnetReviewService _reviewService;
    private readonly DragnetTrustService _trustService;
    private readonly DragnetIdentityDocument _identity;
    private readonly DragnetConfiguration _configuration;

    public DragnetCommand(
        CommandConfiguration config,
        ITranslationLookup translationLookup,
        DragnetEventStore store,
        DragnetPeerStore peerStore,
        DragnetReviewService reviewService,
        DragnetTrustService trustService,
        DragnetIdentityDocument identity,
        DragnetConfiguration configuration)
        : base(config, translationLookup)
    {
        _store = store;
        _peerStore = peerStore;
        _reviewService = reviewService;
        _trustService = trustService;
        _identity = identity;
        _configuration = configuration;
        Name = "dragnet";
        Alias = "dn";
        Description = "Review and manage Dragnet ban exchange events";
        Permission = configuration.CommandPermission;
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

            case "peers":
                await ListPeersAsync(gameEvent);
                return;

            case "peeradd":
                await AddPeerAsync(gameEvent, args);
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

            case "trust":
                await TrustOriginAsync(gameEvent, args);
                return;

            case "trustauto":
                await TrustOriginAsync(gameEvent, args, autoApproveBans: true, autoApproveLifts: true);
                return;

            case "untrust":
                await UntrustOriginAsync(gameEvent, args);
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
        var events = await _reviewService.ListPendingAsync(state, 5, CancellationToken.None);

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
                $"{DragnetReviewService.ShortId(item.Event.EventId)} | {item.Event.PlayerName} | {item.Event.OriginName} | {expires} | {item.Event.Reason}");
        }
    }

    private async Task ListPeersAsync(GameEvent gameEvent)
    {
        var peers = (await _peerStore.ListAsync(CancellationToken.None)).Take(8).ToList();
        if (peers.Count == 0)
        {
            gameEvent.Origin.Tell("No Dragnet peers are known. Add one with !dragnet peeradd <https-url> [originId].");
            return;
        }

        foreach (var peer in peers)
        {
            var state = string.IsNullOrWhiteSpace(peer.LastError)
                ? "ok"
                : $"error: {peer.LastError}";
            gameEvent.Origin.Tell(
                $"{peer.OriginName} | {peer.Endpoint} | {(peer.IsBootstrap ? "bootstrap" : "discovered/manual")} | {state}");
        }
    }

    private async Task AddPeerAsync(GameEvent gameEvent, string[] args)
    {
        if (args.Length < 2)
        {
            gameEvent.Origin.Tell("Usage: !dragnet peeradd <https-url> [expectedOriginId]");
            return;
        }

        if (!Uri.TryCreate(args[1], UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            gameEvent.Origin.Tell("Dragnet peer endpoint must be an absolute HTTPS URL.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_configuration.PublicEndpoint) &&
            uri.ToString().TrimEnd('/').Equals(_configuration.PublicEndpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            gameEvent.Origin.Tell("That endpoint is this Dragnet instance. Add a remote peer endpoint instead.");
            return;
        }

        var expectedOriginId = args.Length > 2 ? args[2] : null;
        await _peerStore.AddManualPeerAsync(uri.ToString().TrimEnd('/'), expectedOriginId, CancellationToken.None);
        gameEvent.Origin.Tell($"Added Dragnet peer {uri.ToString().TrimEnd('/')}. Heartbeat will run on the next interval.");
    }

    private async Task ShowInfoAsync(GameEvent gameEvent, string[] args)
    {
        var lookup = await FindEventAsync(gameEvent, args);
        if (lookup is null)
        {
            return;
        }

        var item = lookup;
        var dragnetEvent = item.Event;
        gameEvent.Origin.Tell($"Dragnet {DragnetReviewService.ShortId(dragnetEvent.EventId)}: {dragnetEvent.EventType} / {item.ReviewState}");
        gameEvent.Origin.Tell($"Player: {dragnetEvent.PlayerName} ({dragnetEvent.PlayerNetworkId})");
        gameEvent.Origin.Tell($"Origin: {dragnetEvent.OriginName} / {dragnetEvent.OriginServerName}");
        gameEvent.Origin.Tell($"Penalty: {dragnetEvent.PenaltyKind}, IW4MAdmin #{dragnetEvent.Iw4mAdminPenaltyId}");
        gameEvent.Origin.Tell($"Reason: {dragnetEvent.Reason}");
        if (item.ReviewedAtUtc is not null)
        {
            gameEvent.Origin.Tell(
                $"Reviewed: {item.ReviewState} by {item.ReviewedByName ?? "Unknown"} at {item.ReviewedAtUtc:yyyy-MM-dd HH:mm} UTC");
        }

        var evidenceUrl = item.EvidenceUpdate?.EvidenceUrl ?? dragnetEvent.EvidenceUrl;
        if (!string.IsNullOrWhiteSpace(evidenceUrl))
        {
            gameEvent.Origin.Tell($"Evidence: {evidenceUrl}");
        }
    }

    private async Task SetStateAsync(
        GameEvent gameEvent,
        string[] args,
        DragnetReviewState expectedState,
        DragnetReviewState targetState)
    {
        if (args.Length < 2)
        {
            gameEvent.Origin.Tell("Provide a Dragnet event id.");
            return;
        }

        var action = ToAction(expectedState, targetState);
        var reason = args.Length > 2 ? string.Join(' ', args.Skip(2)) : null;
        var result = await _reviewService.ApplyActionAsync(
            args[1],
            action,
            reason,
            GetReviewerName(gameEvent),
            gameEvent.Origin.ClientId,
            CancellationToken.None);
        gameEvent.Origin.Tell(result.Message);
    }

    private static DragnetReviewAction ToAction(
        DragnetReviewState expectedState,
        DragnetReviewState targetState)
    {
        if (expectedState is DragnetReviewState.PendingBan)
        {
            return targetState switch
            {
                DragnetReviewState.ApprovedBan => DragnetReviewAction.ApproveBan,
                DragnetReviewState.DeniedBan => DragnetReviewAction.DenyBan,
                DragnetReviewState.IgnoredBan => DragnetReviewAction.IgnoreBan,
                _ => throw new ArgumentOutOfRangeException(nameof(targetState), targetState, null)
            };
        }

        return targetState switch
        {
            DragnetReviewState.ApprovedLift => DragnetReviewAction.ApproveLift,
            DragnetReviewState.DeniedLift => DragnetReviewAction.DenyLift,
            DragnetReviewState.IgnoredLift => DragnetReviewAction.IgnoreLift,
            _ => throw new ArgumentOutOfRangeException(nameof(targetState), targetState, null)
        };
    }

    private async Task TrustOriginAsync(
        GameEvent gameEvent,
        string[] args,
        bool autoApproveBans = false,
        bool autoApproveLifts = false)
    {
        var item = await FindEventAsync(gameEvent, args);
        if (item is null)
        {
            return;
        }

        await _trustService.TrustAsync(
            item.Event.OriginId,
            item.Event.OriginName,
            autoApproveBans,
            autoApproveLifts,
            CancellationToken.None);

        gameEvent.Origin.Tell(
            $"Trusted Dragnet origin {item.Event.OriginName}.");
    }

    private async Task UntrustOriginAsync(GameEvent gameEvent, string[] args)
    {
        var item = await FindEventAsync(gameEvent, args);
        if (item is null)
        {
            return;
        }

        gameEvent.Origin.Tell(await _trustService.UntrustAsync(item.Event.OriginId, CancellationToken.None)
            ? $"Untrusted Dragnet origin {item.Event.OriginName}."
            : "That Dragnet origin was not trusted.");
    }

    private async Task<DragnetStoredEvent?> FindEventAsync(GameEvent gameEvent, string[] args)
    {
        if (args.Length < 2)
        {
            gameEvent.Origin.Tell("Provide a Dragnet event id.");
            return null;
        }

        var lookup = await _reviewService.FindByPrefixAsync(args[1], CancellationToken.None);
        if (!lookup.Result.Success)
        {
            gameEvent.Origin.Tell(lookup.Result.Message);
        }

        return lookup.Match;
    }

    private void TellHelp(GameEvent gameEvent)
    {
        gameEvent.Origin.Tell("Dragnet commands: pending, lifts, peers, peeradd <https-url> [originId], info <id>, approve <id>, deny <id> [reason], ignore <id>, trust <id>, trustauto <id>, untrust <id>, liftapprove <id>, liftdeny <id> [reason], identity");
    }

    private static string GetReviewerName(GameEvent gameEvent) =>
        gameEvent.Origin.CleanedName ??
        gameEvent.Origin.CurrentAlias?.Name ??
        gameEvent.Origin.Name ??
        $"Client #{gameEvent.Origin.ClientId}";

    private static string[] SplitArgs(string? data)
    {
        return string.IsNullOrWhiteSpace(data)
            ? []
            : data.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
