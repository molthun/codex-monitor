# CodexMonitor reinstall kit

This folder contains everything needed to restore the Rainmeter desktop monitor after a Windows reinstall.

## What is included

- `CodexBridge.exe --build-skin` generates the active Rainmeter skin from `config.json`.
- `Payload\CodexBridge\` - bridge source; the installer obtains the matching self-contained `CodexBridge.exe` from GitHub Releases.
- `Payload\@Resources\temps.example.txt` - placeholder sensor output file.
- `Payload\RainmeterLayout-CodexMonitor.ini` - current Rainmeter placement and desktop behavior.
- `Install-CodexMonitor.ps1` - install/restore script.
- `Configure-CodexMonitor.ps1` - launches the graphical settings wizard through `CodexBridge.exe --settings`.
- `Backup-CodexMonitor.ps1` - creates a timestamped backup zip including `config.json`, the version and the active skin.
- `Uninstall-CodexMonitor.ps1` - removes the scheduled task and optionally files.

## Requirements after reinstall

Install these first:

1. Rainmeter 4.5.x or newer.
2. NVIDIA driver with `nvidia-smi.exe` if NVIDIA GPU fallback data is desired.
3. Fan behavior is managed outside CodexMonitor by the user's BIOS/UEFI, drivers, or existing system tools.

The bridge reads CPU/GPU/fan values directly through LibreHardwareMonitor. CodexMonitor displays current state only and does not control fan behavior.

## Restore

Open PowerShell and run:

```powershell
powershell -ExecutionPolicy Bypass -File C:\CodexMonitor\Deploy\Install-CodexMonitor.ps1
```

The installer will:

- copy the bridge to `C:\CodexMonitor\CodexBridge`;
- copy the skin to the current Rainmeter `SkinPath`;
- create/update `config.json` through the graphical settings wizard;
- create/update the elevated scheduled task `CodexMonitor Bridge Elevated`;
- restore the `[CodexMonitor]` Rainmeter desktop settings;
- start the bridge and refresh/activate the Rainmeter skin.

## Backup current state

```powershell
powershell -ExecutionPolicy Bypass -File C:\CodexMonitor\Deploy\Backup-CodexMonitor.ps1
```

Backups are written to `C:\CodexMonitor\Backups`.

## Uninstall

Only remove the scheduled task and deactivate the skin:

```powershell
powershell -ExecutionPolicy Bypass -File C:\CodexMonitor\Deploy\Uninstall-CodexMonitor.ps1
```

Remove files too:

```powershell
powershell -ExecutionPolicy Bypass -File C:\CodexMonitor\Deploy\Uninstall-CodexMonitor.ps1 -RemoveFiles
```

## Notes

- The bridge writes sensor/network values to the active skin’s `@Resources\temps.txt`; the installer records that path in `config.json`.
- Rainmeter reads that file once per second.
- The bridge scheduled task runs with highest privileges because low-level hardware sensors often require elevated access.
- Set widget margins in Settings. The display watcher keeps it positioned against the primary monitor.
