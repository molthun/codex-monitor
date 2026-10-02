using System.Globalization;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

var configPath = GetArgValue(args, "--config")
    ?? Environment.GetEnvironmentVariable("CODEXMONITOR_CONFIG")
    ?? @"C:\CodexMonitor\config.json";

// Tray icon, started at sign-in without admin rights (one per user session).
if (args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)))
{
    using var trayMutex = new Mutex(true, "CodexMonitorTray", out var firstTray);
    if (!firstTray)
    {
        return;
    }
    // WinForms needs an STA thread; top-level statements run on an MTA one.
    var trayThread = new System.Threading.Thread(() =>
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new CodexBridge.TrayApp(configPath));
    });
    trayThread.SetApartmentState(System.Threading.ApartmentState.STA);
    trayThread.Start();
    trayThread.Join();
    return;
}

var settingsMode = args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));
if (settingsMode)
{
    // Opened from the Start menu after the tray icon was closed: bring the icon back too.
    if (!Mutex.TryOpenExisting("CodexMonitorTray", out var trayRunning))
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? "CodexBridge.exe", $"--tray --config \"{configPath}\"") { UseShellExecute = false });
    }
    else
    {
        trayRunning.Dispose();
    }

    var thread = new System.Threading.Thread(() =>
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new CodexBridge.SettingsForm(configPath));
    });
    thread.SetApartmentState(System.Threading.ApartmentState.STA);
    thread.Start();
    thread.Join();
    return;
}

// Generates the Rainmeter skin from the settings (Switch-WidgetSize.ps1 calls this):
//   --build-skin --out <CodexMonitor.ini> --screen-height <px> [--config <config.json>]
if (args.Any(a => string.Equals(a, "--build-skin", StringComparison.OrdinalIgnoreCase)))
{
    var skinConfig = CodexBridge.AppConfig.Load(configPath);
    var target = GetArgValue(args, "--out") ?? throw new ArgumentException("--build-skin needs --out <path to CodexMonitor.ini>");
    var screenHeight = int.TryParse(GetArgValue(args, "--screen-height"), out var h) ? h : 1080;
    var temps = ReadConfig(configPath).BridgeOutputFile ?? Path.Combine(Path.GetDirectoryName(target)!, @"@Resources\temps.txt");
    var inventoryPath = Path.Combine(Path.GetDirectoryName(temps)!, "inventory.json");
    var fans = CodexBridge.SkinBuilder.FanList(skinConfig, inventoryPath);
    var (skin, width, height, scale) = CodexBridge.SkinBuilder.Build(skinConfig, fans, screenHeight,
        Environment.ProcessPath ?? "CodexBridge.exe", CodexBridge.Available.Read(inventoryPath));
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
    // UTF-16 LE with BOM: the only Unicode encoding Rainmeter reads in skins. UTF-8 is read as ANSI,
    // which turned "°C" into "Â°C" and would garble non-ASCII fan and drive names.
    File.WriteAllText(target, skin, Encoding.Unicode);
    Console.WriteLine($"{{\"width\": {width}, \"height\": {height}, \"scale\": {scale.ToString("0.###", CultureInfo.InvariantCulture)}, \"fans\": {fans.Count}}}");
    return;
}

var config = ReadConfig(configPath);
var root = config.InstallRoot ?? @"C:\CodexMonitor";
var outFile = config.BridgeOutputFile ?? Path.Combine(root, @"@Resources\temps.txt");
var dumpMode = args.Any(a => string.Equals(a, "--dump", StringComparison.OrdinalIgnoreCase));
var onceMode = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase) || dumpMode);

Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

using var mutex = new Mutex(initiallyOwned: true, name: config.BridgeMutexName ?? "CodexMonitorHardwareBridge", out var createdNew);
if (!createdNew && !onceMode)
{
    return;
}

// Initialize LibreHardwareMonitor
var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMotherboardEnabled = true,
    IsControllerEnabled = true,
    IsMemoryEnabled = false,
    IsStorageEnabled = false
};

