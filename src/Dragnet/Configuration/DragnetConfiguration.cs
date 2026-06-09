using Data.Models.Client;

namespace Dragnet.Configuration;

public sealed class DragnetConfiguration
{
    public bool Enabled { get; set; } = true;

    public string OriginName { get; set; } = "Unnamed Dragnet Origin";

    public string? PublicEndpoint { get; set; }

    public string DataDirectory { get; set; } = "Configuration/Dragnet";

    public bool RequireHttps { get; set; } = true;

    public bool TrustForwardedHttpsHeader { get; set; } = true;

    public int MaxEventsPerHeartbeat { get; set; } = 100;

    public int MaxKnownPeersPerHeartbeat { get; set; } = 50;

    public bool ImportApprovedEvents { get; set; } = true;

    public TimeSpan PeerHeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan PeerStaleAfter { get; set; } = TimeSpan.FromMinutes(10);

    public int PeerFailureThreshold { get; set; } = 3;

    public EFClient.Permission WebfrontPermission { get; set; } = EFClient.Permission.Administrator;

    public EFClient.Permission ReviewPermission { get; set; } = EFClient.Permission.Administrator;

    public EFClient.Permission TrustPermission { get; set; } = EFClient.Permission.Administrator;

    public EFClient.Permission PeerManagementPermission { get; set; } = EFClient.Permission.Administrator;

    public EFClient.Permission CommandPermission { get; set; } = EFClient.Permission.Administrator;

    public List<DragnetPeerConfiguration> BootstrapPeers { get; set; } = [];

    public List<DragnetTrustConfiguration> TrustedOrigins { get; set; } = [];
}

public sealed class DragnetPeerConfiguration
{
    public string Endpoint { get; set; } = "";

    public string? ExpectedOriginId { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class DragnetTrustConfiguration
{
    public string OriginId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public bool AutoApproveBans { get; set; }

    public bool AutoApproveLifts { get; set; }
}
