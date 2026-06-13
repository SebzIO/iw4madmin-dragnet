using Dragnet.Models;

namespace Dragnet.Storage;

public sealed class DragnetAuditStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storePath;
    private readonly int _retentionLimit;
    private readonly List<DragnetAuditEntry> _entries = [];

    public DragnetAuditStore(string dataDirectory, int retentionLimit)
    {
        Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, "audit.json");
        _retentionLimit = Math.Clamp(retentionLimit, 100, 10_000);
    }

    public async Task LoadAsync(CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            _entries.Clear();
            if (!File.Exists(_storePath))
            {
                return;
            }

            await using var stream = File.OpenRead(_storePath);
            var stored = await System.Text.Json.JsonSerializer.DeserializeAsync<List<DragnetAuditEntry>>(
                stream,
                DragnetJson.Options,
                token);
            _entries.AddRange((stored ?? [])
                .OrderByDescending(item => item.OccurredAtUtc)
                .Take(_retentionLimit));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddAsync(DragnetAuditEntry entry, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            _entries.Insert(0, entry);
            if (_entries.Count > _retentionLimit)
            {
                _entries.RemoveRange(_retentionLimit, _entries.Count - _retentionLimit);
            }

            await SaveUnlockedAsync(token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DragnetAuditEntry>> ListAsync(
        int maximum,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            return _entries.Take(Math.Clamp(maximum, 1, _retentionLimit)).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveUnlockedAsync(CancellationToken token)
    {
        var temporaryPath = _storePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await System.Text.Json.JsonSerializer.SerializeAsync(
                stream,
                _entries,
                DragnetJson.Options,
                token);
        }

        File.Move(temporaryPath, _storePath, overwrite: true);
    }
}
