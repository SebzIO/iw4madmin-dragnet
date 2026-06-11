using Data.Models;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Storage;
using Microsoft.Extensions.Logging;
using SharedLibraryCore.Events.Management;

namespace Dragnet.Services;

public sealed class DragnetLocalEventService
{
    private const string ImportedReasonPrefix = "[Dragnet]";

    private readonly DragnetConfiguration _configuration;
    private readonly DragnetEventStore _store;
    private readonly DragnetIdentityDocument _identity;
    private readonly DragnetIdentityService _identityService;
    private readonly DragnetAttestationService _attestationService;
    private readonly ILogger<DragnetLocalEventService> _logger;

    public DragnetLocalEventService(
        DragnetConfiguration configuration,
        DragnetEventStore store,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        DragnetAttestationService attestationService,
        ILogger<DragnetLocalEventService> logger)
    {
        _configuration = configuration;
        _store = store;
        _identity = identity;
        _identityService = identityService;
        _attestationService = attestationService;
        _logger = logger;
    }

    public async Task CapturePenaltyAsync(ClientPenaltyEvent penaltyEvent, CancellationToken token)
    {
        if (!_configuration.Enabled || IsImportedPenalty(penaltyEvent.Penalty))
        {
            return;
        }

        if (penaltyEvent.Penalty.Type is not (EFPenalty.PenaltyType.Ban or EFPenalty.PenaltyType.TempBan))
        {
            return;
        }

        var envelope = CreateEnvelope(
            DragnetEventType.BanCreated,
            penaltyEvent,
            penaltyEvent.Penalty.Type == EFPenalty.PenaltyType.Ban
                ? DragnetPenaltyKind.Ban
                : DragnetPenaltyKind.TempBan);

        if (envelope.IsExpired(DateTimeOffset.UtcNow))
        {
            return;
        }

        await _store.UpsertAsync(new DragnetStoredEvent
        {
            Event = envelope,
            ReviewState = DragnetReviewState.ApprovedBan
        }, token);
        await _attestationService.PublishAsync(
            envelope.EventId,
            DragnetBanCoverageStatus.Enforced,
            token);

        _logger.LogInformation(
            "Captured local Dragnet ban event {EventId} for {PlayerName}",
            envelope.EventId,
            envelope.PlayerName);
    }

    public async Task CapturePenaltyRevokeAsync(ClientPenaltyRevokeEvent revokeEvent, CancellationToken token)
    {
        if (!_configuration.Enabled || IsImportedPenalty(revokeEvent.Penalty))
        {
            return;
        }

        if (revokeEvent.Penalty.Type is not (EFPenalty.PenaltyType.Ban or EFPenalty.PenaltyType.TempBan))
        {
            return;
        }

        var envelope = CreateEnvelope(
            DragnetEventType.BanLifted,
            revokeEvent,
            revokeEvent.Penalty.Type == EFPenalty.PenaltyType.Ban
                ? DragnetPenaltyKind.Ban
                : DragnetPenaltyKind.TempBan);

        await _store.UpsertAsync(new DragnetStoredEvent
        {
            Event = envelope,
            ReviewState = DragnetReviewState.ApprovedLift
        }, token);

        _logger.LogInformation(
            "Captured local Dragnet lift event {EventId} for {PlayerName}",
            envelope.EventId,
            envelope.PlayerName);
    }

    private DragnetEventEnvelope CreateEnvelope(
        DragnetEventType eventType,
        ClientPenaltyEvent penaltyEvent,
        DragnetPenaltyKind penaltyKind)
    {
        var penalty = penaltyEvent.Penalty;
        var client = penaltyEvent.Client;
        var createdAtUtc = NormalizeCreatedAt(penalty.When, penaltyEvent.CreatedAt);
        var expiresAtUtc = penalty.Expires is { } expires
            ? DateTime.SpecifyKind(expires, DateTimeKind.Utc)
            : (DateTime?)null;

        var unsignedEnvelope = new DragnetEventEnvelope
        {
            EventId = CreateEventId(eventType, penalty.PenaltyId, client.NetworkId, createdAtUtc),
            EventType = eventType,
            OriginId = _identity.OriginId,
            OriginName = _identity.OriginName,
            OriginServerName = client.CurrentServer?.ServerName ?? client.CurrentServer?.Hostname ?? "Unknown Server",
            OriginEndpoint = _configuration.PublicEndpoint,
            OriginPublicKeyPem = _identity.PublicKeyPem,
            PenaltyKind = penaltyKind,
            Iw4mAdminPenaltyId = penalty.PenaltyId,
            PlayerNetworkId = client.NetworkId.ToString(),
            PlayerGame = client.GameName.ToString(),
            PlayerName = client.CleanedName ?? client.Name,
            Reason = penalty.AutomatedOffense ?? penalty.Offense,
            AdminName = penalty.Punisher?.CurrentAlias?.Name,
            CreatedAtUtc = new DateTimeOffset(createdAtUtc),
            ExpiresAtUtc = expiresAtUtc is null ? null : new DateTimeOffset(expiresAtUtc.Value),
            Signature = ""
        };

        return unsignedEnvelope with
        {
            Signature = _identityService.Sign(_identity, unsignedEnvelope.GetSigningPayload())
        };
    }

    private static DateTime NormalizeCreatedAt(DateTime penaltyWhen, DateTimeOffset eventCreatedAt)
    {
        if (penaltyWhen > DateTime.UnixEpoch)
        {
            return DateTime.SpecifyKind(penaltyWhen, DateTimeKind.Utc);
        }

        if (eventCreatedAt > DateTimeOffset.UnixEpoch)
        {
            return eventCreatedAt.UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    private string CreateEventId(
        DragnetEventType eventType,
        int penaltyId,
        long networkId,
        DateTime createdAtUtc)
    {
        var input = $"{_identity.OriginId}:{eventType}:{penaltyId}:{networkId}:{createdAtUtc:O}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }

    private static bool IsImportedPenalty(EFPenalty penalty)
    {
        return penalty.Offense.StartsWith(ImportedReasonPrefix, StringComparison.OrdinalIgnoreCase) ||
               (penalty.AutomatedOffense?.StartsWith(ImportedReasonPrefix, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
