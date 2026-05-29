# Architecture

## Data Flow

```text
FanControl sensors
        |
        v
C:\CodexMonitor\CodexBridge\bin\Release\net10.0\CodexBridge.exe
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
net10.0
```

Main file:

```text
C:\CodexMonitor\CodexBridge\Program.cs
```

Responsibilities:

- connect to FanControl IPC;
- query all FanControl sensors;
- pick CPU/GPU/board fan sensors;
- query NVIDIA fallback data with `nvidia-smi.exe`;
- compute Ethernet/Wi-Fi/Wi-Fi AP rates;
- write `C:\CodexMonitor\@Resources\temps.txt` once per second;
- read bridge output path, mutex, and update interval from JSON config;
- preserve last values when possible during fallback.

Modes:

- normal mode: runs forever;
- `--once`: writes once and exits;
- `--dump`: prints available FanControl sensors and exits.

Single-instance guard:

```text
CodexMonitorFanControlBridge
```

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
- read current widget width from active skin;
- move widget to top-right of primary monitor with 24 px margin;
- write stable `WindowX`, `WindowY`, `AnchorX`, `AnchorY`, `AutoSelectScreen`, `SavePosition` values into Rainmeter.ini.

### Size Switcher

File:

```text
C:\CodexMonitor\Deploy\Switch-WidgetSize.ps1
```

Modes:

- `Auto`: primary monitor height >= 1440 uses 4K, otherwise 1080p;
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
- FanControl;
- .NET 10 Desktop Runtime.

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
3. Copy changed files into the reinstall kit payload.
4. Rebuild the reinstall zip.
5. Update docs/changelog.

## Critical Rainmeter Detail

Dynamic `IfTrueAction` lines can overwrite meter options during runtime. If a shape looks correct in the `[HealthBg]` section but wrong on screen after a sensor update, inspect `IfTrueAction` entries that call:

```ini
[!SetOption HealthBg Shape "..."]
```

This caused the 4K header bug when old 1080p coordinates were hard-coded in status actions.
