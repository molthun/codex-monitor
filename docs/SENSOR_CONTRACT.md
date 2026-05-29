# Sensor Contract

`CodexBridge.exe` writes this file:

```text
C:\CodexMonitor\@Resources\temps.txt
```

Rainmeter depends on these exact keys. Keep compatibility unless the skin is updated in the same change.

## Current Keys

```text
CPU=42
GPUCore=46
GPUHotspot=0
GPUMemory=0
GPUFan=0
GPUFanPct=0
CPUFan=1536
BoardFan1=1536
BoardFan2=964
BoardFan3=0
BoardFan4=0
BoardFan5=0
BoardFan6=0
BoardFan7=824
PSUFan=0
NetEthInMbps=1.1
NetEthOutMbps=0.2
NetWifiInMbps=0.0
NetWifiOutMbps=0.0
NetWifiApInMbps=0.0
NetWifiApOutMbps=0.0
NetWifiActiveMode=Off
NetWifiActiveInMbps=0.0
NetWifiActiveOutMbps=0.0
NetWifiActiveDlMbps=0.0
NetWifiActiveUlMbps=0.0
BridgeSource=LibreHardwareMonitor+NvidiaSmi
```

## Meaning

- `CPU`: CPU temperature in Celsius.
- `GPUCore`: GPU core temperature in Celsius.
- `GPUHotspot`: GPU hotspot temperature if available.
- `GPUMemory`: GPU memory temperature if available.
- `GPUFan`: GPU fan speed in RPM if available.
- `GPUFanPct`: GPU fan speed percent if available.
- `CPUFan`: CPU cooler RPM.
- `BoardFan1..BoardFan7`: motherboard/SuperIO fan channels exposed by LibreHardwareMonitor.
- `PSUFan`: explicit PSU fan sensor if LibreHardwareMonitor exposes one.
- `NetEthInMbps`, `NetEthOutMbps`: Ethernet receive/transmit Mbps.
- `NetWifiInMbps`, `NetWifiOutMbps`: Wi-Fi client receive/transmit Mbps.
- `NetWifiApInMbps`, `NetWifiApOutMbps`: Wi-Fi Direct/AP receive/transmit Mbps.
- `NetWifiActiveMode`: `WiFi`, `AP`, or `Off`.
- `NetWifiActiveInMbps`, `NetWifiActiveOutMbps`: active Wi-Fi/AP raw interface direction.
- `NetWifiActiveDlMbps`, `NetWifiActiveUlMbps`: user-facing download/upload for active Wi-Fi/AP mode.
- `BridgeSource`: current bridge data source string.

## Network Direction Notes

For normal Wi-Fi client mode:

- DL = adapter receive.
- UL = adapter transmit.

For AP mode:

- AP DL = PC transmits to connected clients.
- AP UL = PC receives from connected clients.

This reversal is intentional for user-facing AP semantics.

## Compatibility Rule

Adding new keys is safe.

Renaming/removing keys is not safe unless all Rainmeter WebParser measures are updated in the same change.

## Custom Configuration

The output of the bridge is customizable via the workstation's `config.json`:
- **Motherboard Fans (`BoardFan1..7`)**: The search prefix is configured using `"boardFanIdentifierPrefix"` under `"bridge"` (default: `"/lpc/nct6796dr/fan/"`). If no sensors match this prefix, the bridge dynamically falls back to listing any other available RPM sensors not mapped to the CPU or GPU.
- **Network Traffic**: Classification of adapters is configured via the `"network"` section in `config.json` (`ignoreAdaptersContaining`, `wifiApNamesContaining`, `wifiNamesContaining`, `ethernetNamesContaining`). The bridge supports IPv6 traffic monitoring automatically by utilizing combined IP statistics.

## Debugging

Dump LibreHardwareMonitor sensors:

```powershell
& "C:\CodexMonitor\CodexBridge\bin\Release\net10.0\CodexBridge.exe" --dump
```

Run once:

```powershell
& "C:\CodexMonitor\CodexBridge\bin\Release\net10.0\CodexBridge.exe" --once
```

Read current output:

```powershell
Get-Content "C:\CodexMonitor\@Resources\temps.txt"
```
