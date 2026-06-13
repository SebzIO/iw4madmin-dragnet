# Dragnet for IW4MAdmin

Dragnet is a peer-to-peer IW4MAdmin plugin for sharing ban and ban-lift events between participating servers.

Remote events are review-first by default. Server owners choose which remote origins are trusted, and can separately enable automatic approval for bans and lifts from trusted origins.

## Status

This repository is in the MVP testing stage. The current implementation:

- loads as an `IPluginV2` IW4MAdmin plugin
- creates a persistent local RSA identity
- captures local ban, temp-ban, and ban-lift events from IW4MAdmin
- lets origin-network administrators add or replace an HTTPS evidence URL after a ban is issued
- propagates evidence as a separately signed, acknowledged amendment without changing the original ban
- exposes an anonymous, read-only `GET /dragnet/ledger` public ban ledger on every upgraded peer
- exposes machine-readable ledger data at `GET /dragnet/ledger/data`
- adds a direct `Dragnet Ledger` link to IW4MAdmin's public `Main` navigation section
- publishes signed per-network ban attestations for accepted, queued, and enforced coverage
- consolidates duplicate events that share an origin and IW4MAdmin penalty ID while preserving their event IDs
- reconciles coverage as enforced, queued, accepted, unreported, stale, degraded, or unavailable per peer
- lets administrators request fresh coverage attestations for their network's active originated bans
- signs captured events with the local origin identity
- stores captured events in `Configuration/Dragnet/events.json`
- stores known peers in `Configuration/Dragnet/peers.json`
- sends outbound HTTPS heartbeat/gossip batches to configured peers
- tracks per-peer event acknowledgements and replays approved events until receipt is confirmed
- retains cursor-based delivery compatibility with older Dragnet peers
- validates inbound heartbeat sender, peer, and event batch limits
- exposes `POST /dragnet/heartbeat` for peer heartbeat/gossip
- seeds new installations through the official Dragnet bootstrap endpoint
- exposes an opt-in, read-only `GET /dragnet/directory` community directory
- signs peer identity advertisements and public health responses
- verifies directory endpoints after direct signed heartbeat contact
- exposes a shareable, endpoint-specific `GET /dragnet/setup-guide`
- rotates gossiped peers fairly across heartbeat batches
- persists per-peer advertisement timestamps across restarts
- adds an administrator-only Dragnet webfront interaction page
- supports webfront peer health, stale-peer visibility, delivery coverage, event resync, error clearing, and discovered-peer removal
- supports webfront-first approve, deny, ignore, retry-import, and trust decisions
- supports checkbox selection, select-all, and one-action bulk approval for trusted pending bans
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

The readiness panel separately checks identity configuration, endpoint syntax, HTTPS, public route reachability, origin fingerprint matching, signed health proof, peer connectivity, and release status. Saving setup changes requires an IW4MAdmin restart so identity and transport state remain consistent.

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
  "PeerQuarantineAfter": "00:30:00",
  "QuarantinedPeerProbeInterval": "00:10:00",
  "UpdateCheckEnabled": true,
  "AutoUpdateEnabled": true,
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

`GET {PublicEndpoint}/health` is an anonymous, read-only installation check containing the Dragnet version, origin fingerprint, display name, monitored server count, public key, timestamp, and identity signature. The public key and signature prove that the response belongs to the advertised fingerprint; no private key is exposed.

`GET {PublicEndpoint}/directory` is an anonymous, read-only list of live networks that explicitly set `DirectoryListingEnabled` to `true`. Listings contain the network name, Dragnet endpoint, optional region and website, monitored server count, plugin version, origin fingerprint, last-seen time, and verification status. They do not expose players, bans, trust configuration, private keys, or local review decisions.

Directory publication is informational only. Appearing in the directory never trusts an origin, approves a ban, imports a penalty, or changes local review policy.

A remote listing becomes verified only after the local node directly contacts its advertised endpoint and receives a fresh, valid signature for the same origin fingerprint and endpoint metadata. Signed gossip can establish identity authenticity, but it cannot by itself verify endpoint ownership. Unsigned older Dragnet versions remain transport-compatible and appear as legacy/unverified until upgraded.

`GET {PublicEndpoint}/setup-guide` provides a shareable JSON deployment checklist tailored to the configured endpoint. It contains public routes, the official bootstrap endpoint, and reverse-proxy requirements; it contains no credentials, private keys, bans, players, or trust settings.

`GET {PublicEndpoint}/ledger` is the public Dragnet ban ledger. It lists all ban-created events known to that peer, current active/expired/lifted status, evidence links, and signed network coverage. Selecting a ban shows which upgraded networks have published **Accepted**, **Queued**, or **Enforced** attestations and how many game servers those networks report. `GET {PublicEndpoint}/ledger/data` exposes the same public information as JSON.

Coverage attestations are signed by the network making the statement and are independently verified before storage or display. **Accepted** means the network approved the ban but did not import a local penalty, **Queued** means approval is retained until IW4MAdmin knows the player locally, and **Enforced** means the network imported or originated the penalty. Each attestation includes the network's signed server count and public server names, so ban detail pages identify the actual servers covered by that network. Coverage is reported against the networks currently known by the viewing peer, so temporary peer discovery differences can produce short-lived count differences between ledgers.

