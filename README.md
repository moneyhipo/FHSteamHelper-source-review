# FHSteamHelper source (Nexus review)

This repository contains the source and build metadata for `FHSteamHelper.exe`, the Steam lobby/invitation and co-op network relay helper for **FHT2 Together Co-op and ATB**.

The helper is optional. It adds Steam friend invites (F7) and joining through Steam, so nobody has to open a port. Without it the mod still works and friends join by IP.

## Download and install

Get `FH2_Coop_SteamHelper_0.5.0.zip` from the [Releases](https://github.com/moneyhipo/FHSteamHelper-source-review/releases) page (the main mod file on Nexus has no executable).

1. Extract the zip into the game folder, next to `Game.exe`. Extracting it into `www/` or into a folder of its own inside the game folder works too.
2. Every player who wants to invite or join through Steam needs it, the host and the friends.
3. Keep Steam running, start the game, and the host presses F7 to invite friends.

Helper 0.5.0 works with every mod version from 0.5.0 on (0.5.12 at the time of writing). The helper only gets a new version when its own code changes; the mod tells you when it needs a newer one.

The helper is not code-signed, so Windows SmartScreen or an antivirus may warn about it. You can check the file against the hash below and build it yourself from this source.

## Match to the released file

The `FHSteamHelper.exe` in `FH2_Coop_SteamHelper_0.5.0.zip` (helper 0.5.0) has SHA-256:

`13744bd4529d1d6fe76470c5d19430a2eaec5de96af0717416eb558fe4b33237`

The zip itself has SHA-256 `c6e2e9a21caa63542aa34880e69f682d1482865d5d0f8e89efa523248428a4a5`.

VirusTotal lookup by hash: https://www.virustotal.com/gui/file/13744bd4529d1d6fe76470c5d19430a2eaec5de96af0717416eb558fe4b33237

## What it does

The helper uses Steamworks.NET to initialize Steam, create and join friends-only lobbies, send invites, and relay co-op game traffic through Steam Networking Messages. It talks to the game over local TCP sockets on the player's own machine and forwards the game traffic. Its only file write is `steam_appid.txt` next to itself, when that file is missing; it does not touch the game's files or saves.

## Build

Requirements: .NET 10 SDK, Visual Studio Build Tools 2022 with the "Desktop development with C++" workload (Native AOT uses its linker), and internet access for restoring the Steamworks.NET NuGet package. `vswhere.exe` must be on PATH (it is in `C:\Program Files (x86)\Microsoft Visual Studio\Installer`).

From this directory, run:

```powershell
dotnet publish .\FHSteamHelper.csproj -c Release -r win-x64
```

The project is configured for a Native AOT Windows x64 publish (`PublishAot`). Native AOT output is not always byte-identical between machines. `steam_api64.dll` is copied next to the output executable. `steam_appid.txt` contains the app ID used by the distributed helper.

## Release zips (for the mod's author)

A mod release refers to the released helper and does not rebuild it (a rebuilt zip gets a new hash, and antivirus warnings start over). From the main mod repository root:

```powershell
node .\tools\make_release.js <mod version> --helper 0.5.0
```

This writes `dist/FH2_Coop_<mod version>.zip`, whose README points to `FH2_Coop_SteamHelper_0.5.0.zip`.

Only when the helper's code changes: set the new `Version` in `FHSteamHelper.csproj`, build it, place the published `FHSteamHelper.exe` in the mod's `mod/fh_steam_helper/` folder alongside `steam_api64.dll` and `steam_appid.txt`, and run `node .\tools\make_release.js <helper version>` (without `--helper`). It also writes `dist/FH2_Coop_SteamHelper_<helper version>.zip` for a new GitHub release.

## Older helpers

| Helper | SHA-256 of `FHSteamHelper.exe` | Change |
|---|---|---|
| 0.5.0 | `13744bd4529d1d6fe76470c5d19430a2eaec5de96af0717416eb558fe4b33237` | Version number only (`Version` in `FHSteamHelper.csproj`); `Program.cs` unchanged from 0.42 |
| 0.42.0 | `9649d6ace2938f006ca5b8cc03874cd6d5704c9683bf9cf799f77908c9a76a40` | Sends a protocol version (`"ver":1`) with the Steam name, so the mod can ask for a newer helper; first separate download |
| 0.41 (in the Nexus 0.41 ZIP) | `b20fa0f8f971fad8dcd754e6e0fa0bb3c3c3b7eb0be791b66fdf62ad399238ec` | Native AOT build (a plain native executable) |
| 0.40 | `34a524de61fa159843749b349bb0674ea22d4254e9e17cafa7941edcd3b247c7` | Compressed single-file .NET bundle |

From 0.40 to 0.41 the source is the same apart from `KeepCallbackTypes` in `Program.cs`, which only names the Steamworks.NET callback structures so the AOT compiler keeps their marshalling code.

VirusTotal (0.41 helper): https://www.virustotal.com/gui/file/b20fa0f8f971fad8dcd754e6e0fa0bb3c3c3b7eb0be791b66fdf62ad399238ec

## Third-party components

- Steamworks.NET 2024.8.0, MIT License: https://github.com/rlabrecque/Steamworks.NET
- `steam_api64.dll` is Valve's Steamworks API redistributable, included under the Steamworks SDK Access Agreement. Steam and the Steam logo are trademarks of Valve Corporation.

The complete Steamworks.NET license text is in `THIRD_PARTY_LICENSES.txt`.
