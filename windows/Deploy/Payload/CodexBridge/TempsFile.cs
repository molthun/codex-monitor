using System.Text;

namespace CodexBridge;

/// <summary>
/// The key order of temps.txt. The Rainmeter skin reads the file with one RegExp built from the
/// same list (SkinBuilder), so the bridge and the skin can never disagree about field numbers.
/// The list depends on the config: one Fan{n} per configured fan, one Temp{n} per extra temperature.
/// </summary>
static class TempsFile
{
    /// <summary>Keys every bridge version writes, in their historic order (docs/SENSOR_CONTRACT.md).</summary>
    public static readonly string[] FixedKeys =
    {
        "CPU", "GPUCore", "GPUHotspot", "GPUMemory", "VRAMUsedMB", "VRAMTotalMB", "VRAMPct", "GPUFan", "GPUFanPct",
        "CPUFan", "BoardFan1", "BoardFan2", "BoardFan3", "BoardFan4", "BoardFan5", "BoardFan6", "BoardFan7", "PSUFan",
        "NetEthInMbps", "NetEthOutMbps", "NetWifiInMbps", "NetWifiOutMbps", "NetWifiApInMbps", "NetWifiApOutMbps",
        "NetWifiActiveMode", "NetWifiActiveInMbps", "NetWifiActiveOutMbps", "NetWifiActiveDlMbps", "NetWifiActiveUlMbps",
    };

    public const string LastKey = "BridgeSource";

    public static List<string> Keys(int fanCount, int tempCount) =>
        FixedKeys.Concat(NetPanel.Keys)
            .Concat(Enumerable.Range(1, fanCount).Select(i => $"Fan{i}"))
            .Concat(Enumerable.Range(1, tempCount).Select(i => $"Temp{i}"))
            .ToList();

    /// <summary>The whole file: every key in order (missing ones as "0"), then BridgeSource.</summary>
    public static string Format(IReadOnlyList<string> keys, IReadOnlyDictionary<string, string> values)
    {
        var text = new StringBuilder();
        foreach (var key in keys.Append(LastKey))
        {
            values.TryGetValue(key, out var value);
            // One line per key: the skin's RegExp splits on newlines.
            text.Append(key).Append('=').Append((value ?? "0").Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// The skin's RegExp: capture n is keys[n - 1]. Each value stops at its line end, and extra
    /// Fan{n} lines (the bridge already restarted with a longer list than this skin knows) are
    /// skipped, so a mismatch only affects those rows instead of blanking the whole widget.
    /// </summary>
    public static string RegExp(IReadOnlyList<string> keys)
    {
        var pattern = new StringBuilder("(?i)");
        for (var i = 0; i < keys.Count; i++)
        {
            var isTemp = keys[i].StartsWith("Temp", StringComparison.Ordinal);
            var previousIsTemp = i > 0 && keys[i - 1].StartsWith("Temp", StringComparison.Ordinal);
            if (isTemp && !previousIsTemp)
            {
                pattern.Append(@"(?:Fan\d+=[^\n]*\n)*");
            }
            pattern.Append(keys[i]).Append(@"=([^\n]*)\n");
        }
        return pattern.ToString();
    }
}
