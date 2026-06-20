using Data.Models;
using Dragnet.Configuration;
using Dragnet.Models;
using Dragnet.Storage;
using Microsoft.Extensions.Logging;
using SharedLibraryCore.Database.Models;
using SharedLibraryCore.Events.Management;
using SharedLibraryCore.Interfaces;

namespace Dragnet.Services;

public sealed class DragnetWatchlistService
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _store;
    private readonly Func<IManager> _managerFactory;
    private readonly ILogger<DragnetWatchlistService> _logger;

    public DragnetWatchlistService(
        DragnetConfiguration configuration,
        DragnetEventStore store,
        Func<IManager> managerFactory,
        ILogger<DragnetWatchlistService> logger)
    {
        _configuration = configuration;
        _store = store;
        _managerFactory = managerFactory;
        _logger = logger;
    }

    public async Task NotifyAdminsForClientAsync(ClientStateEvent clientStateEvent, CancellationToken token)
    {
        if (!_configuration.Enabled ||
            !_configuration.WatchlistJoinAlertsEnabled ||
            _configuration.ParticipationMode is not DragnetParticipationMode.IntelligenceOnly)
        {
            return;
        }

        var client = clientStateEvent.Client;
        var now = DateTimeOffset.UtcNow;
        await _store.ExpireElapsedTempBansAsync(now, token);
        var matches = (await _store.ListAsync(token))
            .Where(item => item.ReviewState is DragnetReviewState.WatchlistedBan)
            .Where(item => !item.Event.IsExpired(now))
            .Where(item => MatchesClient(item.Event, client))
            .Where(item => item.LastWatchlistAlertedAtUtc is null ||
                           now - item.LastWatchlistAlertedAtUtc.Value >= _configuration.WatchlistJoinAlertCooldown)
            .Take(5)
            .ToList();
        if (matches.Count == 0)
        {
            return;
        }

        var admins = _managerFactory().GetActiveClients()
            .Where(activeClient => activeClient.Level >= _configuration.WatchlistAlertPermission)
            .GroupBy(activeClient => activeClient.ClientId)
            .Select(group => group.First())
            .ToList();
        if (admins.Count == 0)
        {
            return;
        }

        foreach (var match in matches)
        {
            var category = match.Event.PublicCategory?.ToString() ?? DragnetRiskClassifier.ClassifyCategory(match.Event.Reason).ToString();
            var publicReason = string.IsNullOrWhiteSpace(match.Event.PublicReason)
                ? match.Event.Reason
                : match.Event.PublicReason;
            var shortId = DragnetReviewService.ShortId(match.Event.EventId);
            foreach (var admin in admins)
            {
                admin.Tell(
                    $"^5Dragnet watchlist:^7 {client.CleanedName ?? client.Name} matched {category} flag " +
                    $"from {match.Event.OriginName} ({shortId}). {publicReason}");
            }

            await _store.SetWatchlistAlertedAsync(match.Event.EventId, now, token);
            _logger.LogInformation(
                "Sent Dragnet watchlist alert for {PlayerName} from {OriginName}",
                client.CleanedName ?? client.Name,
                match.Event.OriginName);
        }
    }

    private static bool MatchesClient(DragnetEventEnvelope envelope, EFClient client)
    {
        if (!long.TryParse(envelope.PlayerNetworkId, out var networkId) ||
            client.NetworkId != networkId)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(envelope.PlayerGame) ||
               Enum.TryParse<Reference.Game>(envelope.PlayerGame, true, out var game) &&
               client.GameName == game;
    }
}
