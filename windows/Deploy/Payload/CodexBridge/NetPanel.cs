using System.Globalization;

namespace CodexBridge;

static class AppIcons
{
    public const string Blank = @"AppIcons\_blank.png";
    public const string Default = @"AppIcons\_app.png";
}

sealed class AppRates
{
    public double WanDown, WanUp, LanDown, LanUp;
    public string Icon = AppIcons.Default;
    public double Total => WanDown + WanUp + LanDown + LanUp;
}

sealed class NetPanelConfig
{
    public double InternetDownMbps { get; set; }
    public double InternetUpMbps { get; set; }
    public double LanMbps { get; set; }
}

/// <summary>
/// Computes everything the Rainmeter network panel shows: Internet/LAN split bars, the plan tick,
/// the mirrored graph scale and the top applications, as ready-to-draw percentages and text.
/// </summary>
sealed class NetPanel
{
    // Order is the temps.txt order; the skin's RegExp depends on it.
    public static readonly string[] Keys =
    {
        "NetLinkMbps", "NetSplitMode", "NetDownMbps", "NetUpMbps",
        "NetWanDownMbps", "NetLanDownMbps", "NetWanUpMbps", "NetLanUpMbps",
        "NetTotalText", "NetDownWanText", "NetDownLanText", "NetUpWanText", "NetUpLanText",
        "NetDownWanPct", "NetDownTotalPct", "NetUpWanPct", "NetUpTotalPct",
        "NetPlanDownPct", "NetPlanUpPct", "NetDownMaxed", "NetUpMaxed",
        "NetGraphDownWan", "NetGraphDownTotal", "NetGraphUpWan", "NetGraphUpTotal",
        "NetGraphPlanDown", "NetGraphPlanUp", "NetGraphScaleText",
        "NetTop1Name", "NetTop1Icon", "NetTop1Text", "NetTop1Scope",
        "NetTop2Name", "NetTop2Icon", "NetTop2Text", "NetTop2Scope",
        "NetTop3Name", "NetTop3Icon", "NetTop3Text", "NetTop3Scope",
        "NetTop4Name", "NetTop4Icon", "NetTop4Text", "NetTop4Scope",
        "NetTop5Name", "NetTop5Icon", "NetTop5Text", "NetTop5Scope",
        "UpdateAvailable",
    };

    /// <summary>Rows the bridge reports; the skin shows widget.topProcesses of them.</summary>
    public const int TopRows = 5;

