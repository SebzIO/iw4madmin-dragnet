using Dragnet.Configuration;
using Dragnet.Models;
using SharedLibraryCore.Interfaces;

namespace Dragnet.Services;

public sealed class DragnetTrustService
{
    private readonly DragnetConfiguration _configuration;
    private readonly IConfigurationHandlerV2<DragnetConfiguration> _configurationHandler;

    public DragnetTrustService(
        DragnetConfiguration configuration,
        IConfigurationHandlerV2<DragnetConfiguration> configurationHandler)
    {
        _configuration = configuration;
        _configurationHandler = configurationHandler;
    }

    public DragnetTrustDecision Evaluate(DragnetEventEnvelope envelope)
    {
        var trust = FindTrust(envelope.OriginId);
        if (trust is null)
        {
            return new DragnetTrustDecision(false, false, null);
        }

        var autoApprove = envelope.EventType switch
        {
            DragnetEventType.BanCreated => trust.AutoApproveBans,
            DragnetEventType.BanLifted => trust.AutoApproveLifts,
            _ => false
        };

        return new DragnetTrustDecision(true, autoApprove, trust);
    }

    public DragnetTrustConfiguration? FindTrust(string originId) =>
        _configuration.TrustedOrigins.FirstOrDefault(origin =>
            string.Equals(origin.OriginId, originId, StringComparison.OrdinalIgnoreCase));

    public async Task<DragnetTrustConfiguration> TrustAsync(
        string originId,
        string displayName,
        bool autoApproveBans,
        bool autoApproveLifts,
        CancellationToken token)
    {
        var existing = FindTrust(originId);
        if (existing is null)
        {
            existing = new DragnetTrustConfiguration
            {
                OriginId = originId,
                DisplayName = displayName,
                AutoApproveBans = autoApproveBans,
                AutoApproveLifts = autoApproveLifts
            };
            _configuration.TrustedOrigins.Add(existing);
        }
        else
        {
            existing.DisplayName = displayName;
            existing.AutoApproveBans = autoApproveBans || existing.AutoApproveBans;
            existing.AutoApproveLifts = autoApproveLifts || existing.AutoApproveLifts;
        }

        await _configurationHandler.Set(_configuration);
        return existing;
    }

    public async Task<bool> UntrustAsync(string originId, CancellationToken token)
    {
        var removed = _configuration.TrustedOrigins.RemoveAll(origin =>
            string.Equals(origin.OriginId, originId, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            await _configurationHandler.Set(_configuration);
        }

        return removed;
    }
}

public sealed record DragnetTrustDecision(
    bool IsTrusted,
    bool AutoApprove,
    DragnetTrustConfiguration? Configuration);