The ledger omits an attestation from the network that originated a ban because origin enforcement is implicit. Acceptance and enforced-server totals therefore measure propagation to other known peer networks.

Duplicate event rows are consolidated only when they have the same origin fingerprint and a positive IW4MAdmin penalty ID. Dragnet does not merge bans merely because they target the same player. The detail view retains the underlying event IDs and reports peer availability separately from signed coverage, so an offline peer is not represented as a denial.

Administrators can use **Refresh coverage** on a capable peer from the Dragnet dashboard. The request is persisted until a successful heartbeat and asks that peer to republish its current signed status for active bans originated by this network. Older peers ignore the optional request field and continue exchanging bans normally.

The public ledger does not expose IP addresses, local trust settings, reviewer identities, private decision notes, denial reasons, ignored decisions, private keys, or IW4MAdmin authentication data. Player names, game network IDs, public ban reasons, origins, evidence URLs, and signed acceptance/enforcement statements are public.

`PeerFailureThreshold` controls how many consecutive heartbeat failures are required before a peer is shown as errored. `PeerQuarantineAfter` removes a continuously failing peer from active network counts, gossip, directory listings, delivery coverage, and ledger coverage after the configured duration. Quarantined records are retained rather than deleted and are retried at `QuarantinedPeerProbeInterval`; a valid signed heartbeat automatically restores the peer. Bootstrap peers use the same quarantine and recovery path, so an unavailable bootstrap cannot prevent communication through healthy discovered peers. A successful inbound or outbound signed heartbeat clears all failure and quarantine state.

Current Dragnet peers acknowledge each valid event by event ID. The sender retains per-peer delivery state and replays unacknowledged active events, including events that share the same creation timestamp. The dashboard reports acknowledged and pending delivery coverage and provides per-peer **Verify sync** and **Resync** actions. Resync clears only that peer's delivery cursor and causes active approved events to be offered again; it does not change trust, review, or import decisions.

An acknowledgement means the remote Dragnet instance received and validated the event. It does not mean that the remote operator trusted it, approved it, or imported a penalty. Those decisions remain local to each participating network. Older peers that do not advertise acknowledgement support continue using the legacy timestamp-and-event-ID cursor.

Delivery capability negotiation is intentionally excluded from the signed identity payload so current nodes remain identity-proof compatible with `0.1.0-alpha.16`. Identity fields remain signed; the capability flag only selects acknowledgement-based or legacy cursor delivery behavior.

For a locally originated ban, administrators with `ReviewPermission` can use **Add evidence** or **Update evidence** on the event detail panel. Evidence must be an absolute HTTPS URL and is limited to 2048 characters. Dragnet signs the amendment with the originating network identity and peers verify that signature against the original ban before attaching it. Evidence does not alter a peer's trust, review, or penalty-import decision. Peers older than `0.1.0-alpha.19` continue exchanging bans normally but do not receive evidence amendments until upgraded.

`0.1.0-beta.1` introduces the public ledger protocol. Alpha peers remain compatible for bans and evidence but do not publish or relay signed coverage attestations until upgraded.

`MaxKnownPeersPerHeartbeat` limits the number of peer advertisements carried in one heartbeat, not the total number of peers Dragnet can store or contact. Eligible peers are rotated using their persisted `LastAdvertisedAtUtc` timestamp. Never-advertised peers go first, then the least recently advertised peers; verified and fully healthy peers break otherwise equal ties. Stale peers, visibly errored peers, and the heartbeat counterpart are omitted. This keeps heartbeat payloads bounded while ensuring networks beyond the configured limit still receive discovery exposure.

The dashboard checks the official GitHub releases API in the background and caches the result. Opening the Dragnet dashboard refreshes release information when the cached check is older than `PageLoadUpdateCheckMaxAge` (five minutes by default). Concurrent page loads share the same check. If the API is unavailable or rate-limited, Dragnet falls back to the repository's public Atom release feed. `UpdateCheckEnabled` disables outbound checks, while `UpdateCheckInterval` controls the background refresh interval.

When `AutoUpdateEnabled` is enabled, Dragnet downloads only the expected release ZIP from the official `SebzIO/iw4madmin-dragnet` GitHub release, validates the packaged DLL name and version, backs up the currently deployed DLL, and atomically stages the replacement. Dragnet does not restart IW4MAdmin automatically. Administrators receive a persistent notification that the update was installed and that IW4MAdmin must be restarted. A failed validation or installation leaves the running and deployed DLL unchanged.

The dashboard **Updates** module records the rollout lifecycle in `Configuration/Dragnet/update-history.json`, including release detection, download, staging, application after restart, and failures. It also compares versions reported by active peers, identifies outdated or unknown peers, and warns when the Dragnet network is split across multiple versions.

The **Diagnostics** module tracks outbound heartbeat latency, success rate, transport failures, quarantine and recovery transitions, and delivery acknowledgement backlog for each peer. Authorized administrators can download `/api/dragnet/diagnostics` as a JSON report. The export intentionally excludes webhook URLs, private keys, trust identities, player identities, and ban contents.

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

The administrator dashboard displays checkboxes beside trusted pending ban events. **Select all** selects every eligible ban on the current page, and **Approve selected** processes them through the same trust, import, attestation, reviewer, and audit logic as individual approval. A batch is limited to 100 unique events; failures are reported individually and do not prevent other selected bans from being processed.

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