    const int GraphPoints = 60;
    const double Saturated = 0.9;
    static readonly double[] NiceScales = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 2500, 5000, 10000, 25000, 40000, 100000 };

    readonly Queue<double> _peaks = new();

    public Dictionary<string, string> Build(double down, double up, double? linkMbps, Dictionary<string, AppRates> apps,
        bool appsOk, NetPanelConfig config, string updateTag)
    {
        // Keep measured LAN TCP; scale down only when TCP counters exceed interface totals.
        // Unattributed traffic counts as Internet instead of inflating LAN.
        var tcp = new[] { apps.Values.Sum(a => a.WanDown), apps.Values.Sum(a => a.WanUp), apps.Values.Sum(a => a.LanDown), apps.Values.Sum(a => a.LanUp) };
        var lanDown = tcp[0] + tcp[2] > 0 ? tcp[2] * Math.Min(1, down / (tcp[0] + tcp[2])) : 0;
        var lanUp = tcp[1] + tcp[3] > 0 ? tcp[3] * Math.Min(1, up / (tcp[1] + tcp[3])) : 0;
        var wanDown = down - lanDown;
        var wanUp = up - lanUp;
        var split = appsOk;

        var link = config.LanMbps > 0 ? config.LanMbps : linkMbps is > 0 ? linkMbps.Value : 1000;
        var planDown = config.InternetDownMbps;
        var planUp = config.InternetUpMbps;
        var downMaxed = split && planDown > 0 && wanDown >= planDown * Saturated;
        var upMaxed = split && planUp > 0 && wanUp >= planUp * Saturated;

        _peaks.Enqueue(Math.Max(down, up));
        while (_peaks.Count > GraphPoints)
        {
            _peaks.Dequeue();
        }
        var scale = NiceScale(_peaks.Max(), new[] { planDown, planUp, link });

        var values = new Dictionary<string, string>
        {
            ["NetLinkMbps"] = Num(link),
            ["NetSplitMode"] = split ? "estimate" : "none",
            ["NetDownMbps"] = Num(down),
            ["NetUpMbps"] = Num(up),
            ["NetWanDownMbps"] = Num(wanDown),
            ["NetLanDownMbps"] = Num(lanDown),
            ["NetWanUpMbps"] = Num(wanUp),
            ["NetLanUpMbps"] = Num(lanUp),
            ["NetTotalText"] = $"↓ {Rate(down)}  ↑ {Rate(up)}",
            ["NetDownWanText"] = split ? $"Internet {Rate(wanDown)}" : Rate(down),
            ["NetDownLanText"] = split ? $"LAN {Rate(lanDown)}" : "",
            ["NetUpWanText"] = split ? $"Internet {Rate(wanUp)}" : Rate(up),
            ["NetUpLanText"] = split ? $"LAN {Rate(lanUp)}" : "",
            ["NetDownWanPct"] = Pct(wanDown, link),
            ["NetDownTotalPct"] = Pct(down, link),
            ["NetUpWanPct"] = Pct(wanUp, link),
            ["NetUpTotalPct"] = Pct(up, link),
            ["NetPlanDownPct"] = planDown > 0 && planDown < link ? Pct(planDown, link) : "0",
            ["NetPlanUpPct"] = planUp > 0 && planUp < link ? Pct(planUp, link) : "0",
            ["NetDownMaxed"] = downMaxed ? "1" : "0",
            ["NetUpMaxed"] = upMaxed ? "1" : "0",
            ["NetGraphDownWan"] = Pct(wanDown, scale),
            ["NetGraphDownTotal"] = Pct(down, scale),
            ["NetGraphUpWan"] = Pct(wanUp, scale),
            ["NetGraphUpTotal"] = Pct(up, scale),
            ["NetGraphPlanDown"] = planDown > 0 && planDown < scale ? Pct(planDown, scale) : "0",
            ["NetGraphPlanUp"] = planUp > 0 && planUp < scale ? Pct(planUp, scale) : "0",
            ["NetGraphScaleText"] = $"{Rate(scale)}{(split ? " · est." : "")}",
            ["UpdateAvailable"] = updateTag,
        };

        var rows = apps.Select(a => (Name: a.Key, a.Value.Icon, Down: a.Value.WanDown + a.Value.LanDown,
                Up: a.Value.WanUp + a.Value.LanUp, Scope: a.Value.LanDown + a.Value.LanUp > a.Value.WanDown + a.Value.WanUp ? "lan" : "wan"))
            .Where(r => r.Down + r.Up >= 0.05)
            .ToList();
        // UDP (QUIC, games) has no per-connection counters; show it when it is a real share of the traffic.
        var otherDown = Math.Max(down - tcp[0] - tcp[2], 0);
        var otherUp = Math.Max(up - tcp[1] - tcp[3], 0);
        if (split && otherDown + otherUp >= 1 && otherDown + otherUp > 0.2 * (down + up))
        {
            rows.Add(("UDP / other", AppIcons.Default, otherDown, otherUp, "other"));
        }
        rows = rows.OrderByDescending(r => r.Down + r.Up).Take(TopRows).ToList();
        for (var i = 0; i < TopRows; i++)
        {
            var n = i + 1;
            var row = i < rows.Count ? rows[i] : default;
            var has = i < rows.Count;
            values[$"NetTop{n}Name"] = has ? Clean(row.Name)
                : i > 0 ? "" : split ? "No active transfers" : "Per-app traffic needs the elevated bridge task";
            values[$"NetTop{n}Icon"] = has ? row.Icon : AppIcons.Blank;
            values[$"NetTop{n}Text"] = has ? $"↓ {Rate(row.Down)}  ↑ {Rate(row.Up)}" : "";
            values[$"NetTop{n}Scope"] = has ? row.Scope : "none";
        }
        return values;
    }

    static double NiceScale(double peak, double[] extra)
    {
        var limit = extra.Max();
        var steps = NiceScales.Concat(extra.Where(v => v > 0))
            .Where(v => peak > limit || v <= limit)
            .OrderBy(v => v)
            .ToList();
        return steps.FirstOrDefault(v => v >= peak * 1.05, steps[^1]);
    }

    public static string Rate(double mbps)
    {
        if (!(mbps > 0))
        {
            return "0 Mbps";
        }
        if (mbps < 1)
        {
            return $"{Math.Round(mbps * 1000).ToString(CultureInfo.InvariantCulture)} Kbps";
        }
        if (mbps < 1000)
        {
            return $"{mbps.ToString(mbps < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} Mbps";
        }
        return $"{(mbps / 1000).ToString("0.00", CultureInfo.InvariantCulture)} Gbps";
    }

    static string Num(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    static string Pct(double value, double full) =>
        (full > 0 ? Math.Clamp(value / full * 100, 0, 100) : 0).ToString("0.0", CultureInfo.InvariantCulture);

    // temps.txt is line based: keep names on one line.
    static string Clean(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
