# Dragnet for IW4MAdmin

Dragnet is a peer-to-peer IW4MAdmin plugin for sharing ban and ban-lift events between participating servers.

Remote events are review-first by default. Server owners choose which remote origins are trusted, and can separately enable automatic approval for bans and lifts from trusted origins.

## Status

This repository is in the MVP testing stage. The current implementation:

- loads as an `IPluginV2` IW4MAdmin plugin
- creates a persistent local RSA identity
- captures local ban, temp-ban, and ban-lift events from IW4MAdmin
- signs captured events with the local origin identity
- stores captured events in `Configuration/Dragnet/events.json`
- stores known peers in `Configuration/Dragnet/peers.json`
- sends outbound HTTPS heartbeat/gossip batches to configured peers
- tracks per-peer gossip cursors to avoid resending already delivered approved events
- validates inbound heartbeat sender, peer, and event batch limits
- exposes `POST /dragnet/heartbeat` for peer heartbeat/gossip
- seeds new installations through the official Dragnet bootstrap endpoint
- exposes an opt-in, read-only `GET /dragnet/directory` community directory
- adds an administrator-only Dragnet webfront interaction page
- supports webfront peer health, stale-peer visibility, error clearing, and discovered-peer removal
- supports webfront-first approve, deny, ignore, retry-import, and trust decisions
- supports webfront event filters and event detail inspection
- records reviewer identity, decision time, state transition, and reason in a persistent audit trail
- imports approved remote bans and lifts into IW4MAdmin
- queues unknown-player imports and retries them automatically when the player becomes known locally
- registers the `!dragnet` / `!dn` command for local review state management
- supports command-based peer seeding with `!dragnet peeradd`
- omits IP addresses from the event model
- discards expired temp-ban events before storing
- ignores penalties already imported with a `[Dragnet]` reason prefix to avoid propagation loops
- includes a lightweight console test harness for trust, review, import queueing, webfront render, event store, peer store, and transport behavior

## Build

```bash
dotnet build src/Dragnet/Dragnet.csproj -c Release
```

The plugin DLL is produced at:

```text
src/Dragnet/bin/Release/net10.0/Dragnet.dll
```

Copy `Dragnet.dll` into the IW4MAdmin `Plugins` directory, then restart IW4MAdmin.

After startup, open **Admin > Dragnet** and use **Configure** in the deployment-readiness panel. The guided setup persists:

- a recognizable network/community name
- the externally reachable HTTPS Dragnet endpoint
- an optional bootstrap peer
- optional public directory publication, region, and community website

The readiness panel checks identity configuration, endpoint syntax, HTTPS, public `/dragnet/health` reachability, peer connectivity, and release status. Saving setup changes requires an IW4MAdmin restart so identity and transport state remain consistent.

To create the same zip package used by GitHub releases:

```bash
dotnet restore tests/Dragnet.Tests/Dragnet.Tests.csproj
dotnet run --no-restore --project tests/Dragnet.Tests/Dragnet.Tests.csproj
./scripts/package-release.sh
```

The package is written to `artifacts/Dragnet.IW4MAdmin.Plugin-<version>.zip` and contains:

- `Plugins/Dragnet.dll`
- `Configuration/DragnetSettings.example.json`
- `README.md`
- `INSTALL.txt`

Tagged pushes matching `v*` run the GitHub Actions release workflow and attach the zip package to the GitHub release.

## Configuration

IW4MAdmin creates/loads the plugin configuration as `DragnetSettings`. The default data directory is `Configuration/Dragnet`.

Example configuration:

```json
{
  "Enabled": true,
  "OriginName": "My IW4MAdmin Network",
  "PublicEndpoint": "https://example.com/dragnet",
  "DirectoryListingEnabled": false,
  "DirectoryRegion": "North America",
  "DirectoryWebsite": "https://example.com",
  "DataDirectory": "Configuration/Dragnet",
  "RequireHttps": true,
  "TrustForwardedHttpsHeader": true,
  "MaxEventsPerHeartbeat": 100,
  "MaxKnownPeersPerHeartbeat": 50,
  "ImportApprovedEvents": true,
  "PeerHeartbeatInterval": "00:01:00",
  "PeerStaleAfter": "00:10:00",
  "PeerFailureThreshold": 3,
  "UpdateCheckEnabled": true,
  "UpdateCheckInterval": "06:00:00",
  "PageLoadUpdateCheckMaxAge": "00:05:00",
  "ReleaseApiUrl": "https://api.github.com/repos/SebzIO/iw4madmin-dragnet/releases/latest",
  "ReleaseFeedUrl": "https://github.com/SebzIO/iw4madmin-dragnet/releases.atom",
  "WebfrontPermission": "Administrator",
  "ReviewPermission": "Administrator",
  "TrustPermission": "Administrator",
  "PeerManagementPermission": "Administrator",
  "CommandPermission": "Administrator",
  "BootstrapPeers": [
    {
      "Endpoint": "https://mw2.sebz.xyz/dragnet",
      "ExpectedOriginId": null,
      "Enabled": true
    }
  ],
  "TrustedOrigins": []
}
```

`PublicEndpoint` should be the externally reachable Dragnet base URL for this IW4MAdmin instance. Peers call `POST {PublicEndpoint}/heartbeat`.

`GET {PublicEndpoint}/health` is an anonymous, read-only installation check containing the Dragnet version, origin fingerprint, display name, and monitored server count. It does not expose players, bans, trust configuration, keys, or peer details.

