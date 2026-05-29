# CodexMonitor reinstall kit

This folder contains everything needed to restore the Rainmeter desktop monitor after a Windows reinstall.

## What is included

- `Payload\RainmeterSkin\CodexMonitor\CodexMonitor.ini` - active Rainmeter skin.
- `Payload\CodexBridge\` - .NET bridge source and current Release build.
- `Payload\@Resources\temps.example.txt` - placeholder sensor output file.
- `Payload\RainmeterLayout-CodexMonitor.ini` - current Rainmeter placement and desktop behavior.
- `Install-CodexMonitor.ps1` - install/restore script.
- `Backup-CodexMonitor.ps1` - creates a timestamped backup zip.
- `Uninstall-CodexMonitor.ps1` - removes the scheduled task and optionally files.

## Requirements after reinstall

Install these first:

1. Rainmeter 4.5.x or newer.
2. FanControl v268 or newer.
3. NVIDIA driver with `nvidia-smi.exe`.
4. .NET 10 Desktop Runtime if the bridge does not start. FanControl v268 usually already implies a .NET 10-capable setup.

FanControl must be running and able to see the sensors before the bridge can publish CPU/GPU/fan values.

## Restore

Open PowerShell and run:

```powershell
powershell -ExecutionPolicy Bypass -File C:\CodexMonitor\Deploy\Install-CodexMonitor.ps1
```

The installer will:

- copy the bridge to `C:\CodexMonitor\CodexBridge`;
- copy the skin to the current Rainmeter `SkinPath`;
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

- The bridge writes sensor/network values to `C:\CodexMonitor\@Resources\temps.txt`.
- Rainmeter reads that file once per second.
- The bridge scheduled task runs with highest privileges because FanControl often runs elevated.
- If the widget appears in the wrong place after reinstall, adjust it once in Rainmeter, then run `Backup-CodexMonitor.ps1` or refresh this deploy kit.