string? openError = null;
try
{
    computer.Open();
}
catch (Exception ex)
{
    openError = ex.Message;
    File.AppendAllText(Path.Combine(root, "CodexBridge.error.log"), $"{DateTime.Now:u} Failed to open LibreHardwareMonitor: {ex}\n");
}

var appConfig = CodexBridge.AppConfig.Load(configPath);
// Fans and extra temperatures chosen in the settings; without a fan list, defaults are picked once
// from the first sensor read (CPU fan + every other fan spinning then) and kept for this run.
var fanList = appConfig.Fans;
var tempList = appConfig.Temps;
var inventoryFile = Path.Combine(Path.GetDirectoryName(outFile)!, "inventory.json");
var inventoryAt = DateTime.MinValue;
List<string>? fileKeys = null;
// Sensors seen during this run; once seen they stay, so a missed read never reshapes the skin.
var seen = new CodexBridge.Available(false, false, false, false, false);

var lastErrorLog = DateTime.MinValue;
var networkPrevious = new Dictionary<string, (long Received, long Sent)>(StringComparer.OrdinalIgnoreCase);
var networkPreviousAt = DateTime.UtcNow;
// Last time Wi-Fi or the hotspot carried traffic: an adapter that is up but idle is reported as "Off",
// so the skin hides its legend (same rule as the Linux bridge).
DateTime? wirelessAt = null;
// Per-app traffic; app icons go next to temps.txt so the skin can show them from @Resources.
var appTraffic = new CodexBridge.AppTraffic(Path.Combine(Path.GetDirectoryName(outFile)!, "AppIcons"));
var netPanel = new CodexBridge.NetPanel();
var updateStatusFile = Path.Combine(root, "update-status.txt");

