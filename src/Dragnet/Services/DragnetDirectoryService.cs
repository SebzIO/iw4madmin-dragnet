using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Transport;

namespace Dragnet.Services;

public sealed class DragnetDirectoryService
{
    private readonly DragnetConfiguration _configuration;
    private readonly DragnetIdentityDocument _identity;
    private readonly DragnetPeerStore _peerStore;
    private readonly Func<int> _localServerCount;

    public DragnetDirectoryService(
        DragnetConfiguration configuration,
        DragnetIdentityDocument identity,
        DragnetPeerStore peerStore,
        Func<int> localServerCount)
    {
        _configuration = configuration;
        _identity = identity;
        _peerStore = peerStore;
        _localServerCount = localServerCount;
    }

    public async Task<IReadOnlyList<DragnetDirectoryEntry>> ListAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<DragnetDirectoryEntry>();

        if (_configuration.DirectoryListingEnabled &&
            IsHttpsUrl(_configuration.PublicEndpoint))
        {
            entries.Add(new DragnetDirectoryEntry
            {
                OriginId = _identity.OriginId,
                OriginName = _identity.OriginName,
                Endpoint = _configuration.PublicEndpoint!.TrimEnd('/'),
                Region = Normalize(_configuration.DirectoryRegion),
                Website = NormalizeHttpsUrl(_configuration.DirectoryWebsite),
                ServerCount = Math.Max(0, _localServerCount()),
                Version = DragnetBuildInfo.Version,
                LastSeenUtc = now,
                Verified = true,
                VerifiedAtUtc = now,
                VerificationMethod = "Local signed endpoint"
            });
        }

        var peers = await _peerStore.ListAsync(token);
        entries.AddRange(peers
            .Where(peer =>
                peer.DirectoryListed &&
                DragnetPeerHealth.IsActive(peer, now, _configuration.PeerStaleAfter) &&
                IsHttpsUrl(peer.Endpoint))
            .Select(peer => new DragnetDirectoryEntry
            {
                OriginId = peer.OriginId,
                OriginName = peer.OriginName,
                Endpoint = peer.Endpoint.TrimEnd('/'),
                Region = Normalize(peer.Region),
                Website = NormalizeHttpsUrl(peer.Website),
                ServerCount = Math.Max(0, peer.ServerCount),
                Version = Normalize(peer.Version),
                LastSeenUtc = peer.LastSeenUtc,
                Verified = IsEndpointVerified(peer, now),
                VerifiedAtUtc = peer.EndpointVerifiedAtUtc,
                VerificationMethod = IsEndpointVerified(peer, now)
                    ? "Direct signed heartbeat"
                    : peer.IdentityVerified
                        ? "Signed identity; endpoint verification is pending or stale"
                        : "Legacy or unsigned peer"
            }));

        return entries
            .GroupBy(entry => entry.Endpoint, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.LastSeenUtc).First())
            .OrderBy(entry => entry.OriginName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    private static string? NormalizeHttpsUrl(string? value) =>
        IsHttpsUrl(value) ? value!.Trim().TrimEnd('/') : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool IsEndpointVerified(DragnetPeerRecord peer, DateTimeOffset now) =>
        peer.IdentityVerified &&
        peer.EndpointVerifiedAtUtc is { } verifiedAt &&
        now - verifiedAt <= _configuration.PeerStaleAfter;
}

public sealed record DragnetDirectoryEntry
{
    public required string OriginId { get; init; }
    public required string OriginName { get; init; }
    public required string Endpoint { get; init; }
    public string? Region { get; init; }
    public string? Website { get; init; }
    public required int ServerCount { get; init; }
    public string? Version { get; init; }
    public required DateTimeOffset LastSeenUtc { get; init; }
    public required bool Verified { get; init; }
    public DateTimeOffset? VerifiedAtUtc { get; init; }
    public required string VerificationMethod { get; init; }
}
