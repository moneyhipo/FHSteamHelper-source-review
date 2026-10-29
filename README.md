# FHSteamHelper source (Nexus review)

This repository contains the source and build metadata for `FHSteamHelper.exe`, the Steam lobby/invitation and co-op network relay helper distributed with **FHT2 Together Co-op and ATB**.

## Download

Since 0.42 the helper is a separate download (the main mod file on Nexus has no executable). Get `FH2_Coop_SteamHelper_<version>.zip` from the [Releases](https://github.com/moneyhipo/FHSteamHelper-source-review/releases) page and extract it into the game folder (next to `Game.exe`). The mod also finds it when the zip is extracted into `www/` or into a folder of its own inside the game folder. Without the helper the mod still works; friends join by IP.

## Match to the released file

The `FHSteamHelper.exe` in `FH2_Coop_SteamHelper_0.42.zip` (helper 0.42.0) has SHA-256:

`9649d6ace2938f006ca5b8cc03874cd6d5704c9683bf9cf799f77908c9a76a40`

The 0.42 helper differs from 0.41 only by sending a protocol version (`"ver":1`) with the Steam name, so the mod can ask for a newer helper when it needs one.

The `FHSteamHelper.exe` included in the Nexus 0.41 ZIP has SHA-256 `b20fa0f8f971fad8dcd754e6e0fa0bb3c3c3b7eb0be791b66fdf62ad399238ec`.

Since 0.41 the helper is a Native AOT build (a plain native executable). The 0.40 helper was a compressed single-file .NET bundle with SHA-256 `34a524de61fa159843749b349bb0674ea22d4254e9e17cafa7941edcd3b247c7`; the source code is the same apart from `KeepCallbackTypes` in `Program.cs`, which only names the Steamworks.NET callback structures so the AOT compiler keeps their marshalling code.

VirusTotal (0.41 helper): https://www.virustotal.com/gui/file/b20fa0f8f971fad8dcd754e6e0fa0bb3c3c3b7eb0be791b66fdf62ad399238ec

The helper uses Steamworks.NET to initialize Steam, create/join friends-only lobbies, send invites, and relay co-op game traffic through Steam Networking Messages. It also uses local TCP sockets to communicate with the game and forward game traffic on the player's machine.

## Build

Requirements: .NET 10 SDK, Visual Studio Build Tools 2022 with the "Desktop development with C++" workload (Native AOT uses its linker), and internet access for restoring the Steamworks.NET NuGet package. `vswhere.exe` must be on PATH (it is in `C:\Program Files (x86)\Microsoft Visual Studio\Installer`).

From this directory, run:

```powershell
dotnet publish .\FHSteamHelper.csproj -c Release -r win-x64
```

The project is configured for a Native AOT Windows x64 publish (`PublishAot`). Native AOT output is not always byte-identical between machines. `steam_api64.dll` is copied next to the output executable. `steam_appid.txt` contains the app ID used by the distributed helper.

To recreate the release ZIPs after building, place the published `FHSteamHelper.exe` in the mod's `mod/fh_steam_helper/` folder alongside `steam_api64.dll` and `steam_appid.txt`, then run this from the main mod repository root (it writes `dist/FH2_Coop_<version>.zip` and `dist/FH2_Coop_SteamHelper_<version>.zip`):

```powershell
node .\tools\make_release.js 0.42
```

## Third-party components

- Steamworks.NET 2024.8.0, MIT License: https://github.com/rlabrecque/Steamworks.NET
- `steam_api64.dll` is Valve's Steamworks API redistributable, included under the Steamworks SDK Access Agreement. Steam and the Steam logo are trademarks of Valve Corporation.

The complete Steamworks.NET license text is in `THIRD_PARTY_LICENSES.txt`.