do
{
    try
    {
        var sensors = new List<SimpleSensor>();
        foreach (var hardware in computer.Hardware)
        {
            GetSensorsRecursive(hardware, sensors);
        }

        if (dumpMode)
        {
            Console.WriteLine($"{"HardwareType",-15} | {"HardwareName",-25} | {"SensorType",-15} | {"Value",-8} | {"SensorName",-25} | {"Identifier"}");
            Console.WriteLine(new string('-', 110));
            foreach (var sensor in sensors)
            {
                Console.WriteLine($"{sensor.HardwareType,-15} | {sensor.HardwareName,-25} | {sensor.Type,-15} | {sensor.Value,8:0.##} | {sensor.Name,-25} | {sensor.Identifier}");
            }
            computer.Close();
            return;
        }

        // 1. CPU Temperature
        var cpuTempSensor = sensors.FirstOrDefault(s => s.HardwareType == HardwareType.Cpu && s.Type == SensorType.Temperature && s.Name.Contains("Core (Average)", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => s.HardwareType == HardwareType.Cpu && s.Type == SensorType.Temperature && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => s.HardwareType == HardwareType.Cpu && s.Type == SensorType.Temperature && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => s.HardwareType == HardwareType.Cpu && s.Type == SensorType.Temperature);
        var cpuTemp = cpuTempSensor?.Value;

        fanList ??= CodexBridge.HardwareSensors.DefaultFans(sensors);
        fileKeys ??= CodexBridge.TempsFile.Keys(fanList.Count, tempList.Count);

        // 2. CPU Fan RPM (Usually under motherboard/SuperIO HardwareType as Fan)
        var cpuFanSensor = sensors.FirstOrDefault(s => (s.HardwareType == HardwareType.Motherboard || s.HardwareType == HardwareType.SuperIO) && s.Type == SensorType.Fan && s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => s.Type == SensorType.Fan && s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => (s.HardwareType == HardwareType.Motherboard || s.HardwareType == HardwareType.SuperIO) && s.Type == SensorType.Fan);
        var cpuFan = cpuFanSensor?.Value;

        // 3. GPU Sensors: the card chosen in the settings ("auto": NVIDIA, else the AMD card with the most memory, else Intel)
        var gpu = CodexBridge.HardwareSensors.PickGpu(sensors, appConfig.GpuDevice);
        var isGpu = new Func<SimpleSensor, bool>(s => gpu is not null && s.HardwareIdentifier == gpu.Id);

        var gpuCoreSensor = sensors.FirstOrDefault(s => isGpu(s) && s.Type == SensorType.Temperature && s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => isGpu(s) && s.Type == SensorType.Temperature && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => isGpu(s) && s.Type == SensorType.Temperature);
        var gpuCore = gpuCoreSensor?.Value;

        var gpuHotspotSensor = sensors.FirstOrDefault(s => isGpu(s) && s.Type == SensorType.Temperature && s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase));
        var gpuHotspot = gpuHotspotSensor?.Value;

        var gpuMemorySensor = sensors.FirstOrDefault(s => isGpu(s) && s.Type == SensorType.Temperature && (s.Name.Contains("GPU Memory", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase)));
        var gpuMemory = gpuMemorySensor?.Value;

        var gpuFanSensor = sensors.FirstOrDefault(s => isGpu(s) && s.Type == SensorType.Fan);
        var gpuFan = gpuFanSensor?.Value;

        var gpuFanPctSensor = sensors.FirstOrDefault(s => isGpu(s) && s.Type == SensorType.Control && s.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => isGpu(s) && s.Type == SensorType.Load && s.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase));
        var gpuFanPct = gpuFanPctSensor?.Value;

        var nvidiaGpu = gpu?.Type == HardwareType.GpuNvidia ? QueryNvidiaSmi() : null;
        if (nvidiaGpu is not null)
        {
            gpuCore = nvidiaGpu.Value.Temp ?? gpuCore;
            gpuFanPct = nvidiaGpu.Value.FanPct ?? gpuFanPct;
        }
        // VRAM: nvidia-smi for NVIDIA, LibreHardwareMonitor's memory sensors for AMD and Intel.
        var lhmVram = CodexBridge.HardwareSensors.Vram(sensors.Where(isGpu));
        var vramUsedMb = nvidiaGpu?.VramUsedMb ?? lhmVram.Used;
        var vramTotalMb = nvidiaGpu?.VramTotalMb ?? lhmVram.Total;
        var vramPct = vramUsedMb.HasValue && vramTotalMb.HasValue && vramTotalMb.Value > 0
            ? vramUsedMb.Value / vramTotalMb.Value * 100
            : (float?)null;

        // 4. Board/System Fans (Excluding CPU fan & GPU fan)
        var boardFansArray = new float?[7];
        var boardFanPrefix = config.BridgeBoardFanIdentifierPrefix;

        if (!string.IsNullOrEmpty(boardFanPrefix))
        {
            for (int i = 0; i < 7; i++)
            {
                var match = sensors.FirstOrDefault(s => s.Identifier.Equals(boardFanPrefix + i, StringComparison.OrdinalIgnoreCase) || s.Identifier.Equals(boardFanPrefix + "fan" + i, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    boardFansArray[i] = match.Value;
                }
            }
        }

        // If prefix didn't match or wasn't provided, auto-map by parsing index from motherboard/SuperIO fan identifiers
        if (boardFansArray.All(f => !f.HasValue))
        {
            var boardFanSensors = sensors
                .Where(s => (s.HardwareType == HardwareType.Motherboard || s.HardwareType == HardwareType.SuperIO) && s.Type == SensorType.Fan)
                .Where(s => s.Identifier != cpuFanSensor?.Identifier && s.Identifier != gpuFanSensor?.Identifier)
                .ToList();

            foreach (var s in boardFanSensors)
            {
                int fanIndex = -1;
                var lastSlash = s.Identifier.LastIndexOf('/');
                if (lastSlash >= 0 && int.TryParse(s.Identifier.Substring(lastSlash + 1), out var idx))
                {
                    fanIndex = idx;
                }
                else if (s.Name.StartsWith("Fan #", StringComparison.OrdinalIgnoreCase) && int.TryParse(s.Name.Substring(5), out var idx2))
                {
                    fanIndex = idx2 - 1;
                }

                if (fanIndex >= 0 && fanIndex < 7)
                {
                    boardFansArray[fanIndex] = s.Value;
                }
            }

            // If still no fans matched (e.g. index parsing yielded nothing), auto-map sequentially
            if (boardFansArray.All(f => !f.HasValue))
            {
                var otherRpmSensors = boardFanSensors
                    .OrderBy(s => s.Name)
                    .Take(7)
                    .ToList();

                for (int i = 0; i < 7; i++)
                {
                    if (i < otherRpmSensors.Count)
                    {
                        boardFansArray[i] = otherRpmSensors[i].Value;
                    }
                }
            }
        }

        // 5. PSU Fan
        var psuFanSensor = sensors.FirstOrDefault(s => s.HardwareType == HardwareType.Psu && s.Type == SensorType.Fan)
            ?? sensors.FirstOrDefault(s => s.Type == SensorType.Fan && s.Name.Contains("PSU", StringComparison.OrdinalIgnoreCase));
        var psuFan = psuFanSensor?.Value;

        // 6. Network Rates
        var network = QueryNetworkRates(networkPrevious, ref networkPreviousAt, config, out var linkMbps);
        var apps = new Dictionary<string, CodexBridge.AppRates>();
        var appsOk = false;
        try
        {
            apps = appTraffic.Sample();
            appsOk = appTraffic.Available;
        }
        catch
        {
            // Per-app counters are optional; the panel falls back to totals.
        }
        var panel = netPanel.Build(
            network.EthInMbps + network.WifiInMbps + network.WifiApInMbps,
            network.EthOutMbps + network.WifiOutMbps + network.WifiApOutMbps,
            linkMbps, apps, appsOk, config.NetPanel, ReadUpdateTag(updateStatusFile));

        seen = new CodexBridge.Available(seen.CpuTemp || cpuTemp.HasValue, seen.Gpu || gpu is not null,
            seen.GpuTemp || gpuCore.HasValue, seen.Vram || vramTotalMb > 0, seen.GpuFan || gpuFan.HasValue || gpuFanPct.HasValue);

        // Below 0.1 Mbps is background chatter (ARP, mDNS) an idle adapter still receives.
        if (network.WifiActiveInMbps + network.WifiActiveOutMbps >= 0.1)
        {
            wirelessAt = DateTime.UtcNow;
        }
        var wirelessShown = network.WifiActiveMode != "Off" && wirelessAt is { } at && DateTime.UtcNow - at <= TimeSpan.FromSeconds(60);
        string Mbps(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        var values = new Dictionary<string, string>
        {
            ["CPU"] = Round(cpuTemp),
            ["GPUCore"] = Round(gpuCore),
            ["GPUHotspot"] = Round(gpuHotspot),
            ["GPUMemory"] = Round(gpuMemory),
            ["VRAMUsedMB"] = Round(vramUsedMb),
            ["VRAMTotalMB"] = Round(vramTotalMb),
            ["VRAMPct"] = Round(vramPct),
            ["GPUFan"] = Round(gpuFan),
            ["GPUFanPct"] = Round(gpuFanPct),
            ["CPUFan"] = Round(cpuFan),
            ["PSUFan"] = Round(psuFan),
            ["NetEthInMbps"] = Mbps(network.EthInMbps),
            ["NetEthOutMbps"] = Mbps(network.EthOutMbps),
            ["NetWifiInMbps"] = Mbps(network.WifiInMbps),
            ["NetWifiOutMbps"] = Mbps(network.WifiOutMbps),
            ["NetWifiApInMbps"] = Mbps(network.WifiApInMbps),
            ["NetWifiApOutMbps"] = Mbps(network.WifiApOutMbps),
            ["NetWifiActiveMode"] = wirelessShown ? network.WifiActiveMode : "Off",
            ["NetWifiActiveInMbps"] = Mbps(wirelessShown ? network.WifiActiveInMbps : 0),
            ["NetWifiActiveOutMbps"] = Mbps(wirelessShown ? network.WifiActiveOutMbps : 0),
            ["NetWifiActiveDlMbps"] = Mbps(wirelessShown ? network.WifiActiveDlMbps : 0),
            ["NetWifiActiveUlMbps"] = Mbps(wirelessShown ? network.WifiActiveUlMbps : 0),
            ["BridgeSource"] = $"LibreHardwareMonitor{(nvidiaGpu is null ? "" : "+NvidiaSmi")}",
        };
        for (var i = 0; i < 7; i++)
        {
            values[$"BoardFan{i + 1}"] = Round(boardFansArray[i]);
        }
        foreach (var (key, value) in panel.Concat(CodexBridge.HardwareSensors.ListValues(sensors, fanList, tempList)))
        {
            values[key] = value;
        }
        var content = CodexBridge.TempsFile.Format(fileKeys, values);

        // The settings window lists this PC's GPUs, fans, temperatures and drives from here.
        if (DateTime.UtcNow - inventoryAt > TimeSpan.FromSeconds(2))
        {
            SafeWriteAllText(inventoryFile, CodexBridge.HardwareSensors.Inventory(sensors, fanList, linkMbps,
                CodexBridge.HardwareSensors.Status(computer, sensors, openError), seen).ToJsonString());
            inventoryAt = DateTime.UtcNow;
        }

        SafeWriteAllText(outFile, content);
        Console.Write(content);
    }
    catch (Exception ex)
    {
        if (DateTime.UtcNow - lastErrorLog > TimeSpan.FromMinutes(1))
        {
            File.AppendAllText(Path.Combine(root, "CodexBridge.error.log"), $"{DateTime.Now:u} {ex}\n");
            lastErrorLog = DateTime.UtcNow;
        }

        TryWriteNvidiaFallback(outFile, fileKeys);
    }

    if (onceMode)
    {
        computer.Close();
        return;
    }

    await Task.Delay(TimeSpan.FromSeconds(config.BridgeUpdateSeconds ?? 1));
}
while (true);

static void GetSensorsRecursive(IHardware hardware, List<SimpleSensor> list)
{
    try
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
        {
            GetSensorsRecursive(sub, list);
        }
        foreach (var sensor in hardware.Sensors)
        {
            list.Add(new SimpleSensor
            {
                Name = sensor.Name,
                Identifier = sensor.Identifier.ToString(),
                Type = sensor.SensorType,
                Value = sensor.Value,
                HardwareName = hardware.Name,
                HardwareIdentifier = hardware.Identifier.ToString(),
                HardwareType = hardware.HardwareType
            });
        }
    }
    catch
    {
        // Suppress driver/sensor read failures for specific hardware items
    }
}

