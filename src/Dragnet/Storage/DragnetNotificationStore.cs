using Dragnet.Models;

namespace Dragnet.Storage;

public sealed class DragnetNotificationStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storePath;
    private readonly Dictionary<string, DragnetNotification> _notifications =
        new(StringComparer.OrdinalIgnoreCase);

    public DragnetNotificationStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, "notifications.json");
    }

    public async Task LoadAsync(CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            _notifications.Clear();
            if (!File.Exists(_storePath))
            {
                return;
            }

            await using var stream = File.OpenRead(_storePath);
            var stored = await System.Text.Json.JsonSerializer.DeserializeAsync<List<DragnetNotification>>(
                stream,
                DragnetJson.Options,
                token);
            foreach (var notification in stored ?? [])
            {
                _notifications[notification.NotificationId] = notification;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> AddAsync(DragnetNotification notification, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (_notifications.ContainsKey(notification.NotificationId))
            {
                return false;
            }

            _notifications[notification.NotificationId] = notification;
            await SaveUnlockedAsync(token);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DragnetNotification>> ListAsync(CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            return _notifications.Values
                .OrderByDescending(item => item.CreatedAtUtc)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> AcknowledgeAsync(
        string notificationId,
        int clientId,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (!_notifications.TryGetValue(notificationId, out var notification))
            {
                return false;
            }

            notification.AcknowledgedByClientIds ??= [];
            if (!notification.AcknowledgedByClientIds.Contains(clientId))
            {
                notification.AcknowledgedByClientIds.Add(clientId);
                await SaveUnlockedAsync(token);
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> AcknowledgeAllAsync(int clientId, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            var changed = 0;
            foreach (var notification in _notifications.Values)
            {
                notification.AcknowledgedByClientIds ??= [];
                if (notification.AcknowledgedByClientIds.Contains(clientId))
                {
                    continue;
                }

                notification.AcknowledgedByClientIds.Add(clientId);
                changed++;
            }

            if (changed > 0)
            {
                await SaveUnlockedAsync(token);
            }

            return changed;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveUnlockedAsync(CancellationToken token)
    {
        var tempPath = $"{_storePath}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await System.Text.Json.JsonSerializer.SerializeAsync(
                stream,
                _notifications.Values.OrderBy(item => item.CreatedAtUtc).ToList(),
                DragnetJson.Options,
                token);
        }

        File.Move(tempPath, _storePath, true);
    }
}
