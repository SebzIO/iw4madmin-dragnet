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
- adds an administrator-only Dragnet webfront interaction page
- supports webfront peer health, stale-peer visibility, error clearing, and discovered-peer removal
- supports webfront-first approve, deny, ignore, retry-import, and trust decisions
- supports webfront event filters and event detail inspection
- imports approved remote bans and lifts into IW4MAdmin
- queues unknown-player import failures for later retry from the webfront
- registers the `!dragnet` / `!dn` command for local review state management
- omits IP addresses from the event model
- discards expired temp-ban events before storing
- ignores penalties already imported with a `[Dragnet]` reason prefix to avoid propagation loops
- includes a lightweight console test harness for trust, review, webfront render, event store, peer store, and transport behavior

## Build

```bash
dotnet build src/Dragnet/Dragnet.csproj -c Release
```

The plugin DLL is produced at:

```text
src/Dragnet/bin/Release/net10.0/Dragnet.dll
```

Copy `Dragnet.dll` into the IW4MAdmin `Plugins` directory, then restart IW4MAdmin.

## Configuration

IW4MAdmin creates/loads the plugin configuration as `DragnetSettings`. The default data directory is `Configuration/Dragnet`.

Example configuration:

```json
{
  "Enabled": true,
  "OriginName": "My IW4MAdmin Network",
  "PublicEndpoint": "https://example.com/dragnet",
  "DataDirectory": "Configuration/Dragnet",
  "RequireHttps": true,
  "TrustForwardedHttpsHeader": true,
  "MaxEventsPerHeartbeat": 100,
  "MaxKnownPeersPerHeartbeat": 50,
  "ImportApprovedEvents": true,
  "PeerHeartbeatInterval": "00:01:00",
  "PeerStaleAfter": "00:10:00",
  "BootstrapPeers": [
    {
      "Endpoint": "https://peer.example.com/dragnet",
      "ExpectedOriginId": null,
      "Enabled": true
    }
  ],
  "TrustedOrigins": []
}
```

`PublicEndpoint` should be the externally reachable Dragnet base URL for this IW4MAdmin instance. Peers call `POST {PublicEndpoint}/heartbeat`.

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

- webfront Dragnet navigation and webfront actions require IW4MAdmin `Administrator`
- `POST /dragnet/heartbeat` is anonymous by design because peer servers must be able to reach it
- the in-game `!dragnet` command is currently registered at IW4MAdmin `Moderator`

Configurable per-action rank requirements are not implemented yet. Before broader testing, the command permission should be tightened and permission levels should become server-owner configurable.

## Commands

- `!dragnet identity`
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

For peer testing, run two IW4MAdmin instances with reachable HTTPS endpoints and configure each instance's `BootstrapPeers` to point at the other instance's Dragnet endpoint.

## Tests

```bash
dotnet run --no-restore --project tests/Dragnet.Tests/Dragnet.Tests.csproj
```

## Known Gaps

- configurable Dragnet-specific permission levels
- broader tests for IW4MAdmin import/local event integration
- a repeatable package/deploy script
- more complete operator documentation for multi-peer production deployments
