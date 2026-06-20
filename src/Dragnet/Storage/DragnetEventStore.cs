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

    public async Task ExpireElapsedTempBansAsync(DateTimeOffset now, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            var changed = false;
            foreach (var storedEvent in _events.Values)
            {
                if (storedEvent.ReviewState is DragnetReviewState.PendingBan or DragnetReviewState.WatchlistedBan &&
                    storedEvent.Event.IsExpired(now))
                {
                    storedEvent.ReviewState = DragnetReviewState.ExpiredBan;
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveUnlockedAsync(token);
            }
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

    public async Task<bool> UpsertAsync(DragnetStoredEvent storedEvent, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (_events.TryGetValue(storedEvent.Event.EventId, out var existing))
            {
                existing.LastSeenUtc = DateTimeOffset.UtcNow;
                await SaveUnlockedAsync(token);
                return false;
            }

            _events[storedEvent.Event.EventId] = storedEvent;
            await SaveUnlockedAsync(token);
            return true;
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
        string reviewedByName,
        int? reviewedByClientId,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (!_events.TryGetValue(eventId, out var storedEvent))
            {
                return;
            }

            var reviewedAtUtc = DateTimeOffset.UtcNow;
            var previousState = storedEvent.ReviewState;
            storedEvent.ReviewState = reviewState;
            storedEvent.LocalDecisionReason = reason;
            storedEvent.ReviewedAtUtc = reviewedAtUtc;
            storedEvent.ReviewedByName = reviewedByName;
            storedEvent.ReviewedByClientId = reviewedByClientId;
            storedEvent.AuditTrail ??= [];
            storedEvent.AuditTrail.Add(new DragnetReviewAuditEntry
            {
                ReviewedAtUtc = reviewedAtUtc,
                ReviewedByName = reviewedByName,
                ReviewedByClientId = reviewedByClientId,
                PreviousState = previousState,
                NewState = reviewState,
                Reason = reason
            });
            await SaveUnlockedAsync(token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetImportResultAsync(
        string eventId,
        bool imported,
        int? importedPenaltyId,
        string? importError,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (!_events.TryGetValue(eventId, out var storedEvent))
            {
                return;
            }

            storedEvent.ImportedPenaltyId = importedPenaltyId;
            storedEvent.ImportedAtUtc = imported ? DateTimeOffset.UtcNow : null;
            storedEvent.ImportError = importError;
            await SaveUnlockedAsync(token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> MarkRelatedWatchlistBansLiftedAsync(
        DragnetEventEnvelope liftEvent,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            var changed = 0;
            foreach (var storedEvent in _events.Values)
            {
                if (storedEvent.ReviewState is not DragnetReviewState.WatchlistedBan ||
                    storedEvent.Event.EventType is not DragnetEventType.BanCreated ||
                    !storedEvent.Event.OriginId.Equals(liftEvent.OriginId, StringComparison.OrdinalIgnoreCase) ||
                    !storedEvent.Event.PlayerNetworkId.Equals(liftEvent.PlayerNetworkId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(storedEvent.Event.PlayerGame, liftEvent.PlayerGame, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                storedEvent.ReviewState = DragnetReviewState.WatchlistLifted;
                storedEvent.LastSeenUtc = DateTimeOffset.UtcNow;
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

    public async Task SetWatchlistAlertedAsync(
        string eventId,
        DateTimeOffset alertedAtUtc,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (!_events.TryGetValue(eventId, out var storedEvent))
            {
                return;
            }

            storedEvent.LastWatchlistAlertedAtUtc = alertedAtUtc;
            await SaveUnlockedAsync(token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetEvidenceUpdateAsync(
        DragnetEvidenceUpdate update,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (!_events.TryGetValue(update.EventId, out var storedEvent) ||
                storedEvent.Event.EventType is not DragnetEventType.BanCreated ||
                !storedEvent.Event.OriginId.Equals(update.OriginId, StringComparison.OrdinalIgnoreCase) ||
                storedEvent.EvidenceUpdate is { CreatedAtUtc: var currentCreatedAt } &&
                currentCreatedAt >= update.CreatedAtUtc)
            {
                return false;
            }

            storedEvent.EvidenceUpdate = update;
            storedEvent.LastSeenUtc = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(token);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> UpsertBanAttestationAsync(
        DragnetBanAttestation attestation,
        CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            if (!_events.TryGetValue(attestation.EventId, out var storedEvent) ||
                storedEvent.Event.EventType is not DragnetEventType.BanCreated)
            {
                return false;
            }

            storedEvent.BanAttestations ??= [];
            var existing = storedEvent.BanAttestations.FirstOrDefault(item =>
                item.NetworkOriginId.Equals(attestation.NetworkOriginId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing.UpdatedAtUtc >= attestation.UpdatedAtUtc)
            {
                return false;
            }

            storedEvent.BanAttestations.RemoveAll(item =>
                item.NetworkOriginId.Equals(attestation.NetworkOriginId, StringComparison.OrdinalIgnoreCase));
            storedEvent.BanAttestations.Add(attestation);
            storedEvent.LastSeenUtc = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(token);
            return true;
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
