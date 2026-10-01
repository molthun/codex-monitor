# Architecture

## Data Flow

```text
LibreHardwareMonitor sensors
        |
        v
C:\CodexMonitor\CodexBridge\CodexBridge.exe
        |
        v
C:\CodexMonitor\@Resources\temps.txt
        |
        v
Rainmeter WebParser measures
        |
        v
<RainmeterSkinPath>\CodexMonitor\CodexMonitor.ini
```

## Components

### Configuration

Local working config:

```text
config.json
```

Public template:

```text
config.example.json
```

Config consumers:

- installer;
- size switcher;
- display watcher;
- bridge.

The model is intentionally simple: runtime tools read one config file, `config.json`. If it does not exist yet, scripts can fall back to `config.example.json` for default values. The installer creates `config.json` from `config.example.json` during first setup.

### CodexBridge

Project:

```text
C:\CodexMonitor\CodexBridge
```

Target framework:

```text
net10.0-windows
```

User installs use the bundled self-contained Windows executable from:

```text
C:\CodexMonitor\Deploy\Payload\CodexBridge\CodexBridge.exe
```

Developers need the .NET SDK only when changing and rebuilding `CodexBridge`.

Main file:

```text
C:\CodexMonitor\CodexBridge\Program.cs
```

Responsibilities:

- initialize LibreHardwareMonitor hardware access;
- query CPU, GPU, motherboard/SuperIO, controller, and fan sensors directly;
- pick CPU/GPU/board fan sensors;
- query NVIDIA fallback data with `nvidia-smi.exe`;
- compute Ethernet/Wi-Fi/Wi-Fi AP rates;
- write `C:\CodexMonitor\@Resources\temps.txt` once per second;
- read bridge output path, mutex, and update interval from JSON config;
- preserve last values when possible during fallback.
- launch the graphical settings wizard when started with `--settings`.

Modes:

- normal mode: runs forever;
- `--once`: writes once and exits;
- `--dump`: prints available LibreHardwareMonitor sensors and exits.
- `--settings`: opens the WinForms settings wizard and exits when the window closes.

Single-instance guard:

```text
CodexMonitorHardwareBridge
```

### Settings Wizard

Launcher:

```text
C:\CodexMonitor\Deploy\Configure-CodexMonitor.ps1
```

Runtime entry point:

```text
C:\CodexMonitor\CodexBridge\CodexBridge.exe --settings --config C:\CodexMonitor\config.json
```

The PowerShell launcher prepares/updates the local `config.json`, locates the installed or payload bridge executable, and then starts the WinForms settings window. The wizard edits the local ignored config only; public defaults still belong in `config.example.json`.

The settings window (also from the tray icon) covers:

- Widget: size (Auto, 1080p, 2K, 4K or a custom percentage), fit to screen, visible sections, top-process rows, show/hide;
- Hardware: graphics card, fan list and extra temperatures (with live values from `inventory.json`), up to six drives with names;
- Network: adapter roles, ignore terms, Internet plan and LAN full scale;
- Updates: notify / install silently / off, Check now and Install, bridge refresh interval.

Saving writes `config.json`, restarts the bridge task and rebuilds the skin.

### Rainmeter Skin

Active file (generated, do not edit by hand):

```text
<RainmeterSkinPath>\CodexMonitor\CodexMonitor.ini
```

`CodexBridge.exe --build-skin --out <file> --screen-height <px>` writes it from `config.json`
(`SkinBuilder.cs`): sections, rows, sizes and the WebParser RegExp all come from the same settings
the bridge uses, and the temps.txt key order is defined once in `TempsFile.cs`. The skin records the
screen height it was built for in `[Metadata] ScreenHeight`.

The skin reads `temps.txt` using WebParser measures and combines those values with Rainmeter native measures:

- CPU usage;
- RAM;
- GPU usage via UsageMonitor;
- disk space and disk I/O.

### Tray Icon

`CodexBridge.exe --tray`, started at sign-in from the Startup folder (not elevated): show/hide the
widget, settings, check for updates, install the update the watcher found, restart the widget.

### Display Watcher

File:

```text
C:\CodexMonitor\Watch-PrimaryDisplay.ps1
```

Responsibilities:

- poll primary monitor every 5 seconds;
- detect the primary monitor's *true physical* resolution via `GetDeviceCaps(DESKTOPHORZRES/DESKTOPVERTRES)` rather than `[Screen]::PrimaryScreen.Bounds`. The watcher is a long-lived System-DPI-aware `powershell.exe`, so `Bounds` is virtualized against the DPI context captured at process start and reports stale dimensions after the display/scaling changes (e.g. a 4K@100% screen looks like 1920x1080 when the watcher started in an RDP/FullHD session). `GetDeviceCaps` is immune to this virtualization;
- rebuild the skin (through the size switcher) when the primary monitor height differs from the one the skin was built for;
- read current widget width from active skin;
- move widget to top-right of primary monitor with 24 px margin;
- write stable `WindowX`, `WindowY`, `AnchorX`, `AnchorY`, `AutoSelectScreen`, `SavePosition` values into Rainmeter.ini.

### Size Switcher

File:

```text
C:\CodexMonitor\Deploy\Switch-WidgetSize.ps1
```

It asks the bridge to generate the skin for the current physical screen height
(`CodexBridge.exe --build-skin`), then refreshes, moves and shows or hides the Rainmeter skin.
The `-Mode` parameter is kept for older callers; the size comes from `config.json`.

### Installer

File:

```text
C:\CodexMonitor\Deploy\Install-CodexMonitor.ps1
```

Responsibilities:

- elevate if needed;
- copy bridge, resources, watcher; generate the skin; create the watcher and tray Startup shortcuts;
- create scheduled task `CodexMonitor Bridge Elevated`;
- create watcher startup shortcut;
- set Rainmeter desktop-mode options;
- auto-select skin size;
- start bridge, watcher, and Rainmeter.

### Reinstall Bootstrapper

File:

```text
<LocalStagingFolder>\INSTALL_ALL_AND_RESTORE.cmd
```

Installs with `winget`:

- Rainmeter;

Then runs the restore script.

## Active Copies vs Source Copies

There are several copies by design:

- active Rainmeter skin under the user's Rainmeter `SkinPath`;
- working copy under `C:\CodexMonitor`;
- optional local reinstall payload under `<LocalStagingFolder>\Deploy\Payload`.

When changing the widget:

1. Edit the intended source (for the skin: `SkinBuilder.cs`).
2. Apply it to the active skin.
3. Keep root and payload copies synchronized.
4. Publish a new tagged release (`v*`) when bridge source or settings UI code changes; CI builds and attaches `CodexBridge.exe` to the release (the binary is not committed).
5. Update docs/changelog.

## Critical Rainmeter Detail

Dynamic `IfTrueAction` lines can overwrite meter options during runtime. If a shape looks correct in the `[HealthBg]` section but wrong on screen after a sensor update, inspect `IfTrueAction` entries that call:

```ini
[!SetOption HealthBg Shape "..."]
```

This caused the 4K header bug when old 1080p coordinates were hard-coded in status actions.
