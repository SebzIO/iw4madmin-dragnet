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

    public async Task<IReadOnlyList<DragnetPeerRecord>> SelectHeartbeatTargetsAsync(
        TimeSpan recoveryProbeInterval,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var targets = _peers.Values
                .Where(peer =>
                    peer.QuarantinedAtUtc is null ||
                    peer.LastRecoveryProbeAtUtc is null ||
                    now - peer.LastRecoveryProbeAtUtc >= recoveryProbeInterval)
                .OrderBy(peer => peer.QuarantinedAtUtc is not null)
                .ThenByDescending(peer => peer.IsBootstrap)
                .ThenBy(peer => peer.LastRecoveryProbeAtUtc)
                .ToList();

            foreach (var peer in targets.Where(peer => peer.QuarantinedAtUtc is not null))
            {
                peer.LastRecoveryProbeAtUtc = now;
            }

            if (targets.Any(peer => peer.QuarantinedAtUtc is not null))
            {
                await SaveUnlockedAsync(token);
            }

            return targets;
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
                    peer.QuarantinedAtUtc is null &&
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
                    SupportsDeliveryAcknowledgements = peerInfo.SupportsDeliveryAcknowledgements,
                    SupportsEvidenceUpdates = peerInfo.SupportsEvidenceUpdates,
                    SupportsBanAttestations = peerInfo.SupportsBanAttestations,
                    SupportsAttestationRefreshRequests = peerInfo.SupportsAttestationRefreshRequests,
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
        int failureThreshold = 1,
        TimeSpan? quarantineAfter = null)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var existing))
            {
                var now = DateTimeOffset.UtcNow;
                existing.ConsecutiveFailures++;
                existing.FirstFailureAtUtc ??= now;
                existing.LastFailureAtUtc = now;
                existing.LastFailureMessage = error;
                existing.LastError = existing.ConsecutiveFailures >= Math.Max(1, failureThreshold)
                    ? error
                    : null;
                if (existing.LastError is not null &&
                    quarantineAfter is { } quarantineDuration &&
                    quarantineDuration > TimeSpan.Zero &&
                    now - existing.FirstFailureAtUtc.Value >= quarantineDuration)
                {
                    existing.QuarantinedAtUtc ??= now;
                }
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
            var lastEventSentId = relatedRecords
                .Where(peer => !string.IsNullOrWhiteSpace(peer.LastEventSentId))
                .OrderByDescending(peer => peer.LastEventSentAtUtc)
                .Select(peer => peer.LastEventSentId)
                .FirstOrDefault();
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
            var eventDeliveries = relatedRecords
                .SelectMany(peer => peer.EventDeliveries ?? [])
                .GroupBy(delivery => delivery.EventId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(delivery => delivery.AcknowledgedAtUtc)
                    .ThenByDescending(delivery => delivery.LastSentAtUtc)
                    .First())
                .ToList();
            var pendingAcknowledgements = relatedRecords
                .SelectMany(peer => peer.PendingAcknowledgementEventIds ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var pendingAttestationRefreshEventIds = relatedRecords
                .SelectMany(peer => peer.PendingAttestationRefreshEventIds ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

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
                LastEventSentId = lastEventSentId,
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
                SupportsDeliveryAcknowledgements = receiver.SupportsDeliveryAcknowledgements,
                SupportsEvidenceUpdates = receiver.SupportsEvidenceUpdates,
                SupportsBanAttestations = receiver.SupportsBanAttestations,
                SupportsAttestationRefreshRequests = receiver.SupportsAttestationRefreshRequests,
                EventDeliveries = eventDeliveries,
                PendingAcknowledgementEventIds = pendingAcknowledgements,
                PendingAttestationRefreshEventIds = pendingAttestationRefreshEventIds,
                LastSyncVerifiedAtUtc = relatedRecords
                    .Where(peer => peer.LastSyncVerifiedAtUtc is not null)
                    .Select(peer => peer.LastSyncVerifiedAtUtc)
                    .Max(),
                LastResyncRequestedAtUtc = relatedRecords
                    .Where(peer => peer.LastResyncRequestedAtUtc is not null)
                    .Select(peer => peer.LastResyncRequestedAtUtc)
                    .Max(),
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
        CancellationToken token,
        bool trackAcknowledgements = false)
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
                var latestSent = sentEvents
                    .OrderBy(envelope => envelope.CreatedAtUtc)
                    .ThenBy(envelope => envelope.EventId, StringComparer.OrdinalIgnoreCase)
                    .Last();
                existing.LastEventSentAtUtc = existing.LastEventSentAtUtc is null ||
                                              latestSentAt > existing.LastEventSentAtUtc
                    ? latestSentAt
                    : existing.LastEventSentAtUtc;
                existing.LastEventSentId = latestSent.EventId;
                if (trackAcknowledgements)
                {
                    existing.EventDeliveries ??= [];
                    foreach (var envelope in sentEvents)
                    {
                        var delivery = existing.EventDeliveries.FirstOrDefault(item =>
                            item.EventId.Equals(envelope.EventId, StringComparison.OrdinalIgnoreCase));
                        if (delivery is null)
                        {
                            existing.EventDeliveries.Add(new DragnetEventDeliveryRecord
                            {
                                EventId = envelope.EventId
                            });
                        }
                        else
                        {
                            delivery.LastSentAtUtc = DateTimeOffset.UtcNow;
                            delivery.SendAttempts++;
                        }
                    }
                }
                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkEventsAcknowledgedAsync(
        string originId,
        IReadOnlyList<string> eventIds,
        CancellationToken token)
    {
        if (eventIds.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            if (!_peers.TryGetValue(originId, out var peer))
            {
                return;
            }

            peer.EventDeliveries ??= [];
            var now = DateTimeOffset.UtcNow;
            var acknowledgedAny = false;
            foreach (var eventId in eventIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var delivery = peer.EventDeliveries.FirstOrDefault(item =>
                    item.EventId.Equals(eventId, StringComparison.OrdinalIgnoreCase));
                if (delivery is not null)
                {
                    delivery.AcknowledgedAtUtc = now;
                    acknowledgedAny = true;
                }
            }

            if (acknowledgedAny)
            {
                peer.LastSyncVerifiedAtUtc = now;
                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkEvidenceBatchSentAsync(
        string originId,
        IReadOnlyList<DragnetEvidenceUpdate> updates,
        CancellationToken token)
    {
        if (updates.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var peer))
            {
                peer.EventDeliveries ??= [];
                foreach (var update in updates)
                {
                    var delivery = peer.EventDeliveries.FirstOrDefault(item =>
                        item.EventId.Equals(update.UpdateId, StringComparison.OrdinalIgnoreCase));
                    if (delivery is null)
                    {
                        peer.EventDeliveries.Add(new DragnetEventDeliveryRecord
                        {
                            EventId = update.UpdateId
                        });
                    }
                    else
                    {
                        delivery.LastSentAtUtc = DateTimeOffset.UtcNow;
                        delivery.SendAttempts++;
                    }
                }

                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkAttestationBatchSentAsync(
        string originId,
        IReadOnlyList<DragnetBanAttestation> attestations,
        CancellationToken token)
    {
        if (attestations.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var peer))
            {
                peer.EventDeliveries ??= [];
                foreach (var attestation in attestations)
                {
                    var deliveryKey = AttestationDeliveryKey(attestation);
                    var delivery = peer.EventDeliveries.FirstOrDefault(item =>
                        item.EventId.Equals(deliveryKey, StringComparison.OrdinalIgnoreCase));
                    if (delivery is null)
                    {
                        peer.EventDeliveries.Add(new DragnetEventDeliveryRecord
                        {
                            EventId = deliveryKey
                        });
                    }
                    else
                    {
                        delivery.LastSentAtUtc = DateTimeOffset.UtcNow;
                        delivery.SendAttempts++;
                    }
                }

                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task QueueAcknowledgementsAsync(
        string originId,
        IReadOnlyList<string> eventIds,
        CancellationToken token)
    {
        if (eventIds.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var peer))
            {
                peer.PendingAcknowledgementEventIds ??= [];
                foreach (var eventId in eventIds)
                {
                    if (!peer.PendingAcknowledgementEventIds.Contains(eventId, StringComparer.OrdinalIgnoreCase))
                    {
                        peer.PendingAcknowledgementEventIds.Add(eventId);
                    }
                }

                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetPendingAcknowledgementsAsync(
        string originId,
        int maximum,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            return _peers.TryGetValue(originId, out var peer)
                ? (peer.PendingAcknowledgementEventIds ?? []).Take(Math.Max(0, maximum)).ToList()
                : [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkAcknowledgementsSentAsync(
        string originId,
        IReadOnlyList<string> eventIds,
        CancellationToken token)
    {
        if (eventIds.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var peer))
            {
                peer.PendingAcknowledgementEventIds ??= [];
                peer.PendingAcknowledgementEventIds.RemoveAll(eventId =>
                    eventIds.Contains(eventId, StringComparer.OrdinalIgnoreCase));
                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> QueueAttestationRefreshAsync(
        string originId,
        IReadOnlyList<string> eventIds,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (!_peers.TryGetValue(originId, out var peer))
            {
                return false;
            }

            peer.PendingAttestationRefreshEventIds ??= [];
            foreach (var eventId in eventIds
                         .Where(eventId => !string.IsNullOrWhiteSpace(eventId))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!peer.PendingAttestationRefreshEventIds.Contains(
                        eventId,
                        StringComparer.OrdinalIgnoreCase))
                {
                    peer.PendingAttestationRefreshEventIds.Add(eventId);
                }
            }

            await SaveUnlockedAsync(token);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetPendingAttestationRefreshAsync(
        string originId,
        int maximum,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            return _peers.TryGetValue(originId, out var peer)
                ? (peer.PendingAttestationRefreshEventIds ?? [])
                    .Take(Math.Max(0, maximum))
                    .ToList()
                : [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkAttestationRefreshSentAsync(
        string originId,
        IReadOnlyList<string> eventIds,
        CancellationToken token)
    {
        if (eventIds.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(token);
        try
        {
            if (_peers.TryGetValue(originId, out var peer))
            {
                peer.PendingAttestationRefreshEventIds ??= [];
                peer.PendingAttestationRefreshEventIds.RemoveAll(eventId =>
                    eventIds.Contains(eventId, StringComparer.OrdinalIgnoreCase));
                await SaveUnlockedAsync(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RequestResyncAsync(string originId, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (!_peers.TryGetValue(originId, out var peer))
            {
                return false;
            }

            peer.EventDeliveries = [];
            peer.LastEventSentAtUtc = null;
            peer.LastEventSentId = null;
            peer.LastSyncVerifiedAtUtc = null;
            peer.LastResyncRequestedAtUtc = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(token);
            return true;
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
                    canonical.LastEventSentId = provisional.LastEventSentId;
                }

                _peers.Remove(provisional.OriginId);
            }
        }
    }

    private static void ClearFailureState(DragnetPeerRecord peer)
    {
        peer.LastError = null;
        peer.ConsecutiveFailures = 0;
        peer.FirstFailureAtUtc = null;
        peer.LastFailureAtUtc = null;
        peer.LastFailureMessage = null;
        peer.QuarantinedAtUtc = null;
        peer.LastRecoveryProbeAtUtc = null;
    }

    private static void ApplyDirectoryMetadata(DragnetPeerRecord record, DragnetPeerInfo peerInfo)
    {
        record.DirectoryListed = peerInfo.DirectoryListed;
        record.Region = peerInfo.Region;
        record.Website = peerInfo.Website;
        record.Version = peerInfo.Version;
        record.SupportsDeliveryAcknowledgements = peerInfo.SupportsDeliveryAcknowledgements;
        record.SupportsEvidenceUpdates = peerInfo.SupportsEvidenceUpdates;
        record.SupportsBanAttestations = peerInfo.SupportsBanAttestations;
        record.SupportsAttestationRefreshRequests = peerInfo.SupportsAttestationRefreshRequests;
    }

    public static string AttestationDeliveryKey(DragnetBanAttestation attestation) =>
        $"{attestation.AttestationId}:{attestation.UpdatedAtUtc.UtcTicks}";

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
