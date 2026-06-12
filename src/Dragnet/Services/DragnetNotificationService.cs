using System.Net.Http.Json;
using Data.Models.Client;
using Dragnet.Configuration;
using Dragnet.Models;
using Dragnet.Storage;
using Microsoft.Extensions.Logging;
using SharedLibraryCore.Interfaces;

namespace Dragnet.Services;

public sealed class DragnetNotificationService : IDisposable
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetNotificationStore _store;
    private readonly DragnetEventStore _eventStore;
    private readonly Func<IManager> _managerFactory;
    private readonly ILogger<DragnetNotificationService> _logger;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly Dictionary<int, DateTimeOffset> _lastInGameSummary =
        new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    public DragnetNotificationService(
        DragnetConfiguration configuration,
        DragnetNotificationStore store,
        DragnetEventStore eventStore,
        Func<IManager> managerFactory,
        ILogger<DragnetNotificationService> logger)
    {
        _configuration = configuration;
        _store = store;
        _eventStore = eventStore;
        _managerFactory = managerFactory;
        _logger = logger;
    }

    public void Start()
    {
        if (_runTask is not null || !_configuration.NotificationsEnabled)
        {
            return;
        }

        _runCancellation = new CancellationTokenSource();
        _runTask = RunAsync(_runCancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_runCancellation is null || _runTask is null)
        {
            return;
        }

        await _runCancellation.CancelAsync();
        try
        {
            await _runTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _runTask = null;
        }
    }

    public async Task NotifyNewEventAsync(
        DragnetEventEnvelope envelope,
        CancellationToken token)
    {
        if (!_configuration.NotificationsEnabled)
        {
            return;
        }

        var type = envelope.EventType is DragnetEventType.BanLifted
            ? DragnetNotificationType.NewLift
            : DragnetNotificationType.NewBan;
        var label = envelope.EventType is DragnetEventType.BanLifted ? "lift" : "ban";
        await AddAsync(new DragnetNotification
        {
            NotificationId = $"{type}:{envelope.EventId}",
            Type = type,
            EventId = envelope.EventId,
            Title = $"New Dragnet {label}",
            Message = $"{envelope.PlayerName} from {envelope.OriginName}: {envelope.Reason}",
            OriginName = envelope.OriginName,
            CreatedAtUtc = DateTimeOffset.UtcNow
        }, token);
    }

    public async Task NotifyEvidenceUpdatedAsync(
        DragnetEvidenceUpdate update,
        DragnetStoredEvent storedEvent,
        CancellationToken token)
    {
        if (!_configuration.NotificationsEnabled)
        {
            return;
        }

        await AddAsync(new DragnetNotification
        {
            NotificationId = $"{DragnetNotificationType.EvidenceUpdated}:{update.UpdateId}",
            Type = DragnetNotificationType.EvidenceUpdated,
            EventId = update.EventId,
            Title = "Dragnet evidence updated",
            Message = $"{storedEvent.Event.PlayerName} from {update.OriginName} now has updated evidence.",
            OriginName = update.OriginName,
            CreatedAtUtc = update.CreatedAtUtc
        }, token);
    }

    public async Task SyncStaleReviewsAsync(CancellationToken token)
    {
        if (!_configuration.NotificationsEnabled ||
            _configuration.StalePendingReviewAfter <= TimeSpan.Zero)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - _configuration.StalePendingReviewAfter;
        var pending = (await _eventStore.ListAsync(token))
            .Where(item =>
                item.FirstSeenUtc <= cutoff &&
                item.ReviewState is DragnetReviewState.PendingBan or DragnetReviewState.PendingLift);
        foreach (var item in pending)
        {
            await AddAsync(new DragnetNotification
            {
                NotificationId = $"{DragnetNotificationType.StaleReview}:{item.Event.EventId}",
                Type = DragnetNotificationType.StaleReview,
                EventId = item.Event.EventId,
                Title = "Dragnet review is stale",
                Message = $"{item.Event.PlayerName} from {item.Event.OriginName} is still awaiting review.",
                OriginName = item.Event.OriginName,
                CreatedAtUtc = DateTimeOffset.UtcNow
            }, token);
        }
    }

    public async Task<IReadOnlyList<DragnetNotification>> ListForClientAsync(
        int clientId,
        CancellationToken token) =>
        (await _store.ListAsync(token))
        .Where(item => !(item.AcknowledgedByClientIds ?? []).Contains(clientId))
        .ToList();

    public Task<bool> AcknowledgeAsync(string notificationId, int clientId, CancellationToken token) =>
        _store.AcknowledgeAsync(notificationId, clientId, token);

    public Task<int> AcknowledgeAllAsync(int clientId, CancellationToken token) =>
        _store.AcknowledgeAllAsync(clientId, token);

    private async Task AddAsync(DragnetNotification notification, CancellationToken token)
    {
        if (!await _store.AddAsync(notification, token))
        {
            return;
        }

        _ = SendWebhookAsync(notification, CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await SyncStaleReviewsAsync(token);
                if (_configuration.InGameNotificationSummariesEnabled)
                {
                    await NotifyConnectedAdministratorsAsync(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dragnet notification cycle failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), token);
        }
    }

    private async Task NotifyConnectedAdministratorsAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var client in _managerFactory().GetActiveClients()
                     .Where(client => client.Level >= _configuration.ReviewPermission)
                     .GroupBy(client => client.ClientId)
                     .Select(group => group.First()))
        {
            if (_lastInGameSummary.TryGetValue(client.ClientId, out var lastSent) &&
                now - lastSent < _configuration.InGameNotificationSummaryInterval)
            {
                continue;
            }

            var unread = await ListForClientAsync(client.ClientId, token);
            if (unread.Count == 0)
            {
                continue;
            }

            var bans = unread.Count(item => item.Type is DragnetNotificationType.NewBan);
            var lifts = unread.Count(item => item.Type is DragnetNotificationType.NewLift);
            var evidence = unread.Count(item => item.Type is DragnetNotificationType.EvidenceUpdated);
            var stale = unread.Count(item => item.Type is DragnetNotificationType.StaleReview);
            client.Tell(
                $"^5Dragnet:^7 {unread.Count} unread alert(s): {bans} ban(s), {lifts} lift(s), " +
                $"{evidence} evidence update(s), {stale} stale review(s).");
            _lastInGameSummary[client.ClientId] = now;
        }
    }

    private async Task SendWebhookAsync(DragnetNotification notification, CancellationToken token)
    {
        if (!Uri.TryCreate(_configuration.NotificationWebhookUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(uri, new
            {
                username = "Dragnet",
                content = $"**{notification.Title}**\n{notification.Message}"
            }, token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Dragnet notification webhook delivery failed");
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _httpClient.Dispose();
    }
}
