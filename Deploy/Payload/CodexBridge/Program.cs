using System.Globalization;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Linq;
using FanControl.IPC;

var configPath = GetArgValue(args, "--config")
    ?? Environment.GetEnvironmentVariable("CODEXMONITOR_CONFIG")
    ?? @"C:\CodexMonitor\config.json";
var config = ReadConfig(configPath);
var root = config.InstallRoot ?? @"C:\CodexMonitor";
var outFile = config.BridgeOutputFile ?? Path.Combine(root, @"@Resources\temps.txt");
var dumpMode = args.Any(a => string.Equals(a, "--dump", StringComparison.OrdinalIgnoreCase));
var onceMode = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase) || dumpMode);

Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

using var mutex = new Mutex(initiallyOwned: true, name: config.BridgeMutexName ?? "CodexMonitorFanControlBridge", out var createdNew);
if (!createdNew && !onceMode)
{
    return;
}

    var sensorClient = IPCFactory.GetSensorClient();
    var lastErrorLog = DateTime.MinValue;
    var networkPrevious = new Dictionary<string, (long Received, long Sent)>(StringComparer.OrdinalIgnoreCase);
    var networkPreviousAt = DateTime.UtcNow;

    do
    {
        try
        {
            if (sensorClient == null)
            {
                sensorClient = IPCFactory.GetSensorClient();
            }
            var sensorsReply = await sensorClient.GetAllSensorsAsync(new GetAllSensorsRequest());
            var sensors = sensorsReply.Sensors
                .Where(s => s.HasValue)
                .OrderBy(s => s.Type.ToString())
                .ThenBy(s => s.Name)
                .ToList();

            if (dumpMode)
            {
                foreach (var sensor in sensors)
                {
                    Console.WriteLine($"{sensor.Type,-12} | {sensor.Value,8:0.##} | {sensor.Name} | {sensor.Identifier} | {sensor.Origin}");
                }
                return;
            }

            var cpuTempSensor = PickSensor(sensors, SensorMessageType.Temperature, "cpu", "package")
                ?? PickSensor(sensors, SensorMessageType.Temperature, "cpu")
                ?? PickSensor(sensors, SensorMessageType.Temperature, "processor");
            var cpuTemp = cpuTempSensor?.Value;

            var cpuFanSensor = PickSensor(sensors, SensorMessageType.Rpm, "cpu")
                ?? PickSensor(sensors, SensorMessageType.Rpm, "processor")
                ?? PickSensor(sensors, SensorMessageType.Rpm, "cpu_fan")
                ?? PickSensor(sensors, SensorMessageType.Rpm, "/lpc/nct6796dr/fan/0")
                ?? PickSensor(sensors, SensorMessageType.Rpm, "fan #1")
                ?? PickSensor(sensors, SensorMessageType.Rpm, "fan 1");
            var cpuFan = cpuFanSensor?.Value;

            var gpuCoreSensor = PickSensor(sensors, SensorMessageType.Temperature, "nvidia", "gpu")
                ?? PickSensor(sensors, SensorMessageType.Temperature, "gpu", "core")
                ?? PickSensor(sensors, SensorMessageType.Temperature, "gpu");
            var gpuCore = gpuCoreSensor?.Value;

            var gpuHotspotSensor = PickSensor(sensors, SensorMessageType.Temperature, "gpu", "hot");
            var gpuHotspot = gpuHotspotSensor?.Value;

            var gpuMemorySensor = PickSensor(sensors, SensorMessageType.Temperature, "gpu", "memory");
            var gpuMemory = gpuMemorySensor?.Value;

            var gpuFanSensor = PickSensor(sensors, SensorMessageType.Rpm, "nvidia")
                ?? PickSensor(sensors, SensorMessageType.Rpm, "gpu");
            var gpuFan = gpuFanSensor?.Value;

            var gpuFanPctSensor = PickSensor(sensors, SensorMessageType.UsagePercent, "gpu", "fan")
                ?? PickSensor(sensors, SensorMessageType.Control, "gpu", "fan")
                ?? PickSensor(sensors, SensorMessageType.Control, "nvidia")
                ?? PickSensor(sensors, SensorMessageType.UsagePercent, "gpu");
            var gpuFanPct = gpuFanPctSensor?.Value;

            var nvidiaGpu = QueryNvidiaSmi();
            if (nvidiaGpu is not null)
            {
                gpuCore = nvidiaGpu.Value.Temp ?? gpuCore;
                gpuFanPct = nvidiaGpu.Value.FanPct ?? gpuFanPct;
            }

            var boardFanPrefix = config.BridgeBoardFanIdentifierPrefix ?? "/lpc/nct6796dr/fan/";
            var boardFans = Enumerable.Range(0, 7)
                .Select(i => PickByIdentifier(sensors, SensorMessageType.Rpm, $"{boardFanPrefix}{i}"))
                .ToArray();

            if (boardFans.All(f => !f.HasValue))
            {
                var otherRpmSensors = sensors
                    .Where(s => s.Type == SensorMessageType.Rpm)
                    .Where(s => s.Identifier != cpuFanSensor?.Identifier && s.Identifier != gpuFanSensor?.Identifier)
                    .Take(7)
                    .ToList();

                for (int i = 0; i < 7; i++)
                {
                    if (i < otherRpmSensors.Count)
                    {
                        boardFans[i] = otherRpmSensors[i].Value;
                    }
                }
            }

            var psuFan = Pick(sensors, SensorMessageType.Rpm, "psu")
                ?? Pick(sensors, SensorMessageType.Rpm, "power supply");
            var network = QueryNetworkRates(networkPrevious, ref networkPreviousAt, config);

        var content = new StringBuilder()
            .AppendLine($"CPU={Round(cpuTemp)}")
            .AppendLine($"GPUCore={Round(gpuCore)}")
            .AppendLine($"GPUHotspot={Round(gpuHotspot)}")
            .AppendLine($"GPUMemory={Round(gpuMemory)}")
            .AppendLine($"GPUFan={Round(gpuFan)}")
            .AppendLine($"GPUFanPct={Round(gpuFanPct)}")
            .AppendLine($"CPUFan={Round(cpuFan)}")
            .AppendLine($"BoardFan1={Round(boardFans[0])}")
            .AppendLine($"BoardFan2={Round(boardFans[1])}")
            .AppendLine($"BoardFan3={Round(boardFans[2])}")
            .AppendLine($"BoardFan4={Round(boardFans[3])}")
            .AppendLine($"BoardFan5={Round(boardFans[4])}")
            .AppendLine($"BoardFan6={Round(boardFans[5])}")
            .AppendLine($"BoardFan7={Round(boardFans[6])}")
            .AppendLine($"PSUFan={Round(psuFan)}")
            .AppendLine($"NetEthInMbps={network.EthInMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"NetEthOutMbps={network.EthOutMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"NetWifiInMbps={network.WifiInMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"NetWifiOutMbps={network.WifiOutMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"NetWifiApInMbps={network.WifiApInMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"NetWifiApOutMbps={network.WifiApOutMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"NetWifiActiveMode={network.WifiActiveMode}")
            .AppendLine($"NetWifiActiveInMbps={network.WifiActiveInMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"NetWifiActiveOutMbps={network.WifiActiveOutMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"NetWifiActiveDlMbps={network.WifiActiveDlMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"NetWifiActiveUlMbps={network.WifiActiveUlMbps.ToString("0.0", CultureInfo.InvariantCulture)}")
            .AppendLine($"BridgeSource=FanControl{(nvidiaGpu is null ? "" : "+NvidiaSmi")}")
            .ToString();

        File.WriteAllText(outFile, content, Encoding.ASCII);
        Console.Write(content);
    }
    catch (Exception ex)
    {
        try
        {
            if (sensorClient is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch { }
        sensorClient = null;

        if (DateTime.UtcNow - lastErrorLog > TimeSpan.FromMinutes(1))
        {
            File.AppendAllText(Path.Combine(root, "CodexBridge.error.log"), $"{DateTime.Now:u} {ex}\n");
            lastErrorLog = DateTime.UtcNow;
        }

        TryWriteNvidiaFallback(outFile);
    }

    if (onceMode)
    {
        return;
    }

    await Task.Delay(TimeSpan.FromSeconds(config.BridgeUpdateSeconds ?? 1));
}
while (true);

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

static float? Pick(IEnumerable<SensorMessage> sensors, SensorMessageType type, params string[] needles)
{
    return sensors
        .Where(s => s.Type == type)
        .Where(s =>
        {
            var text = $"{s.Name} {s.Identifier} {s.Origin}".ToLowerInvariant();
            return needles.All(n => text.Contains(n.ToLowerInvariant()));
        })
        .Select(s => (float?)s.Value)
        .FirstOrDefault();
}

static SensorMessage? PickSensor(IEnumerable<SensorMessage> sensors, SensorMessageType type, params string[] needles)
{
    return sensors
        .Where(s => s.Type == type)
        .Where(s =>
        {
            var text = $"{s.Name} {s.Identifier} {s.Origin}".ToLowerInvariant();
            return needles.All(n => text.Contains(n.ToLowerInvariant()));
        })
        .FirstOrDefault();
}

static float? PickByIdentifier(IEnumerable<SensorMessage> sensors, SensorMessageType type, string identifier)
{
    return sensors
        .Where(s => s.Type == type)
        .Where(s => string.Equals(s.Identifier, identifier, StringComparison.OrdinalIgnoreCase))
        .Select(s => (float?)s.Value)
        .FirstOrDefault();
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
        existing["GPUFan"] = "0";
        existing["GPUFanPct"] = Round(gpu.Value.FanPct);
        existing["BridgeSource"] = "NvidiaSmiFallback";

        var content = new StringBuilder()
            .AppendLine($"CPU={Get(existing, "CPU")}")
            .AppendLine($"GPUCore={Get(existing, "GPUCore")}")
            .AppendLine($"GPUHotspot={Get(existing, "GPUHotspot")}")
            .AppendLine($"GPUMemory={Get(existing, "GPUMemory")}")
            .AppendLine($"GPUFan={Get(existing, "GPUFan")}")
            .AppendLine($"GPUFanPct={Get(existing, "GPUFanPct")}")
            .AppendLine($"CPUFan={Get(existing, "CPUFan")}")
            .AppendLine($"BoardFan1={Get(existing, "BoardFan1")}")
            .AppendLine($"BoardFan2={Get(existing, "BoardFan2")}")
            .AppendLine($"BoardFan3={Get(existing, "BoardFan3")}")
            .AppendLine($"BoardFan4={Get(existing, "BoardFan4")}")
            .AppendLine($"BoardFan5={Get(existing, "BoardFan5")}")
            .AppendLine($"BoardFan6={Get(existing, "BoardFan6")}")
            .AppendLine($"BoardFan7={Get(existing, "BoardFan7")}")
            .AppendLine($"PSUFan={Get(existing, "PSUFan")}")
            .AppendLine($"NetEthInMbps={Get(existing, "NetEthInMbps")}")
            .AppendLine($"NetEthOutMbps={Get(existing, "NetEthOutMbps")}")
            .AppendLine($"NetWifiInMbps={Get(existing, "NetWifiInMbps")}")
            .AppendLine($"NetWifiOutMbps={Get(existing, "NetWifiOutMbps")}")
            .AppendLine($"NetWifiApInMbps={Get(existing, "NetWifiApInMbps")}")
            .AppendLine($"NetWifiApOutMbps={Get(existing, "NetWifiApOutMbps")}")
            .AppendLine($"NetWifiActiveMode={Get(existing, "NetWifiActiveMode")}")
            .AppendLine($"NetWifiActiveInMbps={Get(existing, "NetWifiActiveInMbps")}")
            .AppendLine($"NetWifiActiveOutMbps={Get(existing, "NetWifiActiveOutMbps")}")
            .AppendLine($"NetWifiActiveDlMbps={Get(existing, "NetWifiActiveDlMbps")}")
            .AppendLine($"NetWifiActiveUlMbps={Get(existing, "NetWifiActiveUlMbps")}")
            .AppendLine($"BridgeSource={Get(existing, "BridgeSource")}")
            .ToString();

        File.WriteAllText(outFile, content, Encoding.ASCII);
    }
    catch
    {
        // The bridge should keep retrying FanControl even if the fallback fails.
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

static (float? Temp, float? FanPct)? QueryNvidiaSmi()
{
    var psi = new ProcessStartInfo
    {
        FileName = "nvidia-smi.exe",
        Arguments = "--query-gpu=temperature.gpu,fan.speed --format=csv,noheader,nounits",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process is null)
    {
        return null;
    }

    var output = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit(3000);
    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
    {
        return null;
    }

    var parts = output.Split(',', StringSplitOptions.TrimEntries);
    if (parts.Length < 2)
    {
        return null;
    }

    return (ParseFloat(parts[0]), ParseFloat(parts[1]));
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

    var ignoreList = config.NetworkIgnoreAdapters ?? new List<string> { "hyper-v", "virtual switch", "wsl", "teredo", "wan miniport", "bluetooth" };
    var wifiApList = config.NetworkWifiApNames ?? new List<string> { "wi-fi direct", "wifi direct" };
    var wifiList = config.NetworkWifiNames ?? new List<string> { "wi-fi", "wifi", "wireless" };
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
