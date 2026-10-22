# FHSteamHelper source (Nexus review)

This repository contains the source and build metadata for `FHSteamHelper.exe`, the Steam lobby/invitation and co-op network relay helper distributed with **FHT2 Together Co-op and ATB**.

## Match to the reviewed file

The `FHSteamHelper.exe` included in the Nexus 0.40 ZIP has SHA-256:

`34a524de61fa159843749b349bb0674ea22d4254e9e17cafa7941edcd3b247c7`

The source for this helper is `Program.cs` in the main mod repository at commit `58dbad2` (2026-10-17). The helper binary and source were checked against the local release files before preparing this review package.

The helper uses Steamworks.NET to initialize Steam, create/join friends-only lobbies, send invites, and relay co-op game traffic through Steam Networking Messages. It also uses local TCP sockets to communicate with the game and forward game traffic on the player's machine.

## Build

Requirements: .NET 10 SDK and internet access for restoring the Steamworks.NET NuGet package.

From this directory, run:

```powershell
dotnet publish .\FHSteamHelper.csproj -c Release -r win-x64 --self-contained true
```

The project is configured for a self-contained, single-file, trimmed Windows x64 publish. `steam_api64.dll` is copied next to the output executable. `steam_appid.txt` contains the app ID used by the distributed helper.

To recreate the mod ZIP after building, place the published `FHSteamHelper.exe` in the mod's `mod/fh_steam_helper/` folder alongside `steam_api64.dll` and `steam_appid.txt`, then run this from the main mod repository root:

```powershell
node .\tools\make_release.js 0.40
```

## Third-party components

- Steamworks.NET 2024.8.0, MIT License: https://github.com/rlabrecque/Steamworks.NET
- `steam_api64.dll` is Valve's Steamworks API redistributable, included under the Steamworks SDK Access Agreement. Steam and the Steam logo are trademarks of Valve Corporation.

The complete Steamworks.NET license text is in `THIRD_PARTY_LICENSES.txt`.
