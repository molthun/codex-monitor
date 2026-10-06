using System.Globalization;
using CodexBridge;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

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
