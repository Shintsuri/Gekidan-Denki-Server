# Gekidan-Denki-Server

[中文](README.md) | [日本語](README.ja.md) | [English](README.en.md)

An unofficial local server for *World Dai Star: Yume no Stellarium* (Yumesute), built with C#, ASP.NET Core and PostgreSQL. It implements the client-facing APIs, realtime communication and player-data persistence.

> [!WARNING]
> - This is an unofficial project for learning, protocol research and personal testing. It is not affiliated with the game's developer or publisher, and it does not provide any official service.
> - The project is still in development. Implemented endpoints do not mean the full game works; some rankings and APIs still return placeholder data.

## Contents

- [Quick start](#quick-start)
- [Feature status](#feature-status)
- [Known issues and plans](#known-issues-and-plans)
- [Tech stack and layout](#tech-stack-and-layout)
- [For developers](#for-developers)
- [Related projects](#related-projects)
- [License and disclaimer](#license-and-disclaimer)
- [Acknowledgements](#acknowledgements)

## Quick start

### 1. Requirements

- .NET 10 SDK
- PostgreSQL 16 (database name defaults to `local_game`)
- MasterData and local assets matching your client version, and that you are allowed to use
- A test client or emulator that can reach this server

### 2. Prepare data

- Unpack the client's `mastermemory.db` and put the exported JSON tables under `masterdata-export/tables/` in the server root.
- [`SiriusLocalServer.csproj`](SiriusLocalServer.csproj) embeds about 60 of those tables as `<EmbeddedResource>`; without them the project **will not build**. Cloning the source alone does not guarantee a working build.
- Prepare the game asset directories (Resources and Addressables) and point to them in `.env` in the next step.

### 3. Configuration

```powershell
Copy-Item .env.example .env
```

Fill in `.env` following the comments. At minimum:

| Variable | Description |
| --- | --- |
| `DATABASE_URL` | PostgreSQL connection string |
| `MASTER_DATA_FILE` | Path to `mastermemory.db` |
| `RESOURCES_ROOT` / `ADDRESSABLES_ROOT` | Client asset directories |
| `APPLICATION_VERSION` / `ASSET_VERSION` | Must match the client and master data |
| `WDS_SKIP_MASTER_OVERLAY` | **Must be `1`**; otherwise the client rejects the master-data patch during initialization |

### 4. Build and run

```powershell
dotnet build -c Release
cd bin\Release\net10.0
$env:REPLAY_MODE='0'
$env:WDS_SKIP_MASTER_OVERLAY='1'
$env:WDS_EXTRA_UNION_KEYS='1'
.\SiriusLocalServer.exe
```

After startup, `http://127.0.0.1:8099/health` should return 200.

### 5. Connect a client

The client talks to the server over HTTPS 443 using a self-signed certificate (`server.pfx`). Either trust that certificate on the test client or use a test build that allows self-signed certificates. If the connection fails, check the certificate and `PUBLIC_BASE_URL` first.

## Feature status

Accounts are covered end to end: login, registration, account transfer and account recovery, with persistent player data, home screen, tutorial and profile.

Live performances and lessons settle completely: score validation, rating, drops, experience, star-rank experience, daily limits and stamina consumption/recovery all work, and the settlement hang has been fixed.

Progression works for character levels, blooming, awakening, star rank, skill levels and card upgrades, using the official master tables for data and costs. On the company side, company levels and their bonuses are read and calculated (the circle-support part).

Events implement the official PT formula, cumulative PT rewards (granted as soon as a tier is reached) and ranking rewards at the end of the settlement period. Star-rank rewards, missions, the mission pass and login bonuses can all be claimed normally.

Gacha supports single pulls, ten-pulls, pity and history for both regular and event pools. Exchange covers the exchange shop, music-score exchange and purchase-limit checks; a few endpoints such as character-piece exchange are still missing.

Photos and the photo album support generation, album arrangement, level-up, selling, ability changes and theater-story watch records.

Theater League and Triple Cast participation rewards and ranking settlement work, but the client-side rankings are still fixed data. Friends and circles are mostly usable, with a few edge operations still stubbed.

Realtime communication (gRPC) connects properly, and the basic flows for co-op lives and friend invites work.

## Known issues and plans

- Theater League / Triple Cast client-side rankings are still fixed data: the rewards are real, the rankings are not;
- Some endpoints (character-piece exchange, gacha re-roll, etc.) have never been requested by the client, so there is no real payload to work from and they stay as empty implementations for now;
- Master data and game assets are not included in this repository and must be exported separately;
- `WDS_SKIP_MASTER_OVERLAY=1` is mandatory; paid-pack features that depend on the overlay are unavailable;
- The server only recalculates scores when effects are deterministic; with dynamic effects it keeps the client's values and validates an upper bound.

## Tech stack and layout

- C# / .NET 10, ASP.NET Core, gRPC, PostgreSQL, MessagePack, MasterMemory
- Ports: `8099` HTTP API, `443` HTTP/1.1 + HTTP/2, `8788` gRPC realtime

```
Endpoints/     HTTP routing and protocol handling
Services/      Accounts, player data, circles, realtime
Game/          Rules and settlement (live, lesson, events, rewards)
Storage/       Database schema and migrations
Protocol/      MessagePack, LZ4, realtime protocol
Assets/        Master-data loading and asset resolution
docs/          Progress and reverse-engineering notes
```

## For developers

- [Project progress](docs/PROGRESS.md): fixed issues and TODOs per module
- [Reverse-engineering notes](docs/REVERSE.md): formula derivations, field order, master-data semantics, error-code mapping
- [Changelog](CHANGELOG.md): fixes and changes

## Related projects

- [yumesute-server](https://github.com/Sakurashiyo/yumesute-server): the upstream reference implementation this project builds on

## License and disclaimer

This project is a derivative of [yumesute-server](https://github.com/Sakurashiyo/yumesute-server). Both the upstream project and this project are released under the **PolyForm Noncommercial License 1.0.0**:

- Personal use, modification and non-commercial distribution are allowed;
- Commercial use is prohibited;
- When distributing, keep `LICENSE` and the copyright notices, and state your changes.

This repository contains no game assets or master data. The game and related assets are copyright ©Sirius/Project WDS and KMS.inc. See [LICENSE](LICENSE) and [NOTICE](NOTICE.md) for details.

## Acknowledgements

- The authors and contributors of [yumesute-server](https://github.com/Sakurashiyo/yumesute-server)
- [yumesute-calc](https://github.com/esterTion/yumesute-calc) for numerical references
- Everyone who files issues or helps with testing