`GET {PublicEndpoint}/directory` is an anonymous, read-only list of live networks that explicitly set `DirectoryListingEnabled` to `true`. Listings contain the network name, Dragnet endpoint, optional region and website, monitored server count, plugin version, origin fingerprint, and last-seen time. They do not expose players, bans, trust configuration, private keys, or local review decisions.

Directory publication is informational only. Appearing in the directory never trusts an origin, approves a ban, imports a penalty, or changes local review policy.

`PeerFailureThreshold` controls how many consecutive heartbeat failures are required before a peer is shown as errored. A successful heartbeat clears the failure count and visible error automatically.

The dashboard checks the official GitHub releases API in the background and caches the result. Opening the Dragnet dashboard refreshes release information when the cached check is older than `PageLoadUpdateCheckMaxAge` (five minutes by default). Concurrent page loads share the same check. If the API is unavailable or rate-limited, Dragnet falls back to the repository's public Atom release feed. `UpdateCheckEnabled` disables outbound checks, while `UpdateCheckInterval` controls the background refresh interval.

## Automatic message tokens

Dragnet registers native IW4MAdmin message tokens for use in the existing `AutoMessages` rotation:

- `{{DRAGNETSERVERS}}`: participating game servers advertised by live Dragnet nodes
- `{{DRAGNETNODES}}`: participating IW4MAdmin/Dragnet nodes, including the local node
- `{{DRAGNETBANS}}`: unique ban-created events known to the local Dragnet node
- `{{DRAGNETSTATS}}`: a complete formatted statistics message

Using these tokens does not create a separate broadcast timer. IW4MAdmin expands them when their normal automatic-message slot is reached.

Peer discovery is gossip-based. New configurations include `https://mw2.sebz.xyz/dragnet` as the official bootstrap endpoint so a new node can join the live peer graph after its public endpoint is working. Existing configurations are not silently rewritten. Operators can replace or supplement the seed in `BootstrapPeers`, or add one at runtime with:

```text
!dragnet peeradd https://peer.example.com/dragnet
```

If you already know the peer's origin fingerprint, pin it:

```text
!dragnet peeradd https://peer.example.com/dragnet <expectedOriginId>
```

After the first successful heartbeat, each peer advertises the other peers it knows and the graph can expand without every server manually listing every other server.

The official bootstrap is a discovery convenience, not a trust authority. Every remote origin remains untrusted until a local operator explicitly trusts it.

## Reverse Proxy

If IW4MAdmin is served behind a reverse proxy, the proxy must support Blazor Server WebSockets. Without this, the Dragnet page can appear briefly and then be replaced by IW4MAdmin's generic error UI.

For Nginx/Nginx Proxy Manager:

- enable Websockets Support for the IW4MAdmin proxy host
- forward `Upgrade` and `Connection` headers
- use HTTP/1.1 to the upstream
- keep long read/send timeouts for Blazor Server

Equivalent Nginx settings:

```nginx
proxy_http_version 1.1;
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection "upgrade";
proxy_set_header Host $host;
proxy_set_header X-Forwarded-Proto $scheme;
proxy_read_timeout 3600;
proxy_send_timeout 3600;
```

If Cloudflare or another proxy sits in front of the origin, WebSockets must be enabled there too.

## Permissions

Current behavior:

- webfront Dragnet navigation and actions default to IW4MAdmin `Administrator`
- the in-game `!dragnet` command defaults to IW4MAdmin `Administrator`
- server owners can configure `WebfrontPermission`, `ReviewPermission`, `TrustPermission`, `PeerManagementPermission`, and `CommandPermission`
- `POST /dragnet/heartbeat` is anonymous by design because peer servers must be able to reach it

## Commands

- `!dragnet identity`
- `!dragnet peers`
- `!dragnet peeradd <https-url> [expectedOriginId]`
- `!dragnet pending`
- `!dragnet lifts`
- `!dragnet info <eventId>`
- `!dragnet approve <eventId>`
- `!dragnet deny <eventId> [reason]`
- `!dragnet ignore <eventId>`
- `!dragnet trust <eventId>`
- `!dragnet trustauto <eventId>`
- `!dragnet untrust <eventId>`
- `!dragnet liftapprove <eventId>`
- `!dragnet liftdeny <eventId> [reason]`
- `!dragnet liftignore <eventId>`

Trust commands persist changes to `DragnetSettings`.

## Local Smoke Test

1. Build and copy `Dragnet.dll` into IW4MAdmin's `Plugins` directory.
2. Restart IW4MAdmin.
3. Check the IW4MAdmin log for `Dragnet loaded`.
4. Run `!dragnet identity` in-game.
5. Open the IW4MAdmin webfront as an administrator and click the Dragnet admin nav item.
6. Ban/temp-ban a test client and confirm the event appears in the Dragnet webfront or `Configuration/Dragnet/events.json`.

For peer testing, run two IW4MAdmin instances with reachable HTTPS endpoints. On at least one instance, either configure `BootstrapPeers` to point at the other Dragnet endpoint and restart IW4MAdmin, or run `!dragnet peeradd <other-endpoint>`.

## Tests

```bash
dotnet run --no-restore --project tests/Dragnet.Tests/Dragnet.Tests.csproj
```

## Known Gaps

- broader tests for IW4MAdmin import/local event integration
- more complete operator documentation for multi-peer production deployments
