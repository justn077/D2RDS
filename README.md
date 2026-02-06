# D2RDS (D2R Demon Slayers)

WPF launcher that:
1) runs an optional pre-launch script
2) launches a selected D2R account with the right arguments

## Run (dev)
- `cd C:\Users\justn\src\D2RDS`
- `dotnet run --project .\MultiboxLauncher\MultiboxLauncher.csproj`

## Build & publish
Build (Release):
- `dotnet build .\D2RDS.sln -c Release`

Publish (framework-dependent, single file):
- `dotnet publish .\MultiboxLauncher\MultiboxLauncher.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\D2RDS`

Publish (self-contained, single file):
- `dotnet publish .\MultiboxLauncher\MultiboxLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\D2RDS-selfcontained`

Output EXE paths:
- `dist\D2RDS\D2RDS.exe`
- `dist\D2RDS-selfcontained\D2RDS.exe`

## GitHub release (binary)
Example (self-contained build):
1) `dotnet publish .\MultiboxLauncher\MultiboxLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\D2RDS-selfcontained`
2) `Compress-Archive -Path .\dist\D2RDS-selfcontained\* -DestinationPath .\dist\D2RDS-selfcontained.zip -Force`
3) `gh release create v0.1.0 .\dist\D2RDS-selfcontained.zip --title "D2RDS v0.1.0" --notes "Initial release"`

## First-time setup (in the app)
1) Pick your **Region** from the dropdown (prompted on first run).
2) Set your **Install path** (folder picker). Default is `C:\Program Files (x86)\Diablo II Resurrected`.
3) Click **Add Account**, enter email + password, and optionally a nickname.
4) A new **Launch** button appears and persists between runs.
5) Use **Edit** to change email/nickname. Use **Change password** to update credentials. **Delete** removes an account.
6) Use **Up/Down** to reorder accounts. Use **Lock order** to prevent changes.
7) Toggle **Broadcast** for keyboard/mouse mirroring. Use hotkeys to toggle on/off and switch All vs Selected.
8) Use **Classic** per account if that window is running Classic mode so mouse broadcasts scale to a 4:3 viewport.

Up to **7 accounts** are supported.

## First launch + driver window
- Launch one D2R account normally (via Battle.net) at least once.
- Put that account first in your D2RDS list (driver).
- Use D2RDS to launch the other accounts you want to multibox.
- Selected broadcast will automatically include the driver window (title: "Diablo II: Resurrected").

## Credentials & storage
- Passwords are stored in **Windows Credential Manager**.
- `config.json` stores only non-sensitive metadata and credential keys.

## Configure (advanced)
Edit `MultiboxLauncher\config.json` if you want to tweak:
- `preLaunch.path` (script to run before each launch)
- `installPath`
- `region`

Tokens supported in paths:
- `%DESKTOP%` -> your Windows Desktop folder
- `%APPDIR%` -> the app folder

Example `config.json`:
```
{
  "preLaunch": {
    "enabled": true,
    "path": "%APPDIR%\\assets\\D2RKilla.ps1"
  },
  "installPath": "C:\\Program Files (x86)\\Diablo II Resurrected",
  "region": "Americas",
  "lockOrder": false,
  "broadcast": {
    "enabled": false,
    "broadcastAll": true,
    "keyboard": true,
    "mouse": true,
    "toggleBroadcastHotkey": "Ctrl+Alt+B",
    "toggleModeHotkey": "Ctrl+Alt+M",
    "defaultsApplied": true
  },
  "minimizeToTaskbar": false,
  "accounts": [
    {
      "id": "a1b2c3",
      "nickname": "CTA",
      "email": "user@example.com",
      "credentialId": "D2RDS:a1b2c3",
      "broadcastEnabled": true,
      "classicMode": false
    }
  ]
}
```

## Pre-launch script
The default script is copied into `MultiboxLauncher\assets\D2RKilla.ps1`.
The script requires Sysinternals Handle (`handle64.exe`), which cannot be redistributed.
Download Handle from Microsoft Sysinternals and place `handle64.exe` next to `D2RDS.exe` (same folder).

## Logging
Writes `logs\launcher.log` next to the built exe.

## Broadcasting
- Global toggle hotkey: `Ctrl+Alt+B` (default)
- Mode hotkey (All vs Selected): `Ctrl+Alt+M` (default)
- If **All** is enabled, all running D2R windows receive input.
- If **All** is disabled, only accounts with **Bcast** checked receive input.


