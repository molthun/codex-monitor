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

The wizard currently covers:

- widget profile mode: Auto, 1080p, or 4K;
- displayed disk rows, up to three local drives;
- network adapter ignore terms;
- background update toggle;
- bridge telemetry update interval.

### Rainmeter Skin

Active file:

```text
<RainmeterSkinPath>\CodexMonitor\CodexMonitor.ini
```

Working copy:

```text
C:\CodexMonitor\CodexMonitor.ini
```

Presets:

```text
C:\CodexMonitor\Presets\CodexMonitor.1080p.ini
C:\CodexMonitor\Presets\CodexMonitor.4K.ini
```

The skin reads `temps.txt` using WebParser measures and combines those values with Rainmeter native measures:

- CPU usage;
- RAM;
- GPU usage via UsageMonitor;
- network totals;
- disk space and disk I/O.

### Display Watcher

File:

```text
C:\CodexMonitor\Watch-PrimaryDisplay.ps1
```

Responsibilities:

- poll primary monitor every 5 seconds;
- detect the primary monitor's *true physical* resolution via `GetDeviceCaps(DESKTOPHORZRES/DESKTOPVERTRES)` rather than `[Screen]::PrimaryScreen.Bounds`. The watcher is a long-lived System-DPI-aware `powershell.exe`, so `Bounds` is virtualized against the DPI context captured at process start and reports stale dimensions after the display/scaling changes (e.g. a 4K@100% screen looks like 1920x1080 when the watcher started in an RDP/FullHD session). `GetDeviceCaps` is immune to this virtualization;
- switch automatically between 1080p and 4K profiles when the primary monitor height crosses the configured threshold;
- read current widget width from active skin;
- move widget to top-right of primary monitor with 24 px margin;
- write stable `WindowX`, `WindowY`, `AnchorX`, `AnchorY`, `AutoSelectScreen`, `SavePosition` values into Rainmeter.ini.

### Size Switcher

File:

```text
C:\CodexMonitor\Deploy\Switch-WidgetSize.ps1
```

Modes:

- `Auto`: primary monitor height >= 1600 uses 4K, otherwise 1080p, so 2560x1440 (2K) gets the compact profile and only true 4K-height screens get the large one (height is the true physical resolution from `GetDeviceCaps`, not the DPI-virtualized `Bounds`);
- `1080p`: force compact profile;
- `4K`: force larger profile.

The switcher copies the chosen preset into the active Rainmeter skin path, then refreshes and moves Rainmeter. It does not rewrite the repository's root `CodexMonitor.ini`, so switching runtime size does not dirty the Git checkout.

### Installer

File:

```text
C:\CodexMonitor\Deploy\Install-CodexMonitor.ps1
```

Responsibilities:

- elevate if needed;
- copy bridge, skin, presets, resources, watcher;
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
- presets under `C:\CodexMonitor\Presets`;
- optional local reinstall payload under `<LocalStagingFolder>\Deploy\Payload`.

When changing the widget:

1. Edit the intended source/preset.
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
