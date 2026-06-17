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

var settingsMode = args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));
if (settingsMode)
{
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

try
{
    computer.Open();
}
catch (Exception ex)
{
    File.AppendAllText(Path.Combine(root, "CodexBridge.error.log"), $"{DateTime.Now:u} Failed to open LibreHardwareMonitor: {ex}\n");
}

var lastErrorLog = DateTime.MinValue;
var networkPrevious = new Dictionary<string, (long Received, long Sent)>(StringComparer.OrdinalIgnoreCase);
var networkPreviousAt = DateTime.UtcNow;

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

        // 2. CPU Fan RPM (Usually under motherboard/SuperIO HardwareType as Fan)
        var cpuFanSensor = sensors.FirstOrDefault(s => (s.HardwareType == HardwareType.Motherboard || s.HardwareType == HardwareType.SuperIO) && s.Type == SensorType.Fan && s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => s.Type == SensorType.Fan && s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => (s.HardwareType == HardwareType.Motherboard || s.HardwareType == HardwareType.SuperIO) && s.Type == SensorType.Fan);
        var cpuFan = cpuFanSensor?.Value;

        // 3. GPU Sensors
        var isGpu = new Func<SimpleSensor, bool>(s => s.HardwareType == HardwareType.GpuNvidia || s.HardwareType == HardwareType.GpuAmd || s.HardwareType == HardwareType.GpuIntel);
        
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

        var nvidiaGpu = QueryNvidiaSmi();
        if (nvidiaGpu is not null)
        {
            gpuCore = nvidiaGpu.Value.Temp ?? gpuCore;
            gpuFanPct = nvidiaGpu.Value.FanPct ?? gpuFanPct;
        }
        var vramUsedMb = nvidiaGpu?.VramUsedMb;
        var vramTotalMb = nvidiaGpu?.VramTotalMb;
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
        var network = QueryNetworkRates(networkPrevious, ref networkPreviousAt, config);

        var content = new StringBuilder()
            .Append($"CPU={Round(cpuTemp)}\n")
            .Append($"GPUCore={Round(gpuCore)}\n")
            .Append($"GPUHotspot={Round(gpuHotspot)}\n")
            .Append($"GPUMemory={Round(gpuMemory)}\n")
            .Append($"VRAMUsedMB={Round(vramUsedMb)}\n")
            .Append($"VRAMTotalMB={Round(vramTotalMb)}\n")
            .Append($"VRAMPct={Round(vramPct)}\n")
            .Append($"GPUFan={Round(gpuFan)}\n")
            .Append($"GPUFanPct={Round(gpuFanPct)}\n")
            .Append($"CPUFan={Round(cpuFan)}\n")
            .Append($"BoardFan1={Round(boardFansArray[0])}\n")
            .Append($"BoardFan2={Round(boardFansArray[1])}\n")
            .Append($"BoardFan3={Round(boardFansArray[2])}\n")
            .Append($"BoardFan4={Round(boardFansArray[3])}\n")
            .Append($"BoardFan5={Round(boardFansArray[4])}\n")
            .Append($"BoardFan6={Round(boardFansArray[5])}\n")
            .Append($"BoardFan7={Round(boardFansArray[6])}\n")
            .Append($"PSUFan={Round(psuFan)}\n")
            .Append($"NetEthInMbps={network.EthInMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"NetEthOutMbps={network.EthOutMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"NetWifiInMbps={network.WifiInMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"NetWifiOutMbps={network.WifiOutMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"NetWifiApInMbps={network.WifiApInMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"NetWifiApOutMbps={network.WifiApOutMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"NetWifiActiveMode={network.WifiActiveMode}\n")
            .Append($"NetWifiActiveInMbps={network.WifiActiveInMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"NetWifiActiveOutMbps={network.WifiActiveOutMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"NetWifiActiveDlMbps={network.WifiActiveDlMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"NetWifiActiveUlMbps={network.WifiActiveUlMbps.ToString("0.0", CultureInfo.InvariantCulture)}\n")
            .Append($"BridgeSource=LibreHardwareMonitor{(nvidiaGpu is null ? "" : "+NvidiaSmi")}\n")
            .ToString();

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

        TryWriteNvidiaFallback(outFile);
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
    }
}

static string Round(float? value)
{
    return value.HasValue
        ? Math.Round(value.Value).ToString(CultureInfo.InvariantCulture)
        : "0";
}

static void TryWriteNvidiaFallback(string outFile)
{
    try
    {
        var existing = ReadExisting(outFile);
        var gpu = QueryNvidiaSmi();
        if (gpu is null)
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

        var content = new StringBuilder()
            .Append($"CPU={Get(existing, "CPU")}\n")
            .Append($"GPUCore={Get(existing, "GPUCore")}\n")
            .Append($"GPUHotspot={Get(existing, "GPUHotspot")}\n")
            .Append($"GPUMemory={Get(existing, "GPUMemory")}\n")
            .Append($"VRAMUsedMB={Get(existing, "VRAMUsedMB")}\n")
            .Append($"VRAMTotalMB={Get(existing, "VRAMTotalMB")}\n")
            .Append($"VRAMPct={Get(existing, "VRAMPct")}\n")
            .Append($"GPUFan={Get(existing, "GPUFan")}\n")
            .Append($"GPUFanPct={Get(existing, "GPUFanPct")}\n")
            .Append($"CPUFan={Get(existing, "CPUFan")}\n")
            .Append($"BoardFan1={Get(existing, "BoardFan1")}\n")
            .Append($"BoardFan2={Get(existing, "BoardFan2")}\n")
            .Append($"BoardFan3={Get(existing, "BoardFan3")}\n")
            .Append($"BoardFan4={Get(existing, "BoardFan4")}\n")
            .Append($"BoardFan5={Get(existing, "BoardFan5")}\n")
            .Append($"BoardFan6={Get(existing, "BoardFan6")}\n")
            .Append($"BoardFan7={Get(existing, "BoardFan7")}\n")
            .Append($"PSUFan={Get(existing, "PSUFan")}\n")
            .Append($"NetEthInMbps={Get(existing, "NetEthInMbps")}\n")
            .Append($"NetEthOutMbps={Get(existing, "NetEthOutMbps")}\n")
            .Append($"NetWifiInMbps={Get(existing, "NetWifiInMbps")}\n")
            .Append($"NetWifiOutMbps={Get(existing, "NetWifiOutMbps")}\n")
            .Append($"NetWifiApInMbps={Get(existing, "NetWifiApInMbps")}\n")
            .Append($"NetWifiApOutMbps={Get(existing, "NetWifiApOutMbps")}\n")
            .Append($"NetWifiActiveMode={Get(existing, "NetWifiActiveMode")}\n")
            .Append($"NetWifiActiveInMbps={Get(existing, "NetWifiActiveInMbps")}\n")
            .Append($"NetWifiActiveOutMbps={Get(existing, "NetWifiActiveOutMbps")}\n")
            .Append($"NetWifiActiveDlMbps={Get(existing, "NetWifiActiveDlMbps")}\n")
            .Append($"NetWifiActiveUlMbps={Get(existing, "NetWifiActiveUlMbps")}\n")
            .Append($"BridgeSource={Get(existing, "BridgeSource")}\n")
            .ToString();

        SafeWriteAllText(outFile, content);
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
    BridgeConfig config)
{
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
            using (var writer = new StreamWriter(fs, Encoding.ASCII))
            {
                writer.Write(content);
            }
            File.Move(tempPath, path, overwrite: true);
            return;
        }
        catch (IOException)
        {
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
}
