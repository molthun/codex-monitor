# Development Guide

## Golden Rule

Do not edit only one copy of a file and assume the project is updated. This project has live files, presets, and reinstall payload files.

## Before Changes

1. Check current runtime:

```powershell
Get-ScheduledTask -TaskName "CodexMonitor Bridge Elevated"
Get-Process Rainmeter -ErrorAction SilentlyContinue
Get-Content "C:\CodexMonitor\@Resources\temps.txt"
```

2. Backup:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\CodexMonitor\Deploy\Backup-CodexMonitor.ps1"
```

3. Read:

```text
README.md
docs\PROJECT_CONTEXT.md
docs\ARCHITECTURE.md
docs\SENSOR_CONTRACT.md
CHANGELOG.md
```

## Configuration

Local runtime settings live in:

```text
config.json
```

`config.json` is ignored by Git. Edit it directly on your workstation.

Public defaults live in:

```text
config.example.json
```

Scripts read `config.json`. If it is missing, they fall back to `config.example.json`; the installer creates `config.json` from that template on first setup.

Current config consumers:

- `Deploy\Install-CodexMonitor.ps1`
- `Deploy\Switch-WidgetSize.ps1`
- `Watch-PrimaryDisplay.ps1`
- `CodexBridge.exe`

When adding a new setting, update `config.example.json`, this guide, and the relevant script/code.

## Editing Rainmeter

Important files:

```text
<RainmeterSkinPath>\CodexMonitor\CodexMonitor.ini
C:\CodexMonitor\CodexMonitor.ini
C:\CodexMonitor\Presets\CodexMonitor.1080p.ini
C:\CodexMonitor\Presets\CodexMonitor.4K.ini
```

If editing visual layout, usually edit the appropriate preset first, then apply it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\CodexMonitor\Deploy\Switch-WidgetSize.ps1" -Mode 4K
```

or:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\CodexMonitor\Deploy\Switch-WidgetSize.ps1" -Mode 1080p
```

Refresh:

```powershell
& "C:\Program Files\Rainmeter\Rainmeter.exe" !Refresh "CodexMonitor"
```

## Editing Bridge

Source:

```text
C:\CodexMonitor\CodexBridge\Program.cs
```

Build:

```powershell
dotnet build "C:\CodexMonitor\CodexBridge\CodexBridge.csproj" -c Release
```

The installer also builds the bridge automatically if `CodexBridge.exe` is missing.

Restart bridge:

```powershell
Stop-ScheduledTask -TaskName "CodexMonitor Bridge Elevated"
Start-ScheduledTask -TaskName "CodexMonitor Bridge Elevated"
```

Verify:

```powershell
Get-Content "C:\CodexMonitor\@Resources\temps.txt"
```

## Updating Reinstall Kit

If maintaining a separate local restore folder, copy the changed files to that payload:

```powershell
Copy-Item "C:\CodexMonitor\CodexBridge\Program.cs" "<LocalStagingFolder>\Deploy\Payload\CodexBridge\Program.cs" -Force
Copy-Item "C:\CodexMonitor\CodexBridge\CodexBridge.csproj" "<LocalStagingFolder>\Deploy\Payload\CodexBridge\CodexBridge.csproj" -Force
Copy-Item "C:\CodexMonitor\Presets\CodexMonitor.1080p.ini" "<LocalStagingFolder>\Deploy\Payload\RainmeterSkin\CodexMonitor\CodexMonitor.1080p.ini" -Force
Copy-Item "C:\CodexMonitor\Presets\CodexMonitor.4K.ini" "<LocalStagingFolder>\Deploy\Payload\RainmeterSkin\CodexMonitor\CodexMonitor.4K.ini" -Force
```

Rebuild zip:

```powershell
$root = "<LocalStagingFolder>"
$zip = Join-Path $root "CodexMonitor-Release.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$items = Get-ChildItem -LiteralPath $root -Force | Where-Object { $_.FullName -ne $zip }
Compress-Archive -Path $items.FullName -DestinationPath $zip -Force
```

After GitHub is live, local zip artifacts are optional. The normal developer flow should be:

```powershell
cd C:\CodexMonitor
git status --short
git add README.md CHANGELOG.md docs CodexBridge Deploy Presets Watch-PrimaryDisplay.ps1 CodexMonitor.ini
git commit -m "Describe the change"
git push
```

Only build a zip/release artifact when restoring a fresh Windows install or publishing a release.

## Verification Checklist

- `temps.txt` updates once per second.
- CPU temp and fan show real values.
- GPU fan shows `idle / 0%` at idle and RPM/% under GPU load.
- Network graph moves under traffic.
- Ethernet/Wi-Fi/AP detail line is understandable.
- Disk I/O displays per C:, D:, E:.
- 1080p profile fits on a full HD desktop.
- 4K profile is readable and does not overflow screen.
- Widget is top-right on the primary display.
- Widget is not clickable in normal mode.
- Reinstall kit zip was rebuilt.
- Or, after GitHub is live, changes were committed/pushed and release artifact was rebuilt only if needed.
- `CHANGELOG.md` was updated.

## Things That Break Easily

- Hard-coded 1080p coordinates inside dynamic Rainmeter `IfTrueAction` lines.
- Editing active skin but not presets.
- Editing presets but not applying them.
- Editing the project but not updating any maintained local staging copy.
- Changing `SkinPath` away from the normal Rainmeter path.
- Running several bridge instances.
- Killing the bridge console manually if it was not started through scheduled task.

## Collaboration Rules

- One developer changes bridge/sensors at a time.
- One developer changes Rainmeter layout at a time.
- Every meaningful change gets a short entry in `CHANGELOG.md`.
- Every sensor key change updates `docs\SENSOR_CONTRACT.md`.
- Every deployment/install change updates public docs and any local restore docs if such a folder is being maintained.
- Do not remove old backups until the current state has been tested on both 1080p and 4K.
