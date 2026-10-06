using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using CodexBridge;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var ethernet = NetworkInterfaceType.Ethernet;
Check(!AdapterFilter.IsServiceAdapter(ethernet, "Ethernet", "Microsoft Hyper-V Network Adapter"), "Hyper-V guest NIC must count");
Check(!AdapterFilter.IsServiceAdapter(ethernet, "Ethernet", "vmxnet3 Ethernet Adapter"), "VMware guest NIC must count");
Check(!AdapterFilter.IsServiceAdapter(ethernet, "Ethernet", "Intel I219-V Ethernet"), "Physical Ethernet must count");
Check(!AdapterFilter.IsServiceAdapter(ethernet, "WSL development LAN", "Realtek PCIe GbE"), "A user label containing WSL must not hide a real card");
Check(!AdapterFilter.IsServiceAdapter(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel Wireless"), "Wi-Fi must count");
foreach (var description in new[] { "Hyper-V Virtual Ethernet Adapter", "VMware Virtual Ethernet Adapter for VMnet8", "VirtualBox Host-Only Ethernet Adapter", "Bluetooth PAN", "WireGuard Tunnel", "TAP-Windows Adapter V9", "OpenVPN Data Channel Offload", "Tailscale", "ZeroTier" })
    Check(AdapterFilter.IsServiceAdapter(ethernet, "test", description), $"Service adapter must be hidden: {description}");
Check(AdapterFilter.IsServiceAdapter(ethernet, "vEthernet (WSL)", "test"), "WSL host switch must be hidden");
Check(AdapterFilter.IsServiceAdapter(NetworkInterfaceType.Loopback, "test", "test"), "Loopback must be hidden");
Check(AdapterFilter.IsServiceAdapter(NetworkInterfaceType.Tunnel, "test", "test"), "Tunnel must be hidden");
var legacy = new[] { "hyper-v", "virtual switch", "wsl", "teredo", "wan miniport", "bluetooth", "my custom card" };
var words = AdapterFilter.UserWords(legacy);
Check(words.SequenceEqual(new[] { "my custom card" }), "Legacy defaults must be migrated while preserving custom words");
Check(AdapterFilter.UserWords(new string?[] { " Hyper-V ", "hyper-v", null, "" }).SequenceEqual(new[] { "Hyper-V" }), "Short explicit custom lists must be preserved and cleaned");
Check(AdapterFilter.IsServiceAdapter(ethernet, "Ethernet", "Microsoft Hyper-V Network Adapter", AdapterFilter.UserWords(new[] { "hyper-v" })), "Explicit custom exclusion must still apply");
Check(!AdapterFilter.ShouldIgnore(ethernet, "vEthernet", "test", " ethernet "), "Explicit roles must override built-in filtering");
Check(AdapterFilter.ShouldIgnore(ethernet, "vEthernet", "test", "Auto"), "Explicit Auto must use default filtering");
Check(AdapterFilter.ShouldIgnore(ethernet, "vEthernet", "test", "unknown"), "Invalid role must not bypass filtering");
Check(AdapterFilter.ShouldIgnore(ethernet, "Ethernet", "Intel", "ignore"), "Ignore role must win for physical adapters");
var savedRoles = new JsonObject { ["Disconnected Wi-Fi"] = "Wi-Fi", ["Hidden tunnel"] = "Ignore", ["ETHERNET"] = "Wi-Fi" };
var changedRoles = AdapterFilter.SaveRoles(savedRoles, new[] { ("Ethernet", "Auto") });
Check(changedRoles.Count == 2 && changedRoles["Disconnected Wi-Fi"]!.GetValue<string>() == "Wi-Fi", "Saving other settings must preserve disconnected roles");
Check(changedRoles["Hidden tunnel"]!.GetValue<string>() == "Ignore", "Saving must preserve hidden roles");
changedRoles = AdapterFilter.SaveRoles(savedRoles, new[] { ("Ethernet", "Ethernet") });
Check(changedRoles.Count == 3 && changedRoles["Ethernet"]!.GetValue<string>() == "Ethernet" && changedRoles["ETHERNET"] is null, "Role changes must replace case variants");
Check(savedRoles["ETHERNET"]!.GetValue<string>() == "Wi-Fi", "Role editing must not mutate the unsaved original");

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
var panel = new NetPanel();
var apps = new Dictionary<string, AppRates> { ["LAN"] = new() { LanDown = 1, LanUp = 2 } };
var result = panel.Build(100, 50, 1000, apps, true, new(), "");
Check(result["NetLanDownMbps"] == "1.0" && result["NetWanDownMbps"] == "99.0", "UDP must not inflate LAN download");
Check(result["NetLanUpMbps"] == "2.0" && result["NetWanUpMbps"] == "48.0", "UDP must not inflate LAN upload");
apps["LAN"].LanDown = 100;
apps["WAN"] = new() { WanDown = 100, WanUp = 0 };
result = panel.Build(100, 0, 1000, apps, true, new(), "");
Check(result["NetLanDownMbps"] == "50.0" && result["NetWanDownMbps"] == "50.0", "Counters exceeding totals must be scaled down");
result = panel.Build(100, 50, 1000, new(), true, new(), "");
Check(result["NetLanDownMbps"] == "0.0" && result["NetWanDownMbps"] == "100.0", "Unattributed traffic must remain Internet");

var telemetry = "NVIDIA A, 30, 0, 100, 4000\r\nNVIDIA B, 70, 50, 2000, 8000\r\n";
var gpu = NvidiaTelemetry.Parse(telemetry, "NVIDIA B");
Check(gpu is { Temp: 70, VramUsedMb: 2000, VramTotalMb: 8000 }, "Selected GPU must use its own row");
Check(NvidiaTelemetry.Parse(telemetry, "NVIDIA C") is null, "Missing GPU must not use the first row");
Check(NvidiaTelemetry.Parse(telemetry + "NVIDIA B, 50, 0, 50, 8000\n", "NVIDIA B") is null, "Identical models must retain LHM readings");
Check(NvidiaTelemetry.Parse("NVIDIA B, N/A, N/A, 2000, 8000", "NVIDIA B") is { Temp: null, FanPct: null }, "N/A must remain unavailable");

var temp = Path.Combine(Path.GetTempPath(), "codex-tests-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
try
{
    var config = AppConfig.Load(Path.Combine(temp, "config.json"));
    config.Fans = new() { new("fan/1", "CPU cooler", true) };
    config.Temps = new() { new("temp/1", "Water", 40, 50) };
    config.Save();
    var saved = AppConfig.Load(config.Path);
    var (skin, width, height, scale) = SkinBuilder.Build(saved, saved.Fans!, 1080, "CodexBridge.exe");
    Check(width > 0 && height > 0 && height <= 984 && scale > 0, "Skin must fit the screen");
    Check(skin.Contains("Water") && skin.Contains("CPU cooler"), "Configured sensors must appear in the skin");
    var keys = TempsFile.Keys(1, 1);
    Check(keys.Contains("Fan1") && keys.Contains("Temp1"), "Bridge and skin must share sensor keys");
}
finally { Directory.Delete(temp, true); }
Console.WriteLine("C# regression checks passed.");
