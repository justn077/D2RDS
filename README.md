# D2RDS (D2R Demon Slayers)

WPF launcher that:
1) runs an optional pre-launch script
2) launches a selected D2R account with the right arguments

## Run (dev)
- `cd C:\Users\justn\src\D2RDS`
- `dotnet run --project .\MultiboxLauncher\MultiboxLauncher.csproj`

## First-time setup (in the app)
1) Pick your **Region** from the dropdown (prompted on first run).
2) Set your **Install path** (folder picker). Default is `C:\Program Files (x86)\Diablo II Resurrected`.
3) Click **Add Account**, enter email + password, and optionally a nickname.
4) A new **Launch** button appears and persists between runs.
5) Use **Edit** to change email/nickname. Use **Change password** to update credentials. **Delete** removes an account.

Up to **7 accounts** are supported.

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
  "accounts": [
    {
      "id": "a1b2c3",
      "nickname": "CTA",
      "email": "user@example.com",
      "credentialId": "D2RDS:a1b2c3"
    }
  ]
}
```

## Pre-launch script
The default script is copied into `MultiboxLauncher\assets\D2RKilla.ps1`.
Update its `handle64.exe` path if you move it.

## Logging
Writes `logs\launcher.log` next to the built exe.

## Dev note
`CONTEXT.md` is for development only and should not be included in user-facing packages.

