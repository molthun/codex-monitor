using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexBridge;

/// <summary>
/// Which sensors this PC has, as the bridge found them (inventory.json "available"). The skin leaves
/// out rows for the rest; until the bridge has reported, everything is assumed present.
/// </summary>
sealed record Available(bool CpuTemp, bool Gpu, bool GpuTemp, bool Vram, bool GpuFan)
{
    public static readonly Available All = new(true, true, true, true, true);

    /// <summary>Stamped into the skin and written by the bridge: the display watcher rebuilds the skin when they differ.</summary>
    public string Signature(int fanCount) =>
        $"{(CpuTemp ? 1 : 0)}{(Gpu ? 1 : 0)}{(GpuTemp ? 1 : 0)}{(Vram ? 1 : 0)}{(GpuFan ? 1 : 0)}-{fanCount}";

    public JsonObject ToJson() => new()
    {
        ["cpuTemp"] = CpuTemp, ["gpu"] = Gpu, ["gpuTemp"] = GpuTemp, ["vram"] = Vram, ["gpuFan"] = GpuFan,
    };

    public static Available Read(string inventoryPath)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(inventoryPath))?["available"] is JsonObject a)
            {
                bool Flag(string key) => a[key]?.GetValue<bool>() ?? true;
                return new Available(Flag("cpuTemp"), Flag("gpu"), Flag("gpuTemp"), Flag("vram"), Flag("gpuFan"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // No inventory yet: show everything until the bridge reports.
        }
        return All;
    }
}

/// <summary>
/// Generates the Rainmeter skin (CodexMonitor.ini) from the settings, replacing the fixed 1080p/4K
/// presets: any size, only the sections the user wants, 1–6 drives, any number of fans and extra
/// temperatures, and a size that fits the screen height. Layout is computed in 1080p units
/// (430 px wide) and multiplied by the scale, so every size shares one layout.
/// </summary>
sealed class SkinBuilder
{
    // 1080p geometry of the original skin.
    const int Width = 430, Pad = 18, ValueX = 412, BarW = 394, BarH = 7, BarR = 3, RowStep = 33, BarOffset = 19;
    const int SectionGap = 11, GraphH = 40, ProcRow = 16;

    readonly AppConfig _config;
    readonly List<FanEntry> _fans;
    readonly List<TempEntry> _temps;
    readonly List<DiskEntry> _disks;
    readonly int _topRows;
    readonly double _k;
    readonly string _bridgeExe;
    readonly List<string> _keys;
    readonly Available _hw;
    int _screenHeight;
    readonly StringBuilder _measures = new();
    readonly StringBuilder _meters = new();
    int _y;
    int _bottom;

    SkinBuilder(AppConfig config, List<FanEntry> fans, double scale, string bridgeExe, Available hw)
    {
        _config = config;
        _hw = hw;
        _fans = fans;
        _temps = config.Temps;
        _disks = config.Disks;
        _topRows = config.TopProcesses;
        _k = scale;
        _bridgeExe = bridgeExe;
        _keys = TempsFile.Keys(fans.Count, _temps.Count);
    }

    // ------------------------------------------------------------ entry points

    /// <summary>Scale for the screen: a custom percentage, a fixed profile, or by screen height (like Linux).</summary>
    public static double ProfileScale(AppConfig config, int screenHeight)
    {
        if (config.ScalePercent > 0)
        {
            return config.ScalePercent / 100;
        }
        return config.Profile.ToLowerInvariant() switch
        {
            "1080p" => 1,
            "2k" => 540.0 / 430,
            "4k" => 720.0 / 430,
            _ => screenHeight >= 2000 ? 720.0 / 430 : screenHeight >= 1400 ? 540.0 / 430 : 1,
        };
    }

