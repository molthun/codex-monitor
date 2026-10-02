using System.Globalization;
using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;

namespace CodexBridge;

/// <summary>
/// Picks what the widget shows from all LibreHardwareMonitor sensors (GPU, fans, temperatures)
/// and describes this PC's hardware for the settings window (inventory.json).
/// </summary>
static class HardwareSensors
{
    static bool IsGpu(HardwareType type) => type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    // Fan sources that are not a graphics card: board chips, AIO coolers, PSUs, fan/RGB controllers.
    static bool IsCoolingFan(SimpleSensor s) => s.Type == SensorType.Fan && !IsGpu(s.HardwareType);

    public sealed record Gpu(string Id, string Name, HardwareType Type, double? MemoryMB, bool Integrated);

    /// <summary>
    /// GPUs with their dedicated memory; integrated ones (Intel without dedicated memory, AMD APUs
    /// with a small carve-out) are marked so the settings can say so. Names lose "(R)"/"(TM)".
    /// </summary>
    public static List<Gpu> Gpus(IEnumerable<SimpleSensor> sensors) =>
        sensors.Where(s => IsGpu(s.HardwareType))
            .GroupBy(s => s.HardwareIdentifier)
            .Select(g =>
            {
                var memory = g.FirstOrDefault(s => s.Type == SensorType.SmallData &&
                    (s.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase) ||
                     s.Name.Equals("D3D Dedicated Memory Total", StringComparison.OrdinalIgnoreCase)))?.Value;
                var type = g.First().HardwareType;
                var integrated = type != HardwareType.GpuNvidia && (memory ?? 0) < 2048;
                var name = System.Text.RegularExpressions.Regex.Replace(g.First().HardwareName, @"\s*\((R|TM)\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return new Gpu(g.Key, name, type, memory, integrated);
            })
            .ToList();

    /// <summary>The configured GPU; "auto" prefers NVIDIA, then the AMD card with the most memory, then Intel.</summary>
    public static Gpu? PickGpu(IReadOnlyList<SimpleSensor> sensors, string device)
    {
        var gpus = Gpus(sensors);
        if (device == "none")
        {
            return null;
        }
        if (device != "auto")
        {
            return gpus.FirstOrDefault(g => g.Id == device);
        }
        return gpus.OrderByDescending(g => g.Type switch { HardwareType.GpuNvidia => 3, HardwareType.GpuAmd => 2, _ => 1 })
            .ThenByDescending(g => g.MemoryMB ?? 0)
            .FirstOrDefault();
    }

    /// <summary>VRAM in MB: "GPU Memory Used/Total" (NVIDIA, AMD), "D3D Dedicated Memory Used/Total" (Intel).</summary>
    public static (float? Used, float? Total) Vram(IEnumerable<SimpleSensor> gpuSensors)
    {
        var data = gpuSensors.Where(s => s.Type == SensorType.SmallData).ToList();
        float? Find(string what) =>
            (data.FirstOrDefault(s => s.Name.Equals($"GPU Memory {what}", StringComparison.OrdinalIgnoreCase))
             ?? data.FirstOrDefault(s => s.Name.Equals($"D3D Dedicated Memory {what}", StringComparison.OrdinalIgnoreCase)))?.Value;
        return (Find("Used"), Find("Total"));
    }

