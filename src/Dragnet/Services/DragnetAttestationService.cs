using System.Security.Cryptography;
using System.Text;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Storage;

namespace Dragnet.Services;

public sealed class DragnetAttestationService
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _eventStore;
    private readonly DragnetIdentityDocument _identity;
    private readonly DragnetIdentityService _identityService;
    private readonly Func<int> _localServerCount;
    private readonly Func<IReadOnlyList<string>> _localServerNames;

    public DragnetAttestationService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        Func<int> localServerCount)
        : this(configuration, eventStore, identity, identityService, localServerCount, () => [])
    {
    }

    public DragnetAttestationService(
        DragnetConfiguration configuration,
        DragnetEventStore eventStore,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        Func<int> localServerCount,
        Func<IReadOnlyList<string>> localServerNames)
    {
        _configuration = configuration;
        _eventStore = eventStore;
        _identity = identity;
        _identityService = identityService;
        _localServerCount = localServerCount;
        _localServerNames = localServerNames;
    }

    public async Task PublishAsync(
        string eventId,
        DragnetBanCoverageStatus status,
        CancellationToken token)
    {
        var storedEvent = await _eventStore.GetAsync(eventId, token);
        if (storedEvent?.Event.EventType is not DragnetEventType.BanCreated)
        {
            return;
        }

        var updatedAtUtc = DateTimeOffset.UtcNow;
        var unsigned = new DragnetBanAttestation
        {
            AttestationId = CreateAttestationId(_identity.OriginId, eventId),
            EventId = eventId,
            NetworkOriginId = _identity.OriginId,
            NetworkName = _identity.OriginName,
            PublicEndpoint = _configuration.PublicEndpoint,
            NetworkPublicKeyPem = _identity.PublicKeyPem,
            ServerCount = Math.Max(0, _localServerCount()),
            ServerNames = _localServerNames()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .Take(100)
                .ToList(),
            Status = status,
            UpdatedAtUtc = updatedAtUtc,
            Signature = ""
        };
        var signed = unsigned with
        {
            Signature = _identityService.Sign(_identity, unsigned.GetSigningPayload())
        };
        await _eventStore.UpsertBanAttestationAsync(signed, token);
    }

    public async Task BackfillAsync(CancellationToken token)
    {
        var events = await _eventStore.ListAsync(token);
        foreach (var item in events.Where(item =>
                     item.Event.EventType is DragnetEventType.BanCreated &&
                     (item.Event.OriginId.Equals(_identity.OriginId, StringComparison.OrdinalIgnoreCase) ||
                      item.ReviewState is DragnetReviewState.ApprovedBan)))
        {
            var status = item.Event.OriginId.Equals(_identity.OriginId, StringComparison.OrdinalIgnoreCase) ||
                         item.ImportedAtUtc is not null
                ? DragnetBanCoverageStatus.Enforced
                : item.ImportError?.StartsWith("Queued:", StringComparison.OrdinalIgnoreCase) == true
                    ? DragnetBanCoverageStatus.Queued
                    : DragnetBanCoverageStatus.Accepted;
            var current = (item.BanAttestations ?? []).FirstOrDefault(attestation =>
                attestation.NetworkOriginId.Equals(_identity.OriginId, StringComparison.OrdinalIgnoreCase));
            if (current?.Status == status &&
                current.ServerCount == Math.Max(0, _localServerCount()) &&
                current.ServerNames.SequenceEqual(
                    _localServerNames()
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name)
                        .Take(100),
                    StringComparer.OrdinalIgnoreCase) &&
                current.NetworkName.Equals(_identity.OriginName, StringComparison.Ordinal) &&
                string.Equals(
                    current.PublicEndpoint?.TrimEnd('/'),
                    _configuration.PublicEndpoint?.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await PublishAsync(item.Event.EventId, status, token);
        }
    }

    public static string CreateAttestationId(string networkOriginId, string eventId)
    {
        var idInput = $"{networkOriginId}:{eventId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idInput)))
            .ToLowerInvariant();
    }
}
