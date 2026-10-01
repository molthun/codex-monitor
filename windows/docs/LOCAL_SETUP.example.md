# Local Setup Example

Copy this file to `docs\LOCAL_SETUP.md` and fill it with machine-specific details.

`docs\LOCAL_SETUP.md` is ignored by Git and must not be committed.

## Machine

- Windows version: `<Windows version>`
- CPU: `<CPU model>`
- GPU: `<GPU model>`
- Motherboard: `<motherboard model>`
- Super I/O: `<chip if known>`
- Primary display: `<resolution and coordinates>`
- Secondary display: `<resolution and coordinates>`

## Local Paths

Install root:

```text
C:\CodexMonitor
```

Rainmeter skin path:

```text
<RainmeterSkinPath>\CodexMonitor\CodexMonitor.ini
```

Rainmeter config:

```text
%APPDATA%\Rainmeter\Rainmeter.ini
```

Optional local reinstall/staging folder:

```text
<LocalStagingFolder>
```

## Sensor Mapping

Document local fan/sensor mapping here:

```text
CPU cooler = <LibreHardwareMonitor sensor name / identifier>
Case fan = <LibreHardwareMonitor sensor name / identifier>
PSU fan = <LibreHardwareMonitor sensor name / identifier>
GPU fans = <LibreHardwareMonitor or nvidia-smi>
```

## Notes

- Add anything needed for this exact machine.
- Do not commit this copied local file.
