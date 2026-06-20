using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Data.Models.Client;
using Dragnet.Configuration;
using Dragnet.Models;
using Dragnet.Storage;
using Microsoft.Extensions.Logging;
using SharedLibraryCore.Alerts;
using SharedLibraryCore.Interfaces;

namespace Dragnet.Services;

public sealed class DragnetNotificationService : IDisposable
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetNotificationStore _store;
    private readonly DragnetEventStore _eventStore;
    private readonly Func<IManager> _managerFactory;
    private readonly ILogger<DragnetNotificationService> _logger;
    private readonly IAlertManager? _alertManager;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Dictionary<int, DateTimeOffset> _lastInGameSummary =
        new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    public DragnetNotificationService(
        DragnetConfiguration configuration,
        DragnetNotificationStore store,
        DragnetEventStore eventStore,
        Func<IManager> managerFactory,
        ILogger<DragnetNotificationService> logger,
        IAlertManager? alertManager = null,
        HttpClient? httpClient = null)
    {
        _configuration = configuration;
        _store = store;
        _eventStore = eventStore;
        _managerFactory = managerFactory;
        _logger = logger;
        _alertManager = alertManager;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _ownsHttpClient = httpClient is null;
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
        var risk = DragnetRiskClassifier.Assess(envelope);
        await AddAsync(new DragnetNotification
        {
            NotificationId = $"{type}:{envelope.EventId}",
            Type = type,
            EventId = envelope.EventId,
            Title = $"New Dragnet {label}",
            Message = $"{envelope.PlayerName} from {envelope.OriginName}: {envelope.Reason}",
            OriginName = envelope.OriginName,
            PlayerName = envelope.PlayerName,
            Reason = envelope.Reason,
            PlayerGame = envelope.PlayerGame,
            AdminName = envelope.AdminName,
            OriginServerName = envelope.OriginServerName,
            RiskScore = risk.Label,
            RiskSummary = risk.Summary,
            ExpiresAtUtc = envelope.ExpiresAtUtc,
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
            PlayerName = storedEvent.Event.PlayerName,
            Reason = storedEvent.Event.Reason,
            PlayerGame = storedEvent.Event.PlayerGame,
            AdminName = update.SubmittedByName,
            OriginServerName = storedEvent.Event.OriginServerName,
            CreatedAtUtc = update.CreatedAtUtc
        }, token);
    }

    public Task NotifyUpdateInstalledAsync(
        string version,
        CancellationToken token) =>
        NotifyUpdateInstalledAsync(version, null, null, token);

    public Task NotifyUpdateAvailableAsync(
        string version,
        string? releaseUrl,
        string? releaseNotes,
        CancellationToken token) =>
        AddAsync(new DragnetNotification
        {
            NotificationId = $"{DragnetNotificationType.UpdateAvailable}:{version}",
            Type = DragnetNotificationType.UpdateAvailable,
            EventId = "",
            Title = $"Dragnet {version} update available",
            Message = "A new Dragnet release is available.",
            OriginName = "Local Dragnet",
            ReleaseUrl = releaseUrl,
            ReleaseNotes = releaseNotes,
            CreatedAtUtc = DateTimeOffset.UtcNow
        }, token);

    public Task NotifyUpdateInstalledAsync(
        string version,
        string? releaseUrl,
        string? releaseNotes,
        CancellationToken token)
    {
        _alertManager?.AddAlert(new Alert.AlertState
        {
            Category = Alert.AlertCategory.Warning,
            OccuredAt = DateTime.UtcNow,
            Message = $"Dragnet {version} was installed. Restart IW4MAdmin to load the update.",
            Source = "Dragnet",
            MinimumPermission = EFClient.Permission.Administrator,
            Type = "DragnetUpdateInstalled"
        });

        return AddAsync(new DragnetNotification
        {
            NotificationId = $"{DragnetNotificationType.UpdateInstalled}:{version}",
            Type = DragnetNotificationType.UpdateInstalled,
            EventId = "",
            Title = $"Dragnet {version} update installed",
            Message = "The new plugin DLL is staged. Restart IW4MAdmin to load this update.",
            OriginName = "Local Dragnet",
            ReleaseUrl = releaseUrl,
            ReleaseNotes = releaseNotes,
            CreatedAtUtc = DateTimeOffset.UtcNow
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
                PlayerName = item.Event.PlayerName,
                Reason = item.Event.Reason,
                PlayerGame = item.Event.PlayerGame,
                AdminName = item.Event.AdminName,
                OriginServerName = item.Event.OriginServerName,
                ExpiresAtUtc = item.Event.ExpiresAtUtc,
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

        await SendWebhookAsync(notification, token);
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
            var updates = unread.Count(item => item.Type is DragnetNotificationType.UpdateAvailable or DragnetNotificationType.UpdateInstalled);
            client.Tell(
                $"^5Dragnet:^7 {unread.Count} unread alert(s): {bans} ban(s), {lifts} lift(s), " +
                $"{evidence} evidence update(s), {stale} stale review(s), {updates} update alert(s).");
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
            var action = notification.Type switch
            {
                DragnetNotificationType.NewBan => "Ban issued",
                DragnetNotificationType.NewLift => "Ban lifted",
                DragnetNotificationType.EvidenceUpdated => "Evidence updated",
                DragnetNotificationType.StaleReview => "Review overdue",
                DragnetNotificationType.UpdateAvailable => "Update available",
                DragnetNotificationType.UpdateInstalled => "Update installed",
                _ => notification.Type.ToString()
            };
            var color = notification.Type switch
            {
                DragnetNotificationType.NewBan => 0xE5484D,
                DragnetNotificationType.NewLift => 0x30A46C,
                DragnetNotificationType.EvidenceUpdated => 0x3E63DD,
                DragnetNotificationType.StaleReview => 0xF5A524,
                DragnetNotificationType.UpdateAvailable => 0x3E63DD,
                DragnetNotificationType.UpdateInstalled => 0x8E4EC6,
                _ => 0x687076
            };
            var fields = notification.Type is DragnetNotificationType.UpdateAvailable or DragnetNotificationType.UpdateInstalled
                ? CreateUpdateFields(action)
                : CreateEventFields(notification, action);
            if (!string.IsNullOrWhiteSpace(notification.OriginServerName) ||
                !string.IsNullOrWhiteSpace(notification.AdminName))
            {
                fields.Add(new
                {
                    name = "ꜱᴏᴜʀᴄᴇ",
                    value = DiscordValue(
                        $"{notification.OriginServerName ?? "Unknown server"}\n" +
                        $"Admin: {notification.AdminName ?? "Unknown"}"),
                    inline = true
                });
            }
            if (notification.ExpiresAtUtc is { } expiresAt)
            {
                fields.Add(new
                {
                    name = "ᴇxᴘɪʀᴇꜱ",
                    value = $"<t:{expiresAt.ToUnixTimeSeconds()}:F>\n<t:{expiresAt.ToUnixTimeSeconds()}:R>",
                    inline = true
                });
            }
            if (!string.IsNullOrWhiteSpace(notification.Reason))
            {
                fields.Add(new
                {
                    name = "ʀᴇᴀꜱᴏɴ",
                    value = DiscordValue(notification.Reason),
                    inline = false
                });
            }
            if (notification.Type is DragnetNotificationType.UpdateAvailable or DragnetNotificationType.UpdateInstalled &&
                !string.IsNullOrWhiteSpace(notification.ReleaseNotes))
            {
                fields.Add(new
                {
                    name = "ʀᴇʟᴇᴀꜱᴇ ɴᴏᴛᴇꜱ",
                    value = DiscordValue(NormalizeReleaseNotes(notification.ReleaseNotes)),
                    inline = false
                });
            }
            if (notification.Type is DragnetNotificationType.UpdateAvailable or DragnetNotificationType.UpdateInstalled &&
                !string.IsNullOrWhiteSpace(notification.ReleaseUrl))
            {
                fields.Add(new
                {
                    name = "ʀᴇʟᴇᴀꜱᴇ",
                    value = $"[View on GitHub]({notification.ReleaseUrl})",
                    inline = false
                });
            }

            var shouldMentionAdmins = DragnetRiskClassifier.ShouldMentionAdmins(notification);
            using var response = await _httpClient.PostAsJsonAsync(uri, new
            {
                username = "Dragnet",
                content = shouldMentionAdmins ? "@here High priority Dragnet ban requires review." : null,
                embeds = new[]
                {
                    new
                    {
                        title = notification.Title,
                        description = notification.Message,
                        color,
                        fields,
                        footer = new
                        {
                            text = "Dragnet • IW4MAdmin peer moderation"
                        },
                        timestamp = notification.CreatedAtUtc.ToUniversalTime()
                    }
                },
                allowed_mentions = new
                {
                    parse = shouldMentionAdmins ? new[] { "everyone" } : Array.Empty<string>()
                }
            }, token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Dragnet notification webhook delivery failed");
        }
    }

    private static string DiscordValue(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 1024 ? trimmed : trimmed[..1021] + "...";
    }

    private static string NormalizeReleaseNotes(string value)
    {
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        normalized = Regex.Replace(normalized, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*/\s*p\s*>", "\n\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*p(?:\s+[^>]*)?>", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*/?\s*(ul|ol)(?:\s+[^>]*)?>", "\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*li(?:\s+[^>]*)?>", "- ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*/\s*li\s*>", "\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*(strong|b)(?:\s+[^>]*)?>", "**", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*/\s*(strong|b)\s*>", "**", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*(em|i)(?:\s+[^>]*)?>", "*", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*/\s*(em|i)\s*>", "*", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*code(?:\s+[^>]*)?>", "`", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*/\s*code\s*>", "`", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*h[1-6](?:\s+[^>]*)?>", "\n**", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*/\s*h[1-6]\s*>", "**\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<\s*a\s+[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)<\s*/\s*a\s*>",
            match => $"[{StripHtml(match.Groups["text"].Value)}]({match.Groups["href"].Value})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        normalized = Regex.Replace(normalized, @"<[^>]+>", "", RegexOptions.IgnoreCase);
        normalized = WebUtility.HtmlDecode(normalized);
        normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n").Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "No release notes were provided."
            : normalized;
    }

    private static string StripHtml(string value) =>
        WebUtility.HtmlDecode(Regex.Replace(value, @"<[^>]+>", "", RegexOptions.IgnoreCase)).Trim();

    private static List<object> CreateEventFields(DragnetNotification notification, string action)
    {
        var fields = new List<object>
        {
            new
            {
                name = "ᴘʟᴀʏᴇʀ",
                value = DiscordValue(notification.PlayerName ?? "Not applicable"),
                inline = true
            },
            new
            {
                name = "ɴᴇᴛᴡᴏʀᴋ",
                value = DiscordValue(notification.OriginName),
                inline = true
            },
            new
            {
                name = "ᴀᴄᴛɪᴏɴ",
                value = DiscordValue(action),
                inline = true
            }
        };
        if (!string.IsNullOrWhiteSpace(notification.PlayerGame))
        {
            fields.Add(new
            {
                name = "ᴘʟᴀᴛꜰᴏʀᴍ",
                value = DiscordValue(notification.PlayerGame),
                inline = true
            });
        }
        var risk = !string.IsNullOrWhiteSpace(notification.RiskScore)
            ? new DragnetRiskAssessment(
                Enum.TryParse<DragnetRiskScore>(notification.RiskScore.Replace(" ", "", StringComparison.Ordinal), true, out var parsed)
                    ? parsed
                    : DragnetRiskClassifier.Assess(notification.Reason).Score,
                notification.RiskScore,
                notification.RiskSummary ?? DragnetRiskClassifier.Assess(notification.Reason).Summary,
                "",
                "")
            : DragnetRiskClassifier.Assess(notification.Reason);
        if (notification.Type is DragnetNotificationType.NewBan or DragnetNotificationType.StaleReview or DragnetNotificationType.EvidenceUpdated)
        {
            fields.Add(new
            {
                name = "ꜱᴄᴏʀᴇ",
                value = DiscordValue($"{risk.Label}\n{risk.Summary}"),
                inline = true
            });
        }

        return fields;
    }

    private static List<object> CreateUpdateFields(string action) =>
    [
        new
        {
            name = "ꜱᴏᴜʀᴄᴇ",
            value = DiscordValue("Local Dragnet"),
            inline = true
        },
        new
        {
            name = "ꜱᴛᴀᴛᴜꜱ",
            value = DiscordValue("Installed"),
            inline = true
        },
        new
        {
            name = "ᴀᴄᴛɪᴏɴ",
            value = DiscordValue(action),
            inline = true
        },
        new
        {
            name = "ʀᴇǫᴜɪʀᴇᴅ",
            value = DiscordValue("Restart IW4MAdmin"),
            inline = true
        }
    ];

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
