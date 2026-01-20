# D2RDS (D2R Demon Slayers)

Minimal WPF GUI that:
1) optionally runs a pre-launch step (script/shortcut)
2) launches the selected profile (shortcut/exe)

## Run (dev)
- `cd C:\Users\justn\src\D2RDS`
- `dotnet run --project .\MultiboxLauncher\MultiboxLauncher.csproj`

## Configure
- Edit `MultiboxLauncher\config.json`
- Tokens: `%DESKTOP%` expands to your Windows Desktop folder.

## Logging
- Writes `logs\launcher.log` next to the built exe.

