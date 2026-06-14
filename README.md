# battlenet-prefill-daemon

A long-running **daemon** wrapper around [tpill90/battlenet-lancache-prefill](https://github.com/tpill90/battlenet-lancache-prefill).
It prefills a [LanCache](https://lancache.net/) with games from the **Battle.net / Blizzard CDN** so subsequent
client downloads are served from your local cache.

This image is designed to be driven by **[lancache-manager](https://github.com/regix1/lancache-manager)** over a
Unix-domain-socket (or TCP) IPC channel, exactly like the sibling `steam-prefill-daemon` and `epic-prefill-daemon`
images. lancache-manager spawns the container, connects to the socket, and issues commands (select apps, prefill,
clear cache, …) while receiving real-time progress events.

## Battle.net is anonymous — no account login

Unlike Steam (user / password / 2FA) and Epic (OAuth), **Battle.net prefill is fully anonymous**. Blizzard's TACT/CDN
content is publicly fetchable over plain HTTP, so:

- There is **no account login**, no credentials, and no OAuth flow.
- The daemon reports itself as ready/logged-in **immediately on connect**.
- `get-owned-games` returns the **fixed TACT product catalog** (~23 products), not a personal library.
- The only security layer is the optional **socket HMAC handshake** (`PREFILL_SOCKET_SECRET`) — that secures the
  IPC transport, *not* any Blizzard account.

## Container image

```
ghcr.io/regix1/battlenet-prefill-daemon:latest
```

Multi-arch (`linux/amd64` + `linux/arm64`), built and pushed by `.github/workflows/docker-build.yml`.

The binary runs as **PID 1** (no `entrypoint.sh`). It listens on a Unix Domain Socket by default
(`/responses/daemon.sock`) and only switches to TCP when `PREFILL_TCP_PORT` is set (useful for Windows Docker Desktop
bind mounts). There is no `EXPOSE` and no docker-compose — lancache-manager launches and wires the container
programmatically.

### Volumes

| Path | Purpose |
|---|---|
| `/responses` | Daemon socket + responses (shared with lancache-manager) |
| `/commands` | Reserved for command exchange |
| `/app/Config` | Persisted user state (`selectedAppsToPrefill.json`) |
| `/app/.cache` | Downloaded archive indexes / metadata (safe to delete) |

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `PREFILL_RESPONSES_DIR` | `/responses` | Directory holding the socket + responses |
| `PREFILL_SOCKET_PATH` | `<responsesDir>/daemon.sock` | Unix socket path |
| `PREFILL_TCP_PORT` | *(unset)* | If set (>0), listen on TCP instead of a Unix socket |
| `PREFILL_SOCKET_SECRET` | *(unset)* | Shared HMAC secret; when set the first command must be `auth` |
| `LANCACHE_IP` | *(unset)* | Override LanCache detection (bypass poisoned-DNS resolution) |

Hidden CLI flags (passed as container args): `--verbose` / `--debug`, `--no-download`, `--nocache` / `--no-cache`.

## Command surface (socket IPC)

Length-prefixed JSON (`[4-byte LE Int32 length][UTF-8 JSON]`). Request → single response with the same `id`;
progress is pushed as unsolicited `progress` events.

| Command | Params | Notes |
|---|---|---|
| `auth` | `secret` | Socket handshake (only when `PREFILL_SOCKET_SECRET` is set) |
| `status` | — | `{ isLoggedIn:true, isInitialized:true }` (anonymous = always ready) |
| `get-owned-games` | — | Full TACT product catalog `[{ appId:<code>, name:<displayName> }]` |
| `get-selected-apps` | — | Product codes from `selectedAppsToPrefill.json` |
| `set-selected-apps` | `appIds` (JSON array string) | Persists the selection |
| `get-selected-apps-status` | — | Selected apps + prefill status |
| `check-cache-status` | `appIds` (JSON array string) | Per-product up-to-date status |
| `prefill` | `all`, `force`, `products` (JSON array string) | Starts a prefill; progress via events |
| `cancel-prefill` | — | Cancels an in-flight prefill |
| `clear-cache` | — | Deletes the cache dir |
| `get-cache-info` | — | Cache size / file count |
| `shutdown` | — | Cleans up |

Progress events use a `state` machine: `downloading` → `app_completed` / `already_cached` → `completed` (or `error`).

## Supported products

The TACT catalog the upstream tool knows how to prefill — Blizzard (Diablo, Hearthstone, Overwatch, StarCraft,
Warcraft, WoW, …), Activision (Call of Duty titles, Crash Bandicoot 4), and Microsoft (Avowed, Sea of Thieves).
See [`BattleNetPrefill/TactProduct.cs`](BattleNetPrefill/TactProduct.cs) for the full list.

## Building

```bash
git clone --recurse-submodules https://github.com/regix1/battlenet-prefill-daemon
cd battlenet-prefill-daemon
dotnet build BattleNetPrefill.sln
```

The `LancachePrefill.Common` submodule (`regix1/lancache-prefill-common`) provides the shared LanCache resolver,
`TempDirUtils`, the bundled `CliFx.dll`, and progress helpers.

## Architecture

A daemon = a fork of the upstream console tool + a small `BattleNetPrefill/Api/` layer:

- `Program.cs` — daemon entrypoint (env parsing, UDS vs TCP, runs `DaemonMode`). No interactive prompts.
- `Api/SocketServer.cs` — length-prefixed JSON socket server + HMAC handshake.
- `Api/SocketCommandInterface.cs` — command dispatcher + socket progress emitter.
- `Api/BattleNetPrefillApi.cs` — wraps the upstream `TactProductHandler` / `CdnRequestManager` in-process.
- `Api/ApiConsoleAdapter.cs` — routes the upstream Spectre `IAnsiConsole` output to structured progress.
- `Handlers/`, `Web/`, `Parsers/`, `Structs/`, `Extensions/`, `EncryptDecrypt/`, `Utils/` — upstream tool code.

## License

MIT — see [LICENSE](LICENSE). Copyright (c) 2017 Martin Benjamins, (c) 2022 Tim Pilius.
