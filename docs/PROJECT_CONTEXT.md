# Project Context

This file is the public handoff document for future work. Keep it free of personal paths, usernames, logs, screenshots, and machine-private details.

Machine-specific notes belong in:

```text
docs\LOCAL_SETUP.md
```

That file is ignored by Git. Use `docs\LOCAL_SETUP.example.md` as its template.

Runtime/install settings belong in:

```text
config.json
```

Machine-specific config overrides belong in:

```text
config.local.json
```

`config.local.json` is ignored by Git.

## Goal

Build and maintain a Rainmeter widget on the Windows desktop. It should behave like part of the desktop:

- not above normal windows;
- not clickable;
- not draggable during normal use;
- automatically placed in the top-right corner of the primary display;
- readable at a glance on both 1080p and 4K displays.

The widget shows:

- CPU load;
- RAM usage;
- GPU load;
- CPU/GPU temperatures;
- CPU/case/PSU/GPU fans;
- Ethernet, Wi-Fi, and Wi-Fi AP network traffic;
- per-disk read/write activity;
- disk used/free space.

## Supported Environment

This project targets Windows + Rainmeter + FanControl:

- Windows 11 or newer is recommended.
- Rainmeter 4.5.x or newer.
- FanControl v268 or newer.
- .NET 10 Desktop Runtime.
- NVIDIA driver with `nvidia-smi.exe` if NVIDIA GPU fallback data is desired.

Hardware sensor availability depends on the motherboard, firmware, drivers, Windows security settings, and FanControl support. Keep exact local sensor mapping in `docs\LOCAL_SETUP.md`.

## Main Approach

FanControl is the hardware sensor source. A .NET bridge uses `FanControl.IPC.dll`:

- `IPCFactory.GetSensorClient()`
- `GetAllSensorsAsync(new GetAllSensorsRequest())`

The bridge writes a flat key/value file:

```text
C:\CodexMonitor\@Resources\temps.txt
```

Rainmeter reads that file using `WebParser` measures.

## Why This Architecture Exists

Direct sensor access can be unreliable on modern Windows systems, especially when Memory Integrity/HVCI or driver restrictions are involved. FanControl already solves most hardware access problems, so the project treats it as the sensor authority and keeps Rainmeter simple.

## Standard Paths

Default install root:

```text
C:\CodexMonitor
```

Working skin copy:

```text
C:\CodexMonitor\CodexMonitor.ini
```

Bridge source:

```text
C:\CodexMonitor\CodexBridge\Program.cs
```

Bridge executable after build:

```text
C:\CodexMonitor\CodexBridge\bin\Release\net10.0\CodexBridge.exe
```

Rainmeter active skin location depends on the user's Rainmeter `SkinPath`. The installer reads it from:

```text
%APPDATA%\Rainmeter\Rainmeter.ini
```

Then installs the skin under:

```text
<RainmeterSkinPath>\CodexMonitor\CodexMonitor.ini
```

## Runtime Services

Bridge scheduled task:

```text
CodexMonitor Bridge Elevated
```

Display watcher startup shortcut:

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\CodexMonitor Display Watcher.lnk
```

Watcher script:

```text
C:\CodexMonitor\Watch-PrimaryDisplay.ps1
```

## Normal Rainmeter Settings

The widget should normally use:

```ini
AlwaysOnTop=-2
Draggable=0
ClickThrough=1
SavePosition=0
AnchorX=0
AnchorY=0
AutoSelectScreen=0
```

Emergency visible/edit mode:

```ini
AlwaysOnTop=1
Draggable=1
ClickThrough=0
WindowX=100
WindowY=100
```

Return to desktop mode after debugging.

## Layout Profiles

1080p:

```text
W=430
H=950
BarW=394
BarH=7
HealthY=74
HealthH=46
HealthR=3
```

4K:

```text
W=720
H=1400
BarW=660
BarH=10
HealthY=109
HealthH=68
HealthR=5
```

Important: the health/header background must use variables:

```ini
Rectangle #Pad#,#HealthY#,#BarW#,#HealthH#,#HealthR#
```

Do not hard-code 1080p coordinates in dynamic status actions. That breaks the 4K profile.

## Fan Naming

Default user-facing labels:

- `CPU cooler`
- `Case fan`
- `PSU fan`
- `GPU fans`

Exact physical mapping is machine-specific. Keep it in `docs\LOCAL_SETUP.md`.

## Network Logic

The bridge splits traffic into:

- Ethernet.
- Wi-Fi client.
- Wi-Fi Direct / AP.

For AP mode:

- AP download means traffic from PC to AP clients.
- AP upload means traffic from AP clients to PC.

The Rainmeter graph shows total Rainmeter network traffic; the detail line shows interface split.

## Disk Logic

Disk I/O is shown per drive (`C:`, `D:`, `E:` by default) as read/write MB/s. Do not use a single global `Disk active 100%` bar because it is ambiguous and misleading.

## Source Of Truth

GitHub should be the primary source of truth for:

- source code;
- Rainmeter skin and presets;
- install/watch/switch scripts;
- public documentation;
- changelog.

Local staging folders and restore archives are optional convenience artifacts. They should not become the collaboration source of truth.
