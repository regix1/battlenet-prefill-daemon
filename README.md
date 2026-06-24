# BattleNetPrefill

[![License: MIT](https://img.shields.io/badge/License-MIT-blue?style=for-the-badge)](LICENSE)
[![Platform: Battle.net](https://img.shields.io/badge/Battle.net%20%2F%20Blizzard-148eff?style=for-the-badge&logo=battledotnet&logoColor=white)](https://www.blizzard.com/)
[![Discord](https://img.shields.io/badge/Discord-Join-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.com/invite/BKnBS4u)
[![LANCache Manager](https://img.shields.io/badge/LANCache-Manager-9af?style=for-the-badge)](https://github.com/regix1/lancache-manager)

Battle.net / Blizzard prefill daemon for [LANCache](https://lancache.net/) — a
companion to [**LANCache Manager**](https://github.com/regix1/lancache-manager),
which coordinates the prefill providers.

It downloads Battle.net game content through your lancache *before* you install,
so the real install — and every other machine on your LAN — pulls from the cache
at full LAN speed instead of the internet. Nothing is written to disk: bytes
stream through the cache and are discarded.

## Why use it

- **Cache warm before you install** — pre-download titles overnight, install instantly later.
- **LAN speed for every machine after the first** — the second install of the same version is served from cache.
- **No disk writes, no free space needed** — bytes stream through and are discarded, sparing your SSD.
- **No login, anonymous** — Blizzard's content is public, so there is nothing to sign in to.
- **Headless daemon** — driven by LANCache Manager or any socket client.

## Quick start

**Recommended — run it through [LANCache Manager](https://github.com/regix1/lancache-manager).**
LANCache Manager installs, configures, and drives this daemon alongside the other
prefill providers, so you never touch the socket protocol by hand. This is the
supported path for almost everyone.

**Standalone (.NET 8 SDK)** — build and run from source:

```bash
dotnet build BattleNetPrefill/BattleNetPrefill.csproj -c Release
dotnet run  --project BattleNetPrefill/BattleNetPrefill.csproj -c Release
```

No login required — content is public, so you can prefill straight away.

> A prebuilt container image is published at `ghcr.io/regix1/battlenet-prefill-daemon`
> for advanced/manual setups. It is a socket-driven daemon (volumes `/commands`,
> `/responses`, `/app/Config`, `/app/.cache`), so see
> [LANCache Manager](https://github.com/regix1/lancache-manager) and the repo docs
> for the full container configuration rather than running it ad hoc.

## How it works

1. **No login needed** — Blizzard's content is publicly fetchable; there is nothing to authenticate.
2. **Select titles** — choose from the Blizzard product catalog (Diablo, Hearthstone, Overwatch, StarCraft, Warcraft, WoW, Call of Duty, and more).
3. **Resolve** — each product's manifest and CDN URLs are looked up from Blizzard's content system.
4. **Prefill** — each product's content is fetched through the lancache and discarded, warming the cache.

## Requirements

- A running [LANCache](https://lancache.net/) with the **`blizzard`** cache-domain
  group enabled (from [uklans/cache-domains](https://github.com/uklans/cache-domains)).
- No account needed — content is public.
- Docker, or the [.NET 8 SDK](https://dotnet.microsoft.com/) to build from source.

## Support

Questions or issues? [Open an issue](https://github.com/regix1/battlenet-prefill-daemon/issues),
or find the LANCache community on the
[LanCache.NET Discord](https://discord.com/invite/BKnBS4u).

If these tools have been useful, support the original author on
[ko-fi](https://ko-fi.com/Y8Y5DWGZN), or this fork via
[buy me a coffee](https://www.buymeacoffee.com/regix). Thanks!

## License

Licensed under the MIT License (see [LICENSE](LICENSE)); a fork of the
lancache-prefill tools by Tim Pilius ([@tpill90](https://github.com/tpill90)).