    /// <summary>
    /// The fans to show when the user has not chosen any: the CPU fan, then every other cooling fan
    /// that spins right now, named by LibreHardwareMonitor ("Fan #2", "Pump").
    /// </summary>
    public static List<FanEntry> DefaultFans(IReadOnlyList<SimpleSensor> sensors)
    {
        var fans = sensors.Where(IsCoolingFan).ToList();
        var cpu = fans.FirstOrDefault(s => s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)) ?? fans.FirstOrDefault();
        var result = new List<FanEntry>();
        if (cpu is not null)
        {
            result.Add(new FanEntry(cpu.Identifier, "CPU cooler", true));
        }
        result.AddRange(fans.Where(s => s != cpu && (s.Value ?? 0) > 0)
            .Select(s => new FanEntry(s.Identifier, s.Name, s.Name.Contains("Pump", StringComparison.OrdinalIgnoreCase))));
        return result;
    }

    static string Number(float? value) => value.HasValue ? Math.Round(value.Value, 1).ToString(CultureInfo.InvariantCulture) : "-1";

    /// <summary>temps.txt values for the configured fans and temperatures; -1 = sensor not found.</summary>
    public static Dictionary<string, string> ListValues(IReadOnlyList<SimpleSensor> sensors, IReadOnlyList<FanEntry> fans,
        IReadOnlyList<TempEntry> temps)
    {
        var byId = sensors.GroupBy(s => s.Identifier).ToDictionary(g => g.Key, g => g.First());
        var values = new Dictionary<string, string>();
        for (var i = 0; i < fans.Count; i++)
        {
            values[$"Fan{i + 1}"] = Number(byId.TryGetValue(fans[i].Id, out var s) ? s.Value : null);
        }
        for (var i = 0; i < temps.Count; i++)
        {
            values[$"Temp{i + 1}"] = Number(byId.TryGetValue(temps[i].Id, out var s) ? s.Value : null);
        }
        return values;
    }

    /// <summary>
    /// What this PC has, for the settings window, in the same shape as the Linux bridge's
    /// inventory.json. Sensor ids are LibreHardwareMonitor identifiers ("/lpc/nct6798d/fan/1").
    /// </summary>
    public static JsonObject Inventory(IReadOnlyList<SimpleSensor> sensors, IReadOnlyList<FanEntry> fanList, double? linkMbps,
        JsonObject status)
    {
        JsonArray Chips(Func<SimpleSensor, bool> pick, string key) => new(sensors.Where(pick)
            .GroupBy(s => (s.HardwareIdentifier, s.HardwareName))
            .Select(g => (JsonNode)new JsonObject
            {
                ["name"] = g.Key.HardwareName,
                ["id"] = g.Key.HardwareIdentifier,
                [key] = new JsonObject(g.Select(s => KeyValuePair.Create(s.Identifier, (JsonNode?)Math.Round(s.Value ?? 0, 1)))),
                ["labels"] = new JsonObject(g.Select(s => KeyValuePair.Create(s.Identifier, (JsonNode?)s.Name))),
            }).ToArray());

        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => (JsonNode)new JsonObject
        {
            ["mount"] = d.Name.TrimEnd('\\'),
            ["device"] = d.VolumeLabel,
            ["fs"] = d.DriveFormat,
            ["sizeB"] = d.TotalSize,
        });

        return new JsonObject
        {
            ["gpus"] = new JsonArray(Gpus(sensors).Select(g => (JsonNode)new JsonObject
            {
                ["id"] = g.Id,
                ["name"] = g.Name,
                ["driver"] = g.Type.ToString(),
                ["memoryMB"] = g.MemoryMB,
                ["integrated"] = g.Integrated,
            }).ToArray()),
            // What "Automatic" picks, so the settings can name it.
            ["autoGpu"] = PickGpu(sensors, "auto")?.Id,
            ["fanChips"] = Chips(IsCoolingFan, "fans"),
            ["tempChips"] = Chips(s => s.Type == SensorType.Temperature, "temps"),
            ["fanList"] = new JsonArray(fanList.Select(f => (JsonNode)new JsonObject
            {
                ["id"] = f.Id,
                ["name"] = f.Name,
                ["warn"] = f.Warn,
            }).ToArray()),
            ["mounts"] = new JsonArray(drives.ToArray()),
            ["linkMbps"] = linkMbps,
            ["plugins"] = new JsonArray(),
            ["status"] = status,
            ["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    /// <summary>
    /// Why sensors may be missing, for the settings window: CPU temperatures and board fans need
    /// administrator rights and the PawnIO driver (LibreHardwareMonitor 0.9.5+ no longer ships WinRing0;
    /// it finds PawnIO by the same Uninstall key).
    /// </summary>
    /// <summary>
    /// The virtual machine's name ("Parallels ARM Virtual Machine") when Windows runs in one: VMs have
    /// no temperature or fan sensors at all. Read once; Win32_ComputerSystem names the hypervisor.
    /// </summary>
    static readonly Lazy<string?> VirtualMachine = new(() =>
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var system in searcher.Get())
            {
                var name = $"{system["Manufacturer"]} {system["Model"]}".Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"Virtual|VMware|VirtualBox|Parallels|QEMU|KVM|Xen|innotek",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return system["Model"]?.ToString() is { Length: > 0 } model ? model : name;
                }
            }
        }
        catch
        {
            // No WMI: treat as real hardware.
        }
        return null;
    });

    public static JsonObject Status(IComputer computer, IReadOnlyList<SimpleSensor> sensors, string? openError)
    {
        bool admin;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            admin = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            admin = false;
        }
        bool pawnIo;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            pawnIo = key is not null;
        }
        catch
        {
            pawnIo = false;
        }
        return new JsonObject
        {
            ["admin"] = admin,
            ["pawnIO"] = pawnIo,
            ["devices"] = new JsonArray(computer.Hardware.Select(h => (JsonNode)$"{h.HardwareType}: {h.Name}").ToArray()),
            ["cpuTemp"] = sensors.Any(s => s.HardwareType == HardwareType.Cpu && s.Type == SensorType.Temperature && s.Value.HasValue),
            ["boardFans"] = sensors.Any(IsCoolingFan),
            ["virtualMachine"] = VirtualMachine.Value,
            ["error"] = openError,
        };
    }
}