    /// <summary>
    /// The fans the skin shows: the configured list, else the defaults the bridge picked (it writes
    /// them to inventory.json), so the skin and temps.txt agree on the Fan{n} keys.
    /// </summary>
    public static List<FanEntry> FanList(AppConfig config, string inventoryPath)
    {
        if (config.Fans is { } fans)
        {
            return fans;
        }
        try
        {
            var inventory = JsonNode.Parse(File.ReadAllText(inventoryPath));
            return (inventory?["fanList"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(f => new FanEntry(f["id"]?.GetValue<string>() ?? "", f["name"]?.GetValue<string>() ?? "",
                    f["warn"]?.GetValue<bool>() ?? true))
                .Where(f => f.Id.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new List<FanEntry>();
        }
    }

    /// <summary>The skin text, shrunk to fit <paramref name="maxHeight"/> pixels when the config asks for it.</summary>
    public static (string Skin, int Width, int Height, double Scale) Build(AppConfig config, List<FanEntry> fans,
        int screenHeight, string bridgeExe, Available? hw = null)
    {
        hw ??= Available.All;
        var scale = ProfileScale(config, screenHeight);
        var maxHeight = screenHeight - 2 * config.MarginTop - 48; // leave room for the taskbar
        var naturalHeight = new SkinBuilder(config, fans, 1, bridgeExe, hw).Layout();
        if (config.FitToScreen && naturalHeight * scale > maxHeight && maxHeight > 200)
        {
            scale = maxHeight / (double)naturalHeight;
        }
        var builder = new SkinBuilder(config, fans, scale, bridgeExe, hw) { _screenHeight = screenHeight };
        var height = builder.Layout();
        return (builder.Text(height), builder.S(Width), builder.S(height), scale);
    }

    // ------------------------------------------------------------ helpers

    int S(double value) => (int)Math.Round(value * _k);
    // Numbers inside Rainmeter formulas: always "0.65", never the Windows locale's "0,65".
    static string D(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    string F(double points) => Math.Max(5, Math.Round(points * _k, 1)).ToString(CultureInfo.InvariantCulture);

    string Measure(string key) => $"MeasureT_{key}";
    int Index(string key) => _keys.IndexOf(key) + 1;

    // Only what this PC has: no GPU rows without a graphics card, no temperatures without sensors.
    bool GpuRows => _hw.Gpu;
    bool VramRow => _hw.Gpu && _hw.Vram;
    bool CpuTempRow => _hw.CpuTemp;
    bool GpuTempRow => _hw.Gpu && _hw.GpuTemp;
    bool GpuFanRow => _hw.Gpu && _hw.GpuFan;
    bool ShowTemps => _config.ShowSection("temperatures") && (CpuTempRow || GpuTempRow || _temps.Count > 0);
    bool ShowCooling => _config.ShowSection("cooling") && (_fans.Count > 0 || GpuFanRow);
    bool HasFans => _fans.Count > 0 || GpuFanRow;
    // RAM alone would repeat the RAM row: the strip needs a temperature or a fan.
    bool ShowHealth => _config.ShowSection("health") && (CpuTempRow || GpuTempRow || HasFans);

    // Text from the config inside skin options: no line breaks, no #variable# or [section] syntax.
    static string Clean(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Replace("#", "＃").Replace("[", "(").Replace("]", ")");

    void Section(StringBuilder target, string name, params string[] options)
    {
        target.Append('[').Append(name).Append("]\r\n");
        foreach (var option in options.Where(o => o.Length > 0))
        {
            target.Append(option).Append("\r\n");
        }
        target.Append("\r\n");
    }

    void Meter(string name, params string[] options) => Section(_meters, name, options);
    void MeasureSection(string name, params string[] options) => Section(_measures, name, options);

    /// <summary>A WebParser child for one temps.txt key.</summary>
    void FileMeasure(string key, params string[] extra) =>
        MeasureSection(Measure(key), new[] { "Measure=WebParser", "URL=[MeasureTempsRaw]", $"StringIndex={Index(key)}" }.Concat(extra).ToArray());

    /// <summary>Section title with icon and rule; separator line above for the lower sections.</summary>
    void SectionHeader(string id, string icon, string tint, string title, bool separator, string rightText = "")
    {
        if (separator)
        {
            Meter($"{id}Separator", "Meter=Shape", $"Shape=Line #Pad#,{S(_y - 9)},#ValueX#,{S(_y - 9)} | StrokeWidth 1 | Stroke Color #Line#");
        }
        Meter($"{id}Icon", "Meter=Image", $"ImageName=#@#Icons\\{icon}", "X=#Pad#", $"Y=({S(_y)} + #IconYOffset#)",
            "W=#IconSize#", "H=#IconSize#", $"ImageTint={tint}", "AntiAlias=1");
        Meter($"{id}Title", "Meter=String", "MeterStyle=StyleSectionTitle", "X=(#Pad# + #IconSize# + #IconGap#)", $"Y={S(_y)}", $"Text={title}");
        // Right-aligned text on the title line (network totals); an empty one keeps the rule's end point.
        // Defined before the rule, which reads its position.
        Meter($"{id}Right", "Meter=String", "MeterStyle=StyleSmall", "X=#ValueX#", $"Y={S(_y)}", "StringAlign=Right",
            rightText.Length > 0 ? rightText : "Text= ", rightText.Length > 0 ? "Text=%1" : "");
        Meter($"{id}Rule", "Meter=Shape",
            $"Shape=Line ([{id}Title:X] + [{id}Title:W] + {S(6)}),{S(_y + 6)},([{id}Right:X] - {S(6)}),{S(_y + 6)} | StrokeWidth 1 | Stroke Color #Line#",
            "DynamicVariables=1");
        _y += 18;
    }

    /// <summary>
    /// A label/value line with a bar under it. <paramref name="fraction"/> is a Rainmeter formula (0..1);
    /// <paramref name="alertVar"/> names a variable that tints the bar and label (transparent when OK).
    /// </summary>
    void Row(string id, string label, string[] valueOptions, string fraction, string gradient, string? alertVar = null,
        string[]? zones = null, string? historyMeasure = null)
    {
        var labelColor = alertVar is null ? "#Muted#" : $"[#{alertVar}Label]";
        Meter($"Label{id}", "Meter=String", "MeterStyle=StyleLabel", $"Y={S(_y)}", $"Text={Clean(label)}",
            $"FontColor={labelColor}", alertVar is null ? "" : "DynamicVariables=1");
        Meter($"Value{id}", new[] { "Meter=String", "MeterStyle=StyleValue", $"Y={S(_y)}" }.Concat(valueOptions).ToArray());
        var shapes = new List<string> { "Shape=Rectangle 0,0,#BarW#,#BarH#,#BarR# | Fill Color #Track# | StrokeWidth 0" };
        foreach (var zone in zones ?? Array.Empty<string>())
        {
            shapes.Add($"Shape{shapes.Count + 1}={zone}");
        }
        var width = $"(#BarW# * Clamp({fraction}, 0, 1))";
        shapes.Add($"Shape{shapes.Count + 1}=Rectangle 0,0,{width},#BarH#,#BarR# | Fill LinearGradient Grad | StrokeWidth 0");
        if (alertVar is not null)
        {
            // Warm/hot: the same bar again in a solid alert color on top of the gradient.
            shapes.Add($"Shape{shapes.Count + 1}=Rectangle 0,0,{width},#BarH#,#BarR# | Fill Color [#{alertVar}] | StrokeWidth 0");
        }
        Meter($"Bar{id}", new[] { "Meter=Shape", "X=#BarX#", $"Y={S(_y + BarOffset)}" }.Concat(shapes)
            .Append($"Grad=90 | {gradient}").Append("DynamicVariables=1").ToArray());
        if (historyMeasure is not null)
        {
            Meter($"Line{id}History", "Meter=Line", "X=#BarX#", $"Y={S(_y + 10)}", "W=#BarW#", $"H={S(18)}",
                $"MeasureName={historyMeasure}", "LineColor=255,255,255,60", "LineWidth=1", "AntiAlias=1", "AutoScale=0");
        }
        _bottom = _y + BarOffset + BarH;
        _y += RowStep;
    }

    /// <summary>Calc measure that sets a state variable pair (bar tint, label color) by thresholds.</summary>
    /// <param name="healthText">health cell text with {0} for OK/WARM/HOT, e.g. "CPU {0} %1°C"</param>
    /// <param name="valueMeter">row value meter that reads "n/a" while the sensor is missing (value &lt; 0)</param>
    void ThresholdState(string id, string value, double warm, double hot, string? health = null, string healthText = "", string okColor = "#OK#",
        string? valueMeter = null, string valueText = "")
    {
        string Bangs(string tint, string label, string word, string color, bool missing = false) =>
            $"[!SetVariable {id} \"{tint}\"][!SetVariable {id}Label \"{label}\"]" +
            (valueMeter is null ? "" : $"[!SetOption {valueMeter} Text \"{(missing ? "n/a" : valueText)}\"]") +
            (health is null ? "" : $"[!SetOption {health} Text \"{HealthText(healthText, word, missing)}\"][!SetOption {health} FontColor \"{color}\"][!SetOption {health}Bar SolidColor \"{color}\"]");
        var w = D(warm);
        var h = D(hot);
        MeasureSection($"State{id}", "Measure=Calc", $"Formula={value}",
            $"IfCondition=({value} < 0)", $"IfTrueAction={Bangs("0,0,0,0", "#Muted#", "N/A", "#Muted#", true)}",
            $"IfCondition2=({value} >= 0) && ({value} < {w})", $"IfTrueAction2={Bangs("0,0,0,0", "#Muted#", "OK", okColor)}",
            $"IfCondition3=({value} >= {w}) && ({value} < {h})", $"IfTrueAction3={Bangs("#Warm#", "#Warm#", "WARM", "#Warm#")}",
            $"IfCondition4=({value} >= {h})", $"IfTrueAction4={Bangs("#Hot#", "#Hot#", "HOT", "#Hot#")}",
            "DynamicVariables=1");
    }

    /// <summary>"CPU {0} %1°C" → "CPU OK %1°C"; a missing sensor drops the value: "CPU N/A".</summary>
    static string HealthText(string text, string word, bool missing) =>
        missing ? text.Substring(0, text.IndexOf("{0}", StringComparison.Ordinal)) + word
            : string.Format(CultureInfo.InvariantCulture, text, word);

    // ------------------------------------------------------------ layout

    int Layout()
    {
        _measures.Clear();
        _meters.Clear();
        _y = 14;
        _bottom = 61;
        Header();
        _y = 74;
        if (ShowHealth)
        {
            Health();
            _bottom = 120;
            _y = 132;
        }
        var first = true;
        if (_config.ShowSection("performance"))
        {
            Performance();
            first = false;
        }
        if (ShowTemps)
        {
            Temperatures(first);
            first = false;
        }
        if (ShowCooling)
        {
            Cooling(first);
            first = false;
        }
        if (_config.ShowSection("network"))
        {
            Network(first);
            first = false;
        }
        if (_config.ShowSection("diskIO") && _disks.Count > 0)
        {
            DiskIO(first);
            first = false;
        }
        if (_config.ShowSection("drives") && _disks.Count > 0)
        {
            Drives(first);
        }
        return _bottom + 14;
    }

    void Header()
    {
        Meter("Title", "Meter=String", "X=#Pad#", $"Y={S(14)}", "FontFace=#Font#", $"FontSize={F(14)}", "FontWeight=600",
            "FontColor=#Text#", "AntiAlias=1", "Text=System Monitor");
        Meter("SubTitle", "Meter=String", "X=#Pad#", $"Y={S(38)}", "FontFace=#Font#", $"FontSize={F(8)}", "FontColor=#Muted#",
            "AntiAlias=1", "Text=Hardware bridge / desktop widget");
        Meter("LineTop", "Meter=Shape", $"Shape=Line #Pad#,{S(61)},#ValueX#,{S(61)} | StrokeWidth 1 | Stroke Color #Line#");
    }

    void Health()
    {
        Meter("HealthBg", "Meter=Shape",
            $"Shape=Rectangle #Pad#,{S(74)},#BarW#,{S(46)},{S(3)} | Fill Color [#HealthFill] | StrokeWidth 1 | Stroke Color 78,205,196,32",
            "DynamicVariables=1");
        var cells = new List<(string Id, string Text, string Color, string Measure)>();
        if (CpuTempRow)
        {
            cells.Add(("HealthCPU", "CPU", "#OK#", Measure("CPU")));
        }
        if (GpuTempRow)
        {
            cells.Add(("HealthGPU", "GPU", "#GPU#", Measure("GPUCore")));
        }
        cells.Add(("HealthRAM", "RAM", "#OK#", "MeasureRAMPct"));
        if (HasFans)
        {
            cells.Add(("HealthFans", "FANS OK", "#OK#", ""));
        }
        // Four cells are 76 px wide, 99 px apart; fewer cells share the same width.
        var step = 396.0 / cells.Count;
        for (var i = 0; i < cells.Count; i++)
        {
            var (id, text, color, measure) = cells[i];
            var cellX = Pad + 12 + i * step;
            Meter(id, "Meter=String", $"X={S(cellX + (step - 23) / 2)}", $"Y={S(80)}", "FontFace=#Font#", $"FontSize={F(9)}", $"FontColor={color}",
                "AntiAlias=1", "StringAlign=Center", measure.Length > 0 ? $"MeasureName={measure}" : "", $"Text={text}");
            Meter($"{id}Bar", "Meter=Image", $"X={S(cellX)}", $"Y={S(104)}", $"W={S(step - 23)}", $"H={S(3)}", $"SolidColor={color}");
        }
    }

    void Performance()
    {
        SectionHeader("Perf", "cpu.png", "#CPU#", "PERFORMANCE", false);
        Row("CPU", "CPU load", new[] { "MeasureName=MeasureCPU", "Text=%1%" }, "[MeasureCPU:] / 100",
            "0,229,255,255 ; 0.0 | 0,145,234,255 ; 1.0", historyMeasure: "MeasureCPU");
        Row("RAM", "RAM used", new[] { "MeasureName=MeasureRAMPct", "Text=%1%" }, "[MeasureRAMPct:] / 100",
            "69,201,151,255 ; 0.0 | 0,230,118,255 ; 1.0", "RAMState");
        if (GpuRows)
        {
            Row("GPU", "GPU load", new[] { "MeasureName=MeasureGPUValue", "Text=%1%" }, "[MeasureGPUValue:] / 100",
                "151,136,255,255 ; 0.0 | 224,195,252,255 ; 1.0", historyMeasure: "MeasureGPUValue");
        }
        if (VramRow)
        {
            Row("VRAM", "VRAM used", new[] { "MeasureName=MeasureVRAMUsedGB", "MeasureName2=MeasureVRAMTotalGB", "Text=%1 GB / %2 GB", "NumOfDecimals=1" },
                $"[{Measure("VRAMPct")}:] / 100", "151,136,255,255 ; 0.0 | 224,195,252,255 ; 1.0");
        }
        _y += SectionGap;
    }

    void Temperatures(bool first)
    {
        SectionHeader("Temp", "temp.png", "#Warn#", "TEMPERATURES", false);
        string[] Zones(double warm, double hot) => new[]
        {
            $"Rectangle (#BarW# * {D(warm / 100)}),0,(#BarW# * {D((hot - warm) / 100)}),#BarH# | Fill Color 255,193,94,30 | StrokeWidth 0",
            $"Rectangle (#BarW# * {D(hot / 100)}),0,(#BarW# * {D(Math.Max(0, 100 - hot) / 100)}),#BarH# | Fill Color 255,113,113,38 | StrokeWidth 0",
        };
        if (CpuTempRow)
        {
            Row("CPUTemp", "CPU temp", new[] { $"MeasureName={Measure("CPU")}", "Text=%1°C" }, $"[{Measure("CPU")}:] / 100",
                "0,229,255,255 ; 0.0 | 0,145,234,255 ; 1.0", "CPUState", Zones(65, 80), Measure("CPU"));
        }
        if (GpuTempRow)
        {
            Row("GPUTemp", "GPU temp", new[] { $"MeasureName={Measure("GPUCore")}", "Text=%1°C" }, $"[{Measure("GPUCore")}:] / 100",
                "0,229,255,255 ; 0.0 | 0,145,234,255 ; 1.0", "GPUState", Zones(70, 83), Measure("GPUCore"));
        }
        for (var i = 0; i < _temps.Count; i++)
        {
            var t = _temps[i];
            var key = $"Temp{i + 1}";
            Row(key, t.Name, new[] { $"MeasureName={Measure(key)}", "Text=%1°C", "NumOfDecimals=0" }, $"[{Measure(key)}:] / 100",
                "0,229,255,255 ; 0.0 | 0,145,234,255 ; 1.0", $"{key}State", Zones(t.Warm, t.Hot));
        }
        _y += SectionGap;
    }

    void Cooling(bool first)
    {
        SectionHeader("Cooling", "fan.png", "#CPU#", "COOLING", false);
        for (var i = 0; i < _fans.Count; i++)
        {
            var key = $"Fan{i + 1}";
            // Pumps spin far faster than fans: each bar scales to the fastest speed seen for it.
            Row(key, _fans[i].Name, new[] { $"MeasureName={Measure(key)}", "Text=%1 RPM", "NumOfDecimals=0" },
                $"[{Measure(key)}:] / [Measure{key}Peak:]", "0,229,255,255 ; 0.0 | 0,145,234,255 ; 1.0", $"{key}State");
        }
        if (GpuFanRow)
        {
            Row("GPUFan", "GPU fans", new[] { $"MeasureName={Measure("GPUFan")}", $"MeasureName2={Measure("GPUFanPct")}", "Text=%1 RPM / %2%" },
                $"[{Measure("GPUFanPct")}:] / 100", "151,136,255,255 ; 0.0 | 224,195,252,255 ; 1.0", "GPUFanState");
        }
        _y += SectionGap;
    }

    void Network(bool first)
    {
        SectionHeader("Net", "net.png", "#NET#", "NETWORK TRAFFIC", !first, $"MeasureName={Measure("NetTotalText")}");
        var g = _y;
        const int half = GraphH / 2;
        Meter("NetGraphBg", "Meter=Shape", $"Shape=Rectangle #BarX#,{S(g)},#BarW#,{S(GraphH)},2 | Fill Color 255,255,255,8 | StrokeWidth 0",
            $"Shape2=Line #BarX#,{S(g + half)},#ValueX#,{S(g + half)} | StrokeWidth 1 | Stroke Color 255,255,255,30");
        // Mirrored, stacked graph: download up from the middle, upload down. Primary = Internet,
        // Secondary = Internet + LAN: the overlap (BothColor) is Internet, the rest LAN.
        Meter("NetGraphDown", "Meter=Histogram", "X=#BarX#", $"Y={S(g)}", "W=#BarW#", $"H={S(half)}",
            $"MeasureName={Measure("NetGraphDownWan")}", $"MeasureName2={Measure("NetGraphDownTotal")}",
            "PrimaryColor=#WANFill#", "SecondaryColor=#LANFill#", "BothColor=#WANFill#", "AutoScale=0", "AntiAlias=1");
        Meter("NetGraphUp", "Meter=Histogram", "X=#BarX#", $"Y={S(g + half)}", "W=#BarW#", $"H={S(half)}",
            $"MeasureName={Measure("NetGraphUpWan")}", $"MeasureName2={Measure("NetGraphUpTotal")}",
            "PrimaryColor=#WANFill#", "SecondaryColor=#LANFill#", "BothColor=#WANFill#", "AutoScale=0", "Flip=1", "AntiAlias=1");
        // Internet plan, once LAN traffic pushes the scale past it.
        Meter("NetGraphPlanDown", "Meter=Shape", "X=#BarX#", $"Y={S(g)}",
            $"Shape=Line 0,({S(half)} - {S(half)} * [{Measure("NetGraphPlanDown")}:] / 100),#BarW#,({S(half)} - {S(half)} * [{Measure("NetGraphPlanDown")}:] / 100) | StrokeWidth 1 | Stroke Color 255,255,255,80 | StrokeDashes 3,3",
            "DynamicVariables=1", "Hidden=1");
        Meter("NetGraphPlanUp", "Meter=Shape", "X=#BarX#", $"Y={S(g + half)}",
            $"Shape=Line 0,({S(half)} * [{Measure("NetGraphPlanUp")}:] / 100),#BarW#,({S(half)} * [{Measure("NetGraphPlanUp")}:] / 100) | StrokeWidth 1 | Stroke Color 255,255,255,80 | StrokeDashes 3,3",
            "DynamicVariables=1", "Hidden=1");
        Meter("NetGraphScale", "Meter=String", "MeterStyle=StyleTiny", $"X=({S(ValueX)} - {S(4)})", $"Y={S(g + 1)}", "StringAlign=Right",
            $"MeasureName={Measure("NetGraphScaleText")}", "Text=%1");

        _y = g + GraphH + 6;
        foreach (var (dir, title) in new[] { ("Down", "Download"), ("Up", "Upload") })
        {
            Meter($"Label{dir}", "Meter=String", "MeterStyle=StyleLabel", $"Y={S(_y)}", $"Text={title}");
            Meter($"Value{dir}Lan", "Meter=String", "MeterStyle=StyleValue", $"MeasureName={Measure($"Net{dir}LanText")}",
                $"Y={S(_y)}", "FontColor=#LAN#", "Text=%1");
            Meter($"Value{dir}Wan", "Meter=String", "MeterStyle=StyleValue", $"MeasureName={Measure($"Net{dir}WanText")}",
                $"X=([Value{dir}Lan:X] - {S(8)})", $"Y={S(_y)}", "FontColor=#WAN#", "Text=%1", "DynamicVariables=1");
            // One bar: Internet segment, LAN segment after it, a faint 1 px tick at the Internet plan.
            Meter($"Bar{dir}", "Meter=Shape", "X=#BarX#", $"Y={S(_y + BarOffset)}",
                "Shape=Rectangle 0,0,#BarW#,#BarH#,#BarR# | Fill Color #Track# | StrokeWidth 0",
                $"Shape2=Rectangle 0,0,(#BarW# * [{Measure($"Net{dir}TotalPct")}:] / 100),#BarH#,#BarR# | Fill LinearGradient LANGrad | StrokeWidth 0",
                $"Shape3=Rectangle 0,0,(#BarW# * [{Measure($"Net{dir}WanPct")}:] / 100),#BarH#,#BarR# | Fill LinearGradient WANGrad | StrokeWidth 0",
                $"Shape4=Rectangle (#BarW# * [{Measure($"NetPlan{dir}Pct")}:] / 100),0,(([{Measure($"NetPlan{dir}Pct")}:] > 0) ? 1 : 0),#BarH# | Fill Color #PlanTick{dir}# | StrokeWidth 0",
                "WANGrad=90 | 41,121,255,255 ; 0.0 | 88,179,255,255 ; 1.0",
                "LANGrad=90 | 0,191,165,255 ; 0.0 | 29,233,182,255 ; 1.0",
                "DynamicVariables=1");
            _y += RowStep;
        }

        _y = g + 112;
        for (var n = 1; n <= _topRows; n++)
        {
            Meter($"NetTop{n}Dot", "Meter=Shape", "X=#LabelX#", $"Y={S(_y)}",
                $"Shape=Ellipse {S(3)},{S(ProcRow / 2)},{S(3)} | Fill Color [#Top{n}Color] | StrokeWidth 0", "DynamicVariables=1");
            Meter($"NetTop{n}Icon", "Meter=Image", $"ImageName=#@#[{Measure($"NetTop{n}Icon")}]", $"X=(#LabelX# + {S(10)})",
                $"Y={S(_y + 1)}", $"W={S(13)}", $"H={S(13)}", "PreserveAspectRatio=1", "AntiAlias=1", "DynamicVariables=1");
            Meter($"NetTop{n}Name", "Meter=String", "MeterStyle=StyleSmall", $"X=(#LabelX# + {S(30)})", $"Y={S(_y)}",
                $"W=(#BarW# - {S(180)})", $"H={S(ProcRow)}", "ClipString=1", "FontColor=#Text#",
                $"MeasureName={Measure($"NetTop{n}Name")}", "Text=%1");
            Meter($"NetTop{n}Value", "Meter=String", "MeterStyle=StyleSmall", "X=#ValueX#", $"Y={S(_y)}", "StringAlign=Right",
                "FontColor=#Text#", $"MeasureName={Measure($"NetTop{n}Text")}", "Text=%1");
            _y += ProcRow;
        }
        _y += 1;
        Meter("NetScale", "Meter=String", "MeterStyle=StyleSmall", "X=#LabelX#", $"Y={S(_y)}", "FontColor=#NET#",
            $"MeasureName={Measure("NetEthInMbps")}", $"MeasureName2={Measure("NetEthOutMbps")}", "Text=ETH DL/UL %1/%2 Mbps", "NumOfDecimals=1");
        Meter("NetWirelessScale", "Meter=String", "MeterStyle=StyleSmall", "X=#ValueX#", $"Y={S(_y)}", "StringAlign=Right", "FontColor=#GPU#",
            $"MeasureName={Measure("NetWifiActiveMode")}", $"MeasureName2={Measure("NetWifiActiveDlMbps")}",
            $"MeasureName3={Measure("NetWifiActiveUlMbps")}", "Text=%1 DL/UL %2/%3 Mbps", "NumOfDecimals=1");
        _bottom = _y + 14;
        _y += 30;
    }

    void DiskIO(bool first)
    {
        SectionHeader("DiskIO", "disk.png", "#RAM#", "DISK I/O", !first);
        for (var i = 0; i < _disks.Count; i++)
        {
            Row($"DiskIO{i + 1}", _disks[i].Label,
                new[] { $"MeasureName=MeasureDisk{i + 1}ReadMB", $"MeasureName2=MeasureDisk{i + 1}WriteMB", "Text=R %1 / W %2 MB/s", "NumOfDecimals=1" },
                $"[MeasureDisk{i + 1}IOMB:] / #DiskIOMax#", "116,214,132,255 ; 0.0 | 139,195,74,255 ; 1.0");
        }
        _y += SectionGap + 9;
    }

    void Drives(bool first)
    {
        SectionHeader("Disk", "disk.png", "#DISK#", "DRIVES USED", !first);
        for (var i = 0; i < _disks.Count; i++)
        {
            var n = i + 1;
            Row($"Disk{n}", _disks[i].Label,
                new[] { $"MeasureName=MeasureDisk{n}Used", $"MeasureName2=MeasureDisk{n}Total", "Text=%1B / %2B", "AutoScale=1", "NumOfDecimals=0" },
                $"[MeasureDisk{n}Pct:] / 100", "116,214,132,255 ; 0.0 | 139,195,74,255 ; 1.0", $"Disk{n}State",
                new[]
                {
                    "Line (#BarW# * 0.85),-1,(#BarW# * 0.85),(#BarH# + 1) | StrokeWidth 1 | Stroke Color 255,193,94,100",
                    "Line (#BarW# * 0.95),-1,(#BarW# * 0.95),(#BarH# + 1) | StrokeWidth 1 | Stroke Color 255,113,113,120",
                });
        }
    }

    // ------------------------------------------------------------ measures

    void Measures()
    {
        MeasureSection("MeasureCPU", "Measure=CPU", "Processor=0");
        MeasureSection("MeasureRAM", "Measure=PhysicalMemory");
        MeasureSection("MeasureRAMTotal", "Measure=PhysicalMemory", "Total=1");
        MeasureSection("MeasureRAMPct", "Measure=Calc", "Formula=(MeasureRAM / MeasureRAMTotal) * 100", "MinValue=0", "MaxValue=100");
        MeasureSection("MeasureGPU", "Measure=Plugin", "Plugin=UsageMonitor", "Alias=GPU", "Index=0", "Percent=1", "MinValue=0", "MaxValue=100");
        MeasureSection("MeasureGPUValue", "Measure=Calc", "Formula=Clamp(MeasureGPU, 0, 100)", "MinValue=0", "MaxValue=100");

        MeasureSection("MeasureTempsRaw", "Measure=WebParser", "URL=file://#@#temps.txt", $"RegExp={TempsFile.RegExp(_keys)}",
            "UpdateRate=1", "CodePage=65001");
        var graphKeys = new HashSet<string>(NetPanel.Keys.Where(k => k.EndsWith("Pct", StringComparison.Ordinal) ||
            (k.StartsWith("NetGraph", StringComparison.Ordinal) && k != "NetGraphScaleText")));
        foreach (var key in _keys)
        {
            if (key.StartsWith("NetTop", StringComparison.Ordinal) && int.Parse(key.Substring(6, 1), CultureInfo.InvariantCulture) > _topRows)
            {
                continue;
            }
            var extra = new List<string>();
            if (graphKeys.Contains(key) || key is "CPU" or "GPUCore" or "VRAMPct" or "GPUFanPct")
            {
                extra.Add("MinValue=0");
                extra.Add("MaxValue=100");
            }
            extra.AddRange(KeyActions(key));
            FileMeasure(key, extra.ToArray());
        }
        MeasureSection("MeasureVRAMUsedGB", "Measure=Calc", $"Formula={Measure("VRAMUsedMB")} / 1024", "MinValue=0");
        MeasureSection("MeasureVRAMTotalGB", "Measure=Calc", $"Formula={Measure("VRAMTotalMB")} / 1024", "MinValue=0");
        if (_config.ShowSection("performance") && VramRow)
        {
            // No graphics card (or no memory sensors): n/a instead of "-0.0 GB / -0.0 GB".
            MeasureSection("StateVRAM", "Measure=Calc", $"Formula={Measure("VRAMTotalMB")}",
                "IfCondition=StateVRAM <= 0", "IfTrueAction=[!SetOption ValueVRAM Text \"n/a\"]",
                "IfFalseAction=[!SetOption ValueVRAM Text \"%1 GB / %2 GB\"]");
        }

        // Health and color states.
        var health = ShowHealth;
        var temps = ShowTemps;
        ThresholdState("CPUState", Measure("CPU"), 65, 80, health && CpuTempRow ? "HealthCPU" : null, "CPU {0} %1°C",
            valueMeter: temps && CpuTempRow ? "ValueCPUTemp" : null, valueText: "%1°C");
        ThresholdState("GPUState", Measure("GPUCore"), 70, 83, health && GpuTempRow ? "HealthGPU" : null, "GPU {0} %1°C", "#GPU#",
            temps && GpuTempRow ? "ValueGPUTemp" : null, "%1°C");
        ThresholdState("RAMState", "MeasureRAMPct", 85, 95, health ? "HealthRAM" : null, "RAM {0} %1%");
        for (var i = 0; i < _temps.Count; i++)
        {
            ThresholdState($"Temp{i + 1}State", Measure($"Temp{i + 1}"), _temps[i].Warm, _temps[i].Hot,
                valueMeter: temps ? $"ValueTemp{i + 1}" : null, valueText: "%1°C");
        }
        for (var i = 0; i < _disks.Count; i++)
        {
            ThresholdState($"Disk{i + 1}State", $"MeasureDisk{i + 1}Pct", 85, 95);
        }
        FanStates();
        DiskMeasures();
    }

    IEnumerable<string> KeyActions(string key)
    {
        // Bangs may only touch meters this skin has: hidden sections have none.
        if (key.StartsWith("Net", StringComparison.Ordinal) && !_config.ShowSection("network"))
        {
            return Array.Empty<string>();
        }
        if (key is "NetDownMaxed" or "NetUpMaxed")
        {
            var dir = key == "NetDownMaxed" ? "Down" : "Up";
            return new[]
            {
                $"IfCondition={Measure(key)} = 1",
                $"IfTrueAction=[!SetOption Label{dir} FontColor \"#Warm#\"][!SetOption Value{dir}Wan FontWeight 700][!SetVariable PlanTick{dir} \"#Warm#\"]",
                $"IfFalseAction=[!SetOption Label{dir} FontColor \"#Muted#\"][!SetOption Value{dir}Wan FontWeight 400][!SetVariable PlanTick{dir} \"#PlanTick#\"]",
            };
        }
        if (key is "NetGraphPlanDown" or "NetGraphPlanUp")
        {
            return new[] { $"IfCondition={Measure(key)} > 0", $"IfTrueAction=[!ShowMeter {key}]", $"IfFalseAction=[!HideMeter {key}]" };
        }
        if (key.StartsWith("NetTop", StringComparison.Ordinal) && key.EndsWith("Scope", StringComparison.Ordinal))
        {
            var n = key.Substring(6, 1);
            return new[]
            {
                "IfMatch=^lan$", $"IfMatchAction=[!SetVariable Top{n}Color \"#LAN#\"]",
                "IfMatch2=^wan$", $"IfMatchAction2=[!SetVariable Top{n}Color \"#WAN#\"]",
                "IfMatch3=^other$", $"IfMatchAction3=[!SetVariable Top{n}Color \"#Muted#\"]",
                "IfMatch4=^none$", $"IfMatchAction4=[!SetVariable Top{n}Color \"0,0,0,0\"]",
            };
        }
        if (key == "NetEthInMbps")
        {
            // -1: no cable traffic for a minute, hide the wired half of the legend.
            return new[] { $"IfCondition={Measure(key)} < 0", "IfTrueAction=[!HideMeter NetScale]", "IfFalseAction=[!ShowMeter NetScale]" };
        }
        if (key == "NetWifiActiveMode")
        {
            // No Wi-Fi in use: hide the wireless half of the legend instead of "Off DL/UL 0.0/0.0".
            return new[] { "IfMatch=^(Off|0|)$", "IfMatchAction=[!HideMeter NetWirelessScale]", "IfNotMatchAction=[!ShowMeter NetWirelessScale]" };
        }
        if (key == "UpdateAvailable")
        {
            return new[]
            {
                "IfMatch=.+",
                $"IfMatchAction=[!SetOption SubTitle Text \"Update [{Measure(key)}] available: see the notification or the tray icon\"][!SetOption SubTitle FontColor \"#OK#\"]",
                "IfNotMatchAction=[!SetOption SubTitle Text \"Hardware bridge / desktop widget\"][!SetOption SubTitle FontColor \"#Muted#\"]",
            };
        }
        return Array.Empty<string>();
    }

    void FanStates()
    {
        // Each fan: n/a (-1), stopped (< 200 RPM: red if it should warn), or its speed.
        var lowParts = new List<string>();
        for (var i = 0; i < _fans.Count; i++)
        {
            var key = $"Fan{i + 1}";
            var m = Measure(key);
            var stopped = _fans[i].Warn
                ? $"[!SetVariable {key}State \"#Hot#\"][!SetVariable {key}StateLabel \"#Hot#\"][!SetOption Value{key} Text \"stopped\"]"
                : $"[!SetVariable {key}State \"0,0,0,0\"][!SetVariable {key}StateLabel \"#Muted#\"][!SetOption Value{key} Text \"stopped\"]";
            MeasureSection($"Measure{key}Peak", "Measure=Calc", $"Formula=Max(Max(Measure{key}Peak, {m}), 2500)");
            if (_fans[i].Warn)
            {
                lowParts.Add($"(({m} >= 0) && ({m} < 200))");
            }
            if (!ShowCooling)
            {
                continue;
            }
            MeasureSection($"State{key}", "Measure=Calc", $"Formula={m}",
                $"IfCondition=({m} < 0)", $"IfTrueAction=[!SetVariable {key}State \"0,0,0,0\"][!SetVariable {key}StateLabel \"#Muted#\"][!SetOption Value{key} Text \"n/a\"]",
                $"IfCondition2=({m} >= 0) && ({m} < 200)", $"IfTrueAction2={stopped}",
                $"IfCondition3=({m} >= 200)", $"IfTrueAction3=[!SetVariable {key}State \"0,0,0,0\"][!SetVariable {key}StateLabel \"#Muted#\"][!SetOption Value{key} Text \"%1 RPM\"]",
                "DynamicVariables=1");
        }
        // A graphics card at 0 RPM while cool is in its normal 0 RPM mode; hot and stopped is a problem.
        var gpuHotStopped = $"(({Measure("GPUCore")} >= 60) && ({Measure("GPUFan")} < 300) && ({Measure("GPUFanPct")} <= 0) && " +
            $"(({Measure("GPUFan")} >= 0) || ({Measure("GPUFanPct")} >= 0)))";
        if (GpuFanRow)
        {
            lowParts.Add(gpuHotStopped);
        }
        var low = lowParts.Count > 0 ? string.Join(" || ", lowParts) : "0";
        if (ShowCooling && GpuFanRow)
        {
            GpuFanState(gpuHotStopped);
        }
        if (ShowHealth && HasFans)
        {
            // FANS N/A when no fan reports at all (no fan sensors, e.g. a virtual machine): OK would be a guess.
            var known = string.Join(" || ", Enumerable.Range(1, _fans.Count).Select(n => $"({Measure($"Fan{n}")} >= 0)")
                .Concat(GpuFanRow ? new[] { $"({Measure("GPUFan")} >= 0) || ({Measure("GPUFanPct")} >= 0)" } : Array.Empty<string>()));
            MeasureSection("StateHealthFans", "Measure=Calc", "Formula=1",
                $"IfCondition={low}",
                "IfTrueAction=[!SetOption HealthFans Text \"FANS LOW\"][!SetOption HealthFans FontColor \"#Hot#\"][!SetOption HealthFansBar SolidColor \"#Hot#\"][!SetVariable HealthFill \"255,113,113,34\"]",
                $"IfCondition2=(({low}) = 0) && ({known})",
                "IfTrueAction2=[!SetOption HealthFans Text \"FANS OK\"][!SetOption HealthFans FontColor \"#OK#\"][!SetOption HealthFansBar SolidColor \"#OK#\"][!SetVariable HealthFill \"78,205,196,6\"]",
                $"IfCondition3=(({known}) = 0)",
                "IfTrueAction3=[!SetOption HealthFans Text \"FANS N/A\"][!SetOption HealthFans FontColor \"#Muted#\"][!SetOption HealthFansBar SolidColor \"#Muted#\"][!SetVariable HealthFill \"78,205,196,6\"]",
                "DynamicVariables=1");
        }
    }

    void GpuFanState(string gpuHotStopped)
    {
        // -1 in both = no fan sensors on this card (or no card): n/a.
        var known = $"(({Measure("GPUFan")} >= 0) || ({Measure("GPUFanPct")} >= 0))";
        MeasureSection("StateGPUFan", "Measure=Calc", "Formula=1",
            $"IfCondition={known} && ({Measure("GPUCore")} < 60) && ({Measure("GPUFan")} < 300) && ({Measure("GPUFanPct")} <= 0)",
            "IfTrueAction=[!SetOption ValueGPUFan Text \"idle (0 RPM mode)\"][!SetVariable GPUFanState \"0,0,0,0\"][!SetVariable GPUFanStateLabel \"#Muted#\"]",
            $"IfCondition2={gpuHotStopped}",
            "IfTrueAction2=[!SetOption ValueGPUFan Text \"stopped\"][!SetVariable GPUFanState \"#Hot#\"][!SetVariable GPUFanStateLabel \"#Hot#\"]",
            $"IfCondition3=({Measure("GPUFan")} >= 300) || ({Measure("GPUFanPct")} > 0)",
            "IfTrueAction3=[!SetOption ValueGPUFan Text \"%1 RPM / %2%\"][!SetVariable GPUFanState \"0,0,0,0\"][!SetVariable GPUFanStateLabel \"#Muted#\"]",
            $"IfCondition4=({Measure("GPUFan")} < 0) && ({Measure("GPUFanPct")} < 0)",
            "IfTrueAction4=[!SetOption ValueGPUFan Text \"n/a\"][!SetVariable GPUFanState \"0,0,0,0\"][!SetVariable GPUFanStateLabel \"#Muted#\"]",
            "DynamicVariables=1");
    }

    void DiskMeasures()
    {
        for (var i = 0; i < _disks.Count; i++)
        {
            var n = i + 1;
            var drive = _disks[i].Drive;
            foreach (var (name, counter) in new[] { ("Read", "Disk Read Bytes/sec"), ("Write", "Disk Write Bytes/sec") })
            {
                MeasureSection($"MeasureDisk{n}{name}Bytes", "Measure=Plugin", "Plugin=PerfMon", "PerfMonObject=LogicalDisk",
                    $"PerfMonCounter={counter}", $"PerfMonInstance={drive}");
                MeasureSection($"MeasureDisk{n}{name}MB", "Measure=Calc", $"Formula=MeasureDisk{n}{name}Bytes / 1048576", "MinValue=0", "MaxValue=#DiskIOMax#");
            }
            MeasureSection($"MeasureDisk{n}IOMB", "Measure=Calc", $"Formula=MeasureDisk{n}ReadMB + MeasureDisk{n}WriteMB", "MinValue=0", "MaxValue=#DiskIOMax#");
            MeasureSection($"MeasureDisk{n}Used", "Measure=FreeDiskSpace", $"Drive={drive}", "InvertMeasure=1");
            MeasureSection($"MeasureDisk{n}Total", "Measure=FreeDiskSpace", $"Drive={drive}", "Total=1");
            MeasureSection($"MeasureDisk{n}Pct", "Measure=Calc", $"Formula=(MeasureDisk{n}Total > 0) ? (MeasureDisk{n}Used / MeasureDisk{n}Total * 100) : 0",
                "MinValue=0", "MaxValue=100");
        }
    }

    // ------------------------------------------------------------ assembly

    string Text(int height)
    {
        Measures();
        var configPath = _config.Path;
        var header = new StringBuilder();
        Section(header, "Rainmeter", "Update=1000", "AccurateText=1", "DynamicWindowSize=1",
            "ContextTitle=CodexMonitor settings", $"ContextAction=[\"{_bridgeExe}\" --settings --config \"{configPath}\"]");
        Section(header, "MsBlur", "Measure=Plugin", "Plugin=FrostedGlass", "Type=Acrylic", "Border=All");
        Section(header, "Metadata", "Name=CodexMonitor", "Author=Codex", "Information=Generated by CodexBridge --build-skin from config.json; edits are overwritten.", "Version=3.0",
            // The display watcher rebuilds the skin when the primary screen height changes.
            $"ScreenHeight={_screenHeight}",
            // ...and when the bridge finds other sensors than these (first run, PawnIO installed, new GPU).
            $"Hardware={_hw.Signature(_fans.Count)}");

        var variables = new List<string>
        {
            $"W={S(Width)}", $"H={S(height)}", $"Pad={S(Pad)}", $"LabelX={S(Pad)}", $"ValueX={S(ValueX)}", $"BarX={S(Pad)}",
            $"BarW={S(BarW)}", $"BarH={Math.Max(3, S(BarH))}", $"BarR={Math.Max(1, S(BarR))}",
            $"IconSize={S(14)}", $"IconGap={S(6)}", $"IconYOffset={-Math.Max(1, S(1))}",
            "Font=Segoe UI", "Text=238,243,247,245", "Muted=154,166,178,235", "Panel=10,13,18,238", "Line=255,255,255,24",
            "Track=255,255,255,30", "CPU=0,229,255,255", "RAM=69,201,151,255", "GPU=151,136,255,255", "NET=88,179,255,255",
            "DISK=116,214,132,255", "Warn=255,113,113,255", "OK=0,229,255,255", "Warm=255,193,94,255", "Hot=255,113,113,255",
            "WAN=88,179,255,255", "LAN=29,233,182,255", "WANFill=88,179,255,150", "LANFill=29,233,182,150",
            "PlanTick=255,255,255,50", "PlanTickDown=255,255,255,50", "PlanTickUp=255,255,255,50",
            "DiskIOMax=1000", "HealthFill=78,205,196,6",
        };
        // State variables start neutral; the State measures set them every update.
        foreach (var state in new[] { "CPUState", "GPUState", "RAMState", "GPUFanState" }
                     .Concat(_temps.Select((_, i) => $"Temp{i + 1}State"))
                     .Concat(_fans.Select((_, i) => $"Fan{i + 1}State"))
                     .Concat(_disks.Select((_, i) => $"Disk{i + 1}State")))
        {
            variables.Add($"{state}=0,0,0,0");
            variables.Add($"{state}Label=#Muted#");
        }
        variables.AddRange(Enumerable.Range(1, NetPanel.TopRows).Select(n => $"Top{n}Color=0,0,0,0"));
        Section(header, "Variables", variables.ToArray());

        var styles = new StringBuilder();
        Section(styles, "StyleLabel", "X=#LabelX#", "FontFace=#Font#", $"FontSize={F(10)}", "FontColor=#Muted#", "AntiAlias=1");
        Section(styles, "StyleValue", "X=#ValueX#", "FontFace=#Font#", $"FontSize={F(10)}", "FontColor=#Text#", "AntiAlias=1",
            "StringAlign=Right", "NumOfDecimals=0");
        Section(styles, "StyleSectionTitle", "X=#Pad#", "FontFace=#Font#", $"FontSize={F(8)}", "FontColor=#Muted#", "AntiAlias=1");
        Section(styles, "StyleSmall", "FontFace=#Font#", $"FontSize={F(8)}", "FontColor=#Muted#", "AntiAlias=1");
        Section(styles, "StyleTiny", "FontFace=#Font#", $"FontSize={F(7)}", "FontColor=#Muted#", "AntiAlias=1");
        var panel = new StringBuilder();
        Section(panel, "Panel", "Meter=Shape",
            $"Shape=Rectangle 0,0,#W#,#H#,{S(12)} | Fill Color #Panel# | StrokeWidth 1 | Stroke LinearGradient BorderGrad",
            "BorderGrad=90 | 255,255,255,18 ; 0.0 | 255,255,255,30 ; 0.5 | 255,255,255,18 ; 1.0");

        return header.ToString() + _measures + styles + panel + _meters;
    }
}
