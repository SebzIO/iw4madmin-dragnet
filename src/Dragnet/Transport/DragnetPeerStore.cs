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
                    if (IsLocalEndpoint(peer.Endpoint, configuration.PublicEndpoint) ||
                        IsLocalEndpoint(peer.OriginId, configuration.PublicEndpoint))
                    {
                        continue;
                    }

                    _peers[peer.OriginId] = peer;
                }
            }

            foreach (var peer in configuration.BootstrapPeers.Where(peer => peer.Enabled && !string.IsNullOrWhiteSpace(peer.Endpoint)))
            {
                if (IsLocalEndpoint(peer.Endpoint, configuration.PublicEndpoint))
                {
                    continue;
                }

                var originId = string.IsNullOrWhiteSpace(peer.ExpectedOriginId)
                    ? peer.Endpoint.TrimEnd('/')
                    : peer.ExpectedOriginId;

                if (_peers.TryGetValue(originId, out var existing))
                {
                    existing.Endpoint = peer.Endpoint.TrimEnd('/');
                    existing.IsBootstrap = true;
                    ClearFailureState(existing);
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
                existing.ServerCount = peerInfo.ServerCount;
                ClearFailureState(existing);
            }
            else
            {
                _peers[peerInfo.OriginId] = new DragnetPeerRecord
                {
                    OriginId = peerInfo.OriginId,
                    OriginName = peerInfo.OriginName,
                    Endpoint = peerInfo.PublicEndpoint.TrimEnd('/'),
                    ServerCount = peerInfo.ServerCount,
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

    public async Task AddManualPeerAsync(string endpoint, string? expectedOriginId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.TrimEnd('/'), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Peer endpoint must be an absolute URL.");
        }

        var originId = string.IsNullOrWhiteSpace(expectedOriginId)
            ? uri.ToString().TrimEnd('/')
            : expectedOriginId.Trim();

        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var existing))
            {
                existing.Endpoint = uri.ToString().TrimEnd('/');
                existing.OriginName = existing.OriginName == existing.OriginId ? uri.ToString().TrimEnd('/') : existing.OriginName;
                ClearFailureState(existing);
                existing.IsBootstrap = false;
            }
            else
            {
                _peers[originId] = new DragnetPeerRecord
                {
                    OriginId = originId,
                    OriginName = uri.ToString().TrimEnd('/'),
                    Endpoint = uri.ToString().TrimEnd('/'),
                    IsBootstrap = false
                };
            }

            await SaveUnlockedAsync(token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkErrorAsync(
        string originId,
        string error,
        CancellationToken token,
        int failureThreshold = 1)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var existing))
            {
                existing.ConsecutiveFailures++;
                existing.LastFailureAtUtc = DateTimeOffset.UtcNow;
                existing.LastFailureMessage = error;
                existing.LastError = existing.ConsecutiveFailures >= Math.Max(1, failureThreshold)
                    ? error
                    : null;
                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkHeartbeatSucceededAsync(
        string attemptedOriginId,
        DragnetPeerInfo receiver,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(receiver.PublicEndpoint))
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            _peers.TryGetValue(attemptedOriginId, out var attempted);
            _peers.TryGetValue(receiver.OriginId, out var canonical);

            var matchingEndpointRecords = _peers.Values
                .Where(peer => peer.Endpoint.TrimEnd('/').Equals(
                    receiver.PublicEndpoint.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            var relatedRecords = matchingEndpointRecords
                .Append(attempted)
                .Append(canonical)
                .Where(peer => peer is not null)
                .Cast<DragnetPeerRecord>()
                .Distinct()
                .ToList();

            var firstSeenUtc = relatedRecords.Count > 0
                ? relatedRecords.Min(peer => peer.FirstSeenUtc)
                : DateTimeOffset.UtcNow;
            var lastEventSentAtUtc = relatedRecords
                .Where(peer => peer.LastEventSentAtUtc is not null)
                .Select(peer => peer.LastEventSentAtUtc)
                .Max();
            var isBootstrap = relatedRecords.Any(peer => peer.IsBootstrap);

            foreach (var related in relatedRecords)
            {
                _peers.Remove(related.OriginId);
            }

            var healthy = new DragnetPeerRecord
            {
                OriginId = receiver.OriginId,
                OriginName = receiver.OriginName,
                Endpoint = receiver.PublicEndpoint.TrimEnd('/'),
                FirstSeenUtc = firstSeenUtc,
                LastSeenUtc = DateTimeOffset.UtcNow,
                LastEventSentAtUtc = lastEventSentAtUtc,
                ServerCount = receiver.ServerCount,
                IsBootstrap = isBootstrap
            };
            ClearFailureState(healthy);
            _peers[healthy.OriginId] = healthy;
            await SaveUnlockedAsync(token);
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
                ClearFailureState(existing);
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

    private static bool IsLocalEndpoint(string value, string? publicEndpoint)
    {
        return !string.IsNullOrWhiteSpace(publicEndpoint) &&
               value.TrimEnd('/').Equals(publicEndpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearFailureState(DragnetPeerRecord peer)
    {
        peer.LastError = null;
        peer.ConsecutiveFailures = 0;
        peer.LastFailureAtUtc = null;
        peer.LastFailureMessage = null;
    }
}