static string? GetArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }

    return null;
}

static BridgeConfig ReadConfig(string path)
{
    var config = new BridgeConfig();
    try
    {
        if (File.Exists(path))
        {
            ApplyConfig(config, path);
        }
    }
    catch
    {
        // Keep defaults if config is missing or malformed.
    }

    return config;
}

static void ApplyConfig(BridgeConfig config, string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var rootElement = document.RootElement;
    if (rootElement.TryGetProperty("installRoot", out var installRoot) && installRoot.ValueKind == JsonValueKind.String)
    {
        config.InstallRoot = installRoot.GetString();
    }

    if (rootElement.TryGetProperty("bridge", out var bridge) && bridge.ValueKind == JsonValueKind.Object)
    {
        if (bridge.TryGetProperty("outputFile", out var outputFile) && outputFile.ValueKind == JsonValueKind.String)
        {
            config.BridgeOutputFile = outputFile.GetString();
        }

        if (bridge.TryGetProperty("mutexName", out var mutexName) && mutexName.ValueKind == JsonValueKind.String)
        {
            config.BridgeMutexName = mutexName.GetString();
        }

        if (bridge.TryGetProperty("updateSeconds", out var updateSeconds) && updateSeconds.TryGetDouble(out var seconds))
        {
            config.BridgeUpdateSeconds = Math.Max(0.25, seconds);
        }

        if (bridge.TryGetProperty("boardFanIdentifierPrefix", out var prefix) && prefix.ValueKind == JsonValueKind.String)
        {
            config.BridgeBoardFanIdentifierPrefix = prefix.GetString();
        }
    }

    if (rootElement.TryGetProperty("network", out var network) && network.ValueKind == JsonValueKind.Object)
    {
        if (network.TryGetProperty("ignoreAdaptersContaining", out var ignore) && ignore.ValueKind == JsonValueKind.Array)
        {
            config.NetworkIgnoreAdapters = ignore.EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        if (network.TryGetProperty("wifiApNamesContaining", out var wifiAp) && wifiAp.ValueKind == JsonValueKind.Array)
        {
            config.NetworkWifiApNames = wifiAp.EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        if (network.TryGetProperty("wifiNamesContaining", out var wifi) && wifi.ValueKind == JsonValueKind.Array)
        {
            config.NetworkWifiNames = wifi.EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        if (network.TryGetProperty("ethernetNamesContaining", out var eth) && eth.ValueKind == JsonValueKind.Array)
        {
            config.NetworkEthernetNames = eth.EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        if (network.TryGetProperty("internetDownMbps", out var planDown) && planDown.TryGetDouble(out var planDownValue))
        {
            config.NetPanel.InternetDownMbps = planDownValue;
        }
        if (network.TryGetProperty("internetUpMbps", out var planUp) && planUp.TryGetDouble(out var planUpValue))
        {
            config.NetPanel.InternetUpMbps = planUpValue;
        }
        if (network.TryGetProperty("lanMbps", out var lanMbps) && lanMbps.TryGetDouble(out var lanMbpsValue))
        {
            config.NetPanel.LanMbps = lanMbpsValue;
        }
    }
}

// -1 = no such sensor: the skin shows n/a instead of a believable 0.
static string Round(float? value)
{
    return value.HasValue
        ? Math.Round(value.Value).ToString(CultureInfo.InvariantCulture)
        : "-1";
}

static void TryWriteNvidiaFallback(string outFile, List<string>? keys)
{
    try
    {
        var existing = ReadExisting(outFile);
        var gpu = QueryNvidiaSmi();
        if (gpu is null || keys is null)
        {
            return;
        }

        existing["GPUCore"] = Round(gpu.Value.Temp);
        existing["VRAMUsedMB"] = Round(gpu.Value.VramUsedMb);
        existing["VRAMTotalMB"] = Round(gpu.Value.VramTotalMb);
        existing["VRAMPct"] = gpu.Value.VramUsedMb.HasValue && gpu.Value.VramTotalMb.HasValue && gpu.Value.VramTotalMb.Value > 0
            ? Round(gpu.Value.VramUsedMb.Value / gpu.Value.VramTotalMb.Value * 100)
            : Get(existing, "VRAMPct");
        existing["GPUFan"] = "0";
        existing["GPUFanPct"] = Round(gpu.Value.FanPct);
        existing["BridgeSource"] = "NvidiaSmiFallback";

        SafeWriteAllText(outFile, CodexBridge.TempsFile.Format(keys, existing));
    }
    catch
    {
        // The bridge should keep retrying
    }
}

static Dictionary<string, string> ReadExisting(string outFile)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(outFile))
    {
        return values;
    }

    foreach (var line in File.ReadAllLines(outFile))
    {
        var split = line.Split('=', 2);
        if (split.Length == 2)
        {
            values[split[0]] = split[1];
        }
    }

    return values;
}

// Written by the display watcher when a newer release is out (empty otherwise).
static string ReadUpdateTag(string path)
{
    try
    {
        return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() ?? "" : "";
    }
    catch (IOException)
    {
        return "";
    }
}

static string Get(Dictionary<string, string> values, string key)
{
    return values.TryGetValue(key, out var value) ? value : "0";
}

static (float? Temp, float? FanPct, float? VramUsedMb, float? VramTotalMb)? QueryNvidiaSmi()
{
    var psi = new ProcessStartInfo
    {
        FileName = "nvidia-smi.exe",
        Arguments = "--query-gpu=temperature.gpu,fan.speed,memory.used,memory.total --format=csv,noheader,nounits",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    Process? process = null;
    try
    {
        process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        // Read stdout asynchronously so it drains while the process runs (avoids the
        // classic ReadToEnd/WaitForExit deadlock), and enforce a hard timeout. If
        // nvidia-smi hangs we kill it instead of blocking the whole bridge loop.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(3000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }

        if (!outputTask.Wait(1000))
        {
            return null;
        }

        var output = outputTask.Result.Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var parts = output.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return (
            ParseFloat(parts[0]),
            ParseFloat(parts[1]),
            parts.Length > 2 ? ParseFloat(parts[2]) : null,
            parts.Length > 3 ? ParseFloat(parts[3]) : null);
    }
    catch
    {
        return null;
    }
    finally
    {
        process?.Dispose();
    }
}

static float? ParseFloat(string value)
{
    return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : null;
}

static (double EthInMbps, double EthOutMbps, double WifiInMbps, double WifiOutMbps, double WifiApInMbps, double WifiApOutMbps, string WifiActiveMode, double WifiActiveInMbps, double WifiActiveOutMbps, double WifiActiveDlMbps, double WifiActiveUlMbps) QueryNetworkRates(
    Dictionary<string, (long Received, long Sent)> previous,
    ref DateTime previousAt,
    BridgeConfig config,
    out double? linkMbps)
{
    linkMbps = null;
    var now = DateTime.UtcNow;
    var seconds = Math.Max((now - previousAt).TotalSeconds, 0.001);
    double ethIn = 0;
    double ethOut = 0;
    double wifiIn = 0;
    double wifiOut = 0;
    double wifiApIn = 0;
    double wifiApOut = 0;
    var wifiUp = false;
    var wifiApUp = false;
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var ignoreList = config.NetworkIgnoreAdapters ?? new List<string>
    {
        "hyper-v", "virtual switch", "virtual switch extension", "virtual filtering platform",
        "wsl", "teredo", "teredo tunneling", "wan miniport", "qos packet scheduler",
        "wfp native mac layer", "wfp 802.3 mac layer", "lightweight filter",
        "native wifi filter driver", "virtual wifi filter driver", "pseudo-interface",
        "vswitch", "vethernet", "bluetooth"
    };
    var wifiApList = config.NetworkWifiApNames ?? new List<string> { "wi-fi direct", "wifi direct", "hotspot" };
    var wifiList = config.NetworkWifiNames ?? new List<string> { "wi-fi", "wifi", "wireless", "wlan", "беспровод" };
    var ethList = config.NetworkEthernetNames ?? new List<string> { "ethernet", "i219-v", "intel" };

    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (nic.OperationalStatus != OperationalStatus.Up)
        {
            continue;
        }

        var description = nic.Description ?? "";
        var name = nic.Name ?? "";
        var text = $"{name} {description}";
        var lower = text.ToLowerInvariant();

        if (ignoreList.Any(ignore => lower.Contains(ignore, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        long rxBytes = 0;
        long txBytes = 0;

        try
        {
            var stats = nic.GetIPStatistics();
            rxBytes = stats.BytesReceived;
            txBytes = stats.BytesSent;
        }
        catch 
        {
            try
            {
                var stats4 = nic.GetIPv4Statistics();
                rxBytes = stats4.BytesReceived;
                txBytes = stats4.BytesSent;
            }
            catch { }
        }

        if (nic.Speed > 0)
        {
            linkMbps = Math.Max(linkMbps ?? 0, nic.Speed / 1_000_000.0);
        }

        seen.Add(nic.Id);
        previous.TryGetValue(nic.Id, out var old);
        previous[nic.Id] = (rxBytes, txBytes);

        if (old.Received <= 0 && old.Sent <= 0)
        {
            continue;
        }

        var rxMbps = Math.Max(0, rxBytes - old.Received) * 8 / seconds / 1_000_000;
        var txMbps = Math.Max(0, txBytes - old.Sent) * 8 / seconds / 1_000_000;
        var isWifiDirect = wifiApList.Any(ap => lower.Contains(ap, StringComparison.OrdinalIgnoreCase));
        var isWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                     wifiList.Any(w => lower.Contains(w, StringComparison.OrdinalIgnoreCase));
        var isEthernet = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                         ethList.Any(e => lower.Contains(e, StringComparison.OrdinalIgnoreCase));

        if (isWifiDirect)
        {
            wifiApUp = true;
            wifiApIn += rxMbps;
            wifiApOut += txMbps;
        }
        else if (isWifi)
        {
            wifiUp = true;
            wifiIn += rxMbps;
            wifiOut += txMbps;
        }
        else if (isEthernet)
        {
            ethIn += rxMbps;
            ethOut += txMbps;
        }
    }

    foreach (var id in previous.Keys.Where(id => !seen.Contains(id)).ToList())
    {
        previous.Remove(id);
    }

    previousAt = now;
    var wifiTraffic = wifiIn + wifiOut;
    var wifiApTraffic = wifiApIn + wifiApOut;
    var activeMode = wifiApTraffic > 0.05 || (wifiApUp && !wifiUp)
        ? "AP"
        : wifiTraffic > 0.05 || wifiUp
            ? "WiFi"
            : wifiApUp
                ? "AP"
                : "Off";
    var activeIn = activeMode == "AP" ? wifiApIn : activeMode == "WiFi" ? wifiIn : 0;
    var activeOut = activeMode == "AP" ? wifiApOut : activeMode == "WiFi" ? wifiOut : 0;
    var activeDl = activeMode == "AP" ? wifiApOut : activeIn;
    var activeUl = activeMode == "AP" ? wifiApIn : activeOut;

    return (ethIn, ethOut, wifiIn, wifiOut, wifiApIn, wifiApOut, activeMode, activeIn, activeOut, activeDl, activeUl);
}

static void SafeWriteAllText(string path, string content)
{
    const int maxRetries = 5;
    // Write to a sibling temp file, then atomically replace the target. A reader
    // (Rainmeter's WebParser) therefore always sees either the complete old file
    // or the complete new file, never a half-written/truncated one. This avoids
    // the all-zeros flash that happened when FileMode.Create truncated the file
    // mid-read.
    var tempPath = path + ".tmp";
    for (int i = 0; i < maxRetries; i++)
    {
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            // UTF-8 without BOM: app names may be non-ASCII; the skin reads it with CodePage=65001.
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
            File.Move(tempPath, path, overwrite: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows throws UnauthorizedAccessException (not IOException) for some
            // transient sharing/lock conditions: AV scanning the .tmp file, or a
            // reader holding the target open during File.Move. Retry those too.
            if (i == maxRetries - 1)
            {
                throw;
            }
            System.Threading.Thread.Sleep(50);
        }
    }
}

sealed class SimpleSensor
{
    public string Name { get; set; } = "";
    public string Identifier { get; set; } = "";
    public SensorType Type { get; set; }
    public float? Value { get; set; }
    public string HardwareName { get; set; } = "";
    public string HardwareIdentifier { get; set; } = "";
    public HardwareType HardwareType { get; set; }
}

sealed class BridgeConfig
{
    public string? InstallRoot { get; set; }
    public string? BridgeOutputFile { get; set; }
    public string? BridgeMutexName { get; set; }
    public double? BridgeUpdateSeconds { get; set; }
    public string? BridgeBoardFanIdentifierPrefix { get; set; }
    public List<string>? NetworkIgnoreAdapters { get; set; }
    public List<string>? NetworkWifiApNames { get; set; }
    public List<string>? NetworkWifiNames { get; set; }
    public List<string>? NetworkEthernetNames { get; set; }
    public CodexBridge.NetPanelConfig NetPanel { get; } = new();
}
