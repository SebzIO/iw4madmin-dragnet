using Dragnet.Configuration;
using Dragnet.Models;

namespace Dragnet.Transport;

public sealed class DragnetPeerStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storePath;
    private readonly Dictionary<string, DragnetPeerRecord> _peers = new(StringComparer.OrdinalIgnoreCase);

    public DragnetPeerStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, "peers.json");
    }

    public async Task LoadAsync(DragnetConfiguration configuration, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            _peers.Clear();
            if (File.Exists(_storePath))
            {
                await using var stream = File.OpenRead(_storePath);
                var stored = await System.Text.Json.JsonSerializer.DeserializeAsync<List<DragnetPeerRecord>>(
                    stream,
                    DragnetJson.Options,
                    token);

                foreach (var peer in stored ?? [])
                {
                    _peers[peer.OriginId] = peer;
                }
            }

            foreach (var peer in configuration.BootstrapPeers.Where(peer => peer.Enabled && !string.IsNullOrWhiteSpace(peer.Endpoint)))
            {
                var originId = string.IsNullOrWhiteSpace(peer.ExpectedOriginId)
                    ? peer.Endpoint.TrimEnd('/')
                    : peer.ExpectedOriginId;

                if (_peers.TryGetValue(originId, out var existing))
                {
                    existing.Endpoint = peer.Endpoint.TrimEnd('/');
                    existing.IsBootstrap = true;
                    existing.LastError = null;
                }
                else
                {
                    _peers[originId] = new DragnetPeerRecord
                    {
                        OriginId = originId,
                        OriginName = peer.Endpoint,
                        Endpoint = peer.Endpoint.TrimEnd('/'),
                        IsBootstrap = true
                    };
                }
            }

            await SaveUnlockedAsync(token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DragnetPeerRecord>> ListAsync(CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            return _peers.Values
                .OrderByDescending(peer => peer.LastSeenUtc)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertAsync(DragnetPeerInfo peerInfo, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(peerInfo.PublicEndpoint))
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(peerInfo.OriginId, out var existing))
            {
                existing.OriginName = peerInfo.OriginName;
                existing.Endpoint = peerInfo.PublicEndpoint.TrimEnd('/');
                existing.LastSeenUtc = DateTimeOffset.UtcNow;
                existing.LastError = null;
            }
            else
            {
                _peers[peerInfo.OriginId] = new DragnetPeerRecord
                {
                    OriginId = peerInfo.OriginId,
                    OriginName = peerInfo.OriginName,
                    Endpoint = peerInfo.PublicEndpoint.TrimEnd('/'),
                    LastSeenUtc = DateTimeOffset.UtcNow
                };
            }

            await SaveUnlockedAsync(token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkErrorAsync(string originId, string error, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var existing))
            {
                existing.LastError = error;
                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkEventBatchSentAsync(
        string originId,
        IReadOnlyList<DragnetEventEnvelope> sentEvents,
        CancellationToken token)
    {
        if (sentEvents.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var existing))
            {
                var latestSentAt = sentEvents.Max(envelope => envelope.CreatedAtUtc);
                existing.LastEventSentAtUtc = existing.LastEventSentAtUtc is null ||
                                              latestSentAt > existing.LastEventSentAtUtc
                    ? latestSentAt
                    : existing.LastEventSentAtUtc;
                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearErrorAsync(string originId, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var existing))
            {
                existing.LastError = null;
                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveAsync(string originId, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            var removed = _peers.Remove(originId);
            if (removed)
            {
                await SaveUnlockedAsync(token);
            }

            return removed;
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
                _peers.Values.OrderBy(peer => peer.OriginName).ToList(),
                DragnetJson.Options,
                token);
        }

        File.Move(tempPath, _storePath, true);
    }
}
