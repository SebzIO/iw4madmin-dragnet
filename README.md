# Dragnet for IW4MAdmin

Dragnet is a peer-to-peer IW4MAdmin plugin for sharing ban and ban-lift events between participating servers. Remote events are review-first by default; server owners can manually trust origins and separately enable automatic approval for bans and lifts.

This repository is in the MVP scaffold stage. The current implementation:

- loads as an `IPluginV2` IW4MAdmin plugin
- creates a persistent local RSA identity
- captures local ban, temp-ban, and ban-lift events from IW4MAdmin
- signs captured events with the local origin identity
- stores captured events in `Configuration/Dragnet/events.json`
- registers the `!dragnet` / `!dn` admin command for local review state management
- omits IP addresses from the event model
- discards expired temp-ban events before storing
- ignores penalties already imported with a `[Dragnet]` reason prefix to avoid propagation loops

Initial commands:

- `!dragnet identity`
- `!dragnet pending`
- `!dragnet lifts`
- `!dragnet info <eventId>`
- `!dragnet approve <eventId>`
- `!dragnet deny <eventId> [reason]`
- `!dragnet ignore <eventId>`
- `!dragnet liftapprove <eventId>`
- `!dragnet liftdeny <eventId> [reason]`
- `!dragnet liftignore <eventId>`

Next planned pieces:

- peer heartbeat and gossip transport over HTTPS
- manual trust commands
- ban/lift import into IW4MAdmin
- optional webfront integration if IW4MAdmin exposes a clean route/component hook
