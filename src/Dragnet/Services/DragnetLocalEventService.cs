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
    private readonly DragnetNotificationService _notificationService;
    private readonly DragnetAuditService? _auditService;
    private readonly ILogger<DragnetLocalEventService> _logger;

    public DragnetLocalEventService(
        DragnetConfiguration configuration,
        DragnetEventStore store,
        DragnetIdentityDocument identity,
        DragnetIdentityService identityService,
        DragnetAttestationService attestationService,
        DragnetNotificationService notificationService,
        ILogger<DragnetLocalEventService> logger,
        DragnetAuditService? auditService = null)
    {
        _configuration = configuration;
        _store = store;
        _identity = identity;
        _identityService = identityService;
        _attestationService = attestationService;
        _notificationService = notificationService;
        _auditService = auditService;
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

        var inserted = await _store.UpsertAsync(new DragnetStoredEvent
        {
            Event = envelope,
            ReviewState = DragnetReviewState.ApprovedBan
        }, token);
        await _attestationService.PublishAsync(
            envelope.EventId,
            DragnetBanCoverageStatus.Enforced,
            token);
        if (inserted)
        {
            await _notificationService.NotifyNewEventAsync(envelope, token);
            if (_auditService is not null)
            {
                await _auditService.RecordAsync(
                    DragnetAuditCategory.Moderation,
                    "Ban created",
                    envelope.AdminName ?? "IW4MAdmin",
                    null,
                    envelope.PlayerName,
                    envelope.PlayerNetworkId,
                    envelope.OriginName,
                    envelope.EventId,
                    envelope.Reason,
                    token);
            }
        }

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

        var priorBan = (await _store.ListAsync(token))
            .Where(item =>
                item.Event.EventType is DragnetEventType.BanCreated &&
                item.Event.OriginId.Equals(_identity.OriginId, StringComparison.OrdinalIgnoreCase) &&
                item.Event.PlayerNetworkId.Equals(
                    revokeEvent.Client.NetworkId.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item =>
                revokeEvent.Penalty.PenaltyId > 0 &&
                item.Event.Iw4mAdminPenaltyId == revokeEvent.Penalty.PenaltyId)
            .ThenByDescending(item => item.Event.CreatedAtUtc)
            .FirstOrDefault();
        if (priorBan is null)
        {
            _logger.LogDebug(
                "Ignored local penalty revoke for {PlayerName}: no originating Dragnet ban matched",
                revokeEvent.Client.CleanedName ?? revokeEvent.Client.Name);
            return;
        }

        var envelope = CreateEnvelope(
            DragnetEventType.BanLifted,
            revokeEvent,
            priorBan.Event.PenaltyKind,
            priorBan.Event.Iw4mAdminPenaltyId,
            revokeEvent.CreatedAt > DateTimeOffset.UnixEpoch
                ? revokeEvent.CreatedAt.UtcDateTime
                : DateTime.UtcNow);

        var inserted = await _store.UpsertAsync(new DragnetStoredEvent
        {
            Event = envelope,
            ReviewState = DragnetReviewState.ApprovedLift
        }, token);
        if (inserted)
        {
            await _notificationService.NotifyNewEventAsync(envelope, token);
            if (_auditService is not null)
            {
                await _auditService.RecordAsync(
                    DragnetAuditCategory.Moderation,
                    "Ban lifted",
                    envelope.AdminName ?? "IW4MAdmin",
                    null,
                    envelope.PlayerName,
                    envelope.PlayerNetworkId,
                    envelope.OriginName,
                    envelope.EventId,
                    envelope.Reason,
                    token);
            }
        }

        _logger.LogInformation(
            "Captured local Dragnet lift event {EventId} for {PlayerName}",
            envelope.EventId,
            envelope.PlayerName);
    }

    private DragnetEventEnvelope CreateEnvelope(
        DragnetEventType eventType,
        ClientPenaltyEvent penaltyEvent,
        DragnetPenaltyKind penaltyKind,
        int? penaltyIdOverride = null,
        DateTime? createdAtOverride = null)
    {
        var penalty = penaltyEvent.Penalty;
        var client = penaltyEvent.Client;
        var createdAtUtc = createdAtOverride is { } overrideValue
            ? DateTime.SpecifyKind(overrideValue, DateTimeKind.Utc)
            : NormalizeCreatedAt(penalty.When, penaltyEvent.CreatedAt);
        var penaltyId = penaltyIdOverride ?? penalty.PenaltyId;
        var expiresAtUtc = penalty.Expires is { } expires
            ? DateTime.SpecifyKind(expires, DateTimeKind.Utc)
            : (DateTime?)null;

        var unsignedEnvelope = new DragnetEventEnvelope
        {
            EventId = CreateEventId(eventType, penaltyId, client.NetworkId, createdAtUtc),
            EventType = eventType,
            OriginId = _identity.OriginId,
            OriginName = _identity.OriginName,
            OriginServerName = client.CurrentServer?.ServerName ?? client.CurrentServer?.Hostname ?? "Unknown Server",
            OriginEndpoint = _configuration.PublicEndpoint,
            OriginPublicKeyPem = _identity.PublicKeyPem,
            PenaltyKind = penaltyKind,
            Iw4mAdminPenaltyId = penaltyId,
            PlayerNetworkId = client.NetworkId.ToString(),
            PlayerGame = client.GameName.ToString(),
            PlayerName = client.CleanedName ?? client.Name,
            Reason = penalty.AutomatedOffense ?? penalty.Offense,
            PublicCategory = DeterminePublicCategory(penalty.AutomatedOffense ?? penalty.Offense),
            PublicReason = CreatePublicReason(penalty.AutomatedOffense ?? penalty.Offense),
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

    private DragnetBanCategory DeterminePublicCategory(string? reason)
    {
        if (_configuration.DefaultPublicCategory is not DragnetBanCategory.Other)
        {
            return _configuration.DefaultPublicCategory;
        }

        return DragnetRiskClassifier.ClassifyCategory(reason);
    }

    private string CreatePublicReason(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(_configuration.DefaultPublicReason))
        {
            return _configuration.DefaultPublicReason.Trim();
        }

        return DeterminePublicCategory(reason) switch
        {
            DragnetBanCategory.Cheating => "Cheating or unfair gameplay detected by the origin network.",
            DragnetBanCategory.BanEvasion => "Ban evasion detected by the origin network.",
            DragnetBanCategory.ExploitAbuse => "Exploit abuse detected by the origin network.",
            DragnetBanCategory.Toxicity => "Severe conduct violation handled by the origin network.",
            DragnetBanCategory.Security => "Security or service abuse handled by the origin network.",
            _ => "Moderation action shared by the origin network."
        };
    }
}
