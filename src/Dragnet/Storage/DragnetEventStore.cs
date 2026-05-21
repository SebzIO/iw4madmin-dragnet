using Dragnet.Models;

namespace Dragnet.Storage;

public sealed class DragnetEventStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storePath;
    private readonly Dictionary<string, DragnetStoredEvent> _events = new(StringComparer.OrdinalIgnoreCase);

    public DragnetEventStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, "events.json");
    }

    public async Task LoadAsync(CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            _events.Clear();
            if (!File.Exists(_storePath))
            {
                return;
            }

            await using var stream = File.OpenRead(_storePath);
            var stored = await System.Text.Json.JsonSerializer.DeserializeAsync<List<DragnetStoredEvent>>(
                stream,
                DragnetJson.Options,
                token);

            foreach (var item in stored ?? [])
            {
                _events[item.Event.EventId] = item;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DragnetStoredEvent>> ListAsync(CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            return _events.Values
                .OrderByDescending(item => item.Event.CreatedAtUtc)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DragnetStoredEvent?> GetAsync(string eventId, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            return _events.GetValueOrDefault(eventId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertAsync(DragnetStoredEvent storedEvent, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (_events.TryGetValue(storedEvent.Event.EventId, out var existing))
            {
                existing.LastSeenUtc = DateTimeOffset.UtcNow;
                return;
            }

            _events[storedEvent.Event.EventId] = storedEvent;
            await SaveUnlockedAsync(token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetReviewStateAsync(
        string eventId,
        DragnetReviewState reviewState,
        string? reason,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (!_events.TryGetValue(eventId, out var storedEvent))
            {
                return;
            }

            storedEvent.ReviewState = reviewState;
            storedEvent.LocalDecisionReason = reason;
            await SaveUnlockedAsync(token);
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
                _events.Values.OrderBy(item => item.Event.CreatedAtUtc).ToList(),
                DragnetJson.Options,
                token);
        }

        File.Move(tempPath, _storePath, true);
    }
}
