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

                RemoveDuplicateProvisionalEndpoints();
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

    public async Task<IReadOnlyList<DragnetPeerRecord>> SelectForGossipAsync(
        int maximumPeers,
        TimeSpan staleAfter,
        string? excludedOriginId,
        string? excludedEndpoint,
        CancellationToken token)
    {
        if (maximumPeers <= 0)
        {
            return [];
        }

        await _lock.WaitAsync(token);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var normalizedExcludedEndpoint = excludedEndpoint?.TrimEnd('/');
            var selected = _peers.Values
                .Where(peer =>
                    string.IsNullOrWhiteSpace(peer.LastError) &&
                    now - peer.LastSeenUtc <= staleAfter &&
                    !string.Equals(peer.OriginId, excludedOriginId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(
                        peer.Endpoint.TrimEnd('/'),
                        normalizedExcludedEndpoint,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(peer => peer.LastAdvertisedAtUtc is not null)
                .ThenBy(peer => peer.LastAdvertisedAtUtc)
                .ThenByDescending(peer => peer.IdentityVerified)
                .ThenBy(peer => peer.ConsecutiveFailures)
                .ThenByDescending(peer => peer.LastSeenUtc)
                .Take(maximumPeers)
                .ToList();

            foreach (var peer in selected)
            {
                peer.LastAdvertisedAtUtc = now;
            }

            if (selected.Count > 0)
            {
                await SaveUnlockedAsync(token);
            }

            return selected;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertAsync(
        DragnetPeerInfo peerInfo,
        CancellationToken token,
        bool identityVerified = false,
        bool clearFailureState = false)
    {
        if (string.IsNullOrWhiteSpace(peerInfo.PublicEndpoint))
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            var normalizedEndpoint = peerInfo.PublicEndpoint.TrimEnd('/');
            var observedAtUtc = identityVerified ? peerInfo.SeenAtUtc : DateTimeOffset.UtcNow;
            var endpointMatch = _peers.Values.FirstOrDefault(peer =>
                peer.Endpoint.TrimEnd('/').Equals(normalizedEndpoint, StringComparison.OrdinalIgnoreCase));

            if (endpointMatch is not null &&
                !endpointMatch.OriginId.Equals(peerInfo.OriginId, StringComparison.OrdinalIgnoreCase))
            {
                if (IsProvisionalOriginId(peerInfo.OriginId) &&
                    !IsProvisionalOriginId(endpointMatch.OriginId))
                {
                    if (endpointMatch.IdentityVerified && !identityVerified)
                    {
                        return;
                    }

                    endpointMatch.LastSeenUtc = observedAtUtc;
                    endpointMatch.ServerCount = Math.Max(endpointMatch.ServerCount, peerInfo.ServerCount);
                    ApplyDirectoryMetadata(endpointMatch, peerInfo);
                    ApplyIdentityProof(endpointMatch, peerInfo, identityVerified);
                    if (clearFailureState)
                    {
                        ClearFailureState(endpointMatch);
                    }
                    await SaveUnlockedAsync(token);
                    return;
                }

                if (!IsProvisionalOriginId(peerInfo.OriginId) &&
                    IsProvisionalOriginId(endpointMatch.OriginId))
                {
                    _peers.Remove(endpointMatch.OriginId);
                }
            }

            if (_peers.TryGetValue(peerInfo.OriginId, out var existing))
            {
                if (existing.IdentityVerified && !identityVerified)
                {
                    return;
                }

                existing.OriginName = peerInfo.OriginName;
                existing.Endpoint = normalizedEndpoint;
                existing.LastSeenUtc = observedAtUtc;
                existing.ServerCount = peerInfo.ServerCount;
                ApplyDirectoryMetadata(existing, peerInfo);
                ApplyIdentityProof(existing, peerInfo, identityVerified);
                if (clearFailureState)
                {
                    ClearFailureState(existing);
                }
            }
            else
            {
                _peers[peerInfo.OriginId] = new DragnetPeerRecord
                {
                    OriginId = peerInfo.OriginId,
                    OriginName = peerInfo.OriginName,
                    Endpoint = normalizedEndpoint,
                    ServerCount = peerInfo.ServerCount,
                    DirectoryListed = peerInfo.DirectoryListed,
                    Region = peerInfo.Region,
                    Website = peerInfo.Website,
                    Version = peerInfo.Version,
                    PublicKeyPem = peerInfo.PublicKeyPem,
                    Signature = peerInfo.Signature,
                    IdentityVerified = identityVerified,
                    LastSeenUtc = observedAtUtc
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
        CancellationToken token,
        bool identityVerified = false)
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
            var previouslyVerified = relatedRecords.Any(peer => peer.IdentityVerified);
            var previousEndpointVerifiedAtUtc = relatedRecords
                .Where(peer => peer.EndpointVerifiedAtUtc is not null)
                .Select(peer => peer.EndpointVerifiedAtUtc)
                .Max();
            var previousProof = relatedRecords
                .Where(peer => peer.IdentityVerified)
                .OrderByDescending(peer => peer.EndpointVerifiedAtUtc)
                .FirstOrDefault();
            var preserveVerifiedMetadata = previouslyVerified && !identityVerified && previousProof is not null;
            var lastAdvertisedAtUtc = relatedRecords
                .Where(peer => peer.LastAdvertisedAtUtc is not null)
                .Select(peer => peer.LastAdvertisedAtUtc)
                .Max();

            foreach (var related in relatedRecords)
            {
                _peers.Remove(related.OriginId);
            }

            var healthy = new DragnetPeerRecord
            {
                OriginId = receiver.OriginId,
                OriginName = preserveVerifiedMetadata ? previousProof!.OriginName : receiver.OriginName,
                Endpoint = preserveVerifiedMetadata
                    ? previousProof!.Endpoint
                    : receiver.PublicEndpoint.TrimEnd('/'),
                FirstSeenUtc = firstSeenUtc,
                LastSeenUtc = DateTimeOffset.UtcNow,
                LastEventSentAtUtc = lastEventSentAtUtc,
                ServerCount = preserveVerifiedMetadata ? previousProof!.ServerCount : receiver.ServerCount,
                DirectoryListed = preserveVerifiedMetadata ? previousProof!.DirectoryListed : receiver.DirectoryListed,
                Region = preserveVerifiedMetadata ? previousProof!.Region : receiver.Region,
                Website = preserveVerifiedMetadata ? previousProof!.Website : receiver.Website,
                Version = preserveVerifiedMetadata ? previousProof!.Version : receiver.Version,
                PublicKeyPem = identityVerified ? receiver.PublicKeyPem : previousProof?.PublicKeyPem,
                Signature = identityVerified ? receiver.Signature : previousProof?.Signature,
                IdentityVerified = identityVerified || previouslyVerified,
                EndpointVerifiedAtUtc = identityVerified
                    ? DateTimeOffset.UtcNow
                    : previousEndpointVerifiedAtUtc,
                LastAdvertisedAtUtc = lastAdvertisedAtUtc,
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

    private static bool IsProvisionalOriginId(string originId) =>
        Uri.TryCreate(originId, UriKind.Absolute, out _);

    private void RemoveDuplicateProvisionalEndpoints()
    {
        var duplicateGroups = _peers.Values
            .GroupBy(peer => peer.Endpoint.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in duplicateGroups)
        {
            var canonical = group
                .Where(peer => !IsProvisionalOriginId(peer.OriginId))
                .OrderByDescending(peer => peer.IsBootstrap)
                .ThenByDescending(peer => peer.LastSeenUtc)
                .FirstOrDefault();
            if (canonical is null)
            {
                continue;
            }

            foreach (var provisional in group.Where(peer =>
                         peer.OriginId != canonical.OriginId &&
                         IsProvisionalOriginId(peer.OriginId)))
            {
                canonical.IsBootstrap |= provisional.IsBootstrap;
                canonical.ServerCount = Math.Max(canonical.ServerCount, provisional.ServerCount);
                if (provisional.LastSeenUtc > canonical.LastSeenUtc)
                {
                    canonical.DirectoryListed = provisional.DirectoryListed;
                    canonical.Region = provisional.Region;
                    canonical.Website = provisional.Website;
                    canonical.Version = provisional.Version;
                    canonical.PublicKeyPem = provisional.PublicKeyPem;
                    canonical.Signature = provisional.Signature;
                    canonical.IdentityVerified = provisional.IdentityVerified;
                    canonical.EndpointVerifiedAtUtc = provisional.EndpointVerifiedAtUtc;
                }
                if (canonical.LastAdvertisedAtUtc is null ||
                    provisional.LastAdvertisedAtUtc > canonical.LastAdvertisedAtUtc)
                {
                    canonical.LastAdvertisedAtUtc = provisional.LastAdvertisedAtUtc;
                }
                if (canonical.LastEventSentAtUtc is null ||
                    provisional.LastEventSentAtUtc > canonical.LastEventSentAtUtc)
                {
                    canonical.LastEventSentAtUtc = provisional.LastEventSentAtUtc;
                }

                _peers.Remove(provisional.OriginId);
            }
        }
    }

    private static void ClearFailureState(DragnetPeerRecord peer)
    {
        peer.LastError = null;
        peer.ConsecutiveFailures = 0;
        peer.LastFailureAtUtc = null;
        peer.LastFailureMessage = null;
    }

    private static void ApplyDirectoryMetadata(DragnetPeerRecord record, DragnetPeerInfo peerInfo)
    {
        record.DirectoryListed = peerInfo.DirectoryListed;
        record.Region = peerInfo.Region;
        record.Website = peerInfo.Website;
        record.Version = peerInfo.Version;
    }

    private static void ApplyIdentityProof(
        DragnetPeerRecord record,
        DragnetPeerInfo peerInfo,
        bool identityVerified)
    {
        if (identityVerified)
        {
            record.PublicKeyPem = peerInfo.PublicKeyPem;
            record.Signature = peerInfo.Signature;
            record.IdentityVerified = true;
            return;
        }

        if (!record.IdentityVerified)
        {
            record.PublicKeyPem = peerInfo.PublicKeyPem;
            record.Signature = peerInfo.Signature;
        }
    }
}
