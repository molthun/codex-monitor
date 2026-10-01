using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexBridge;

/// <summary>A fan shown in the widget: a LibreHardwareMonitor sensor id (or a plugin's), a name, whether a stop is an alarm.</summary>
sealed record FanEntry(string Id, string Name, bool Warn);

/// <summary>An extra temperature after CPU and GPU (liquid, board, drives) with its own thresholds.</summary>
sealed record TempEntry(string Id, string Name, double Warm, double Hot);

sealed record DiskEntry(string Drive, string Label);

/// <summary>
/// config.json as a JSON tree with typed accessors for the options the bridge, the skin builder and
/// the settings window share. Unknown keys are kept, so older and newer versions can share a file.
/// </summary>
sealed class AppConfig
{
    public const int MaxDisks = 6;
    public const int MaxTopRows = 5;
    public static readonly string[] Sections = { "health", "performance", "temperatures", "cooling", "network", "diskIO", "drives" };

    public JsonObject Root { get; }
    public string Path { get; }

    AppConfig(string path, JsonObject root)
    {
        Path = path;
        Root = root;
    }

    public static AppConfig Load(string path)
    {
        JsonObject? root = null;
        try
        {
            if (File.Exists(path))
            {
                root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })?.AsObject();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // Unreadable config: start from defaults rather than failing.
        }
        return new AppConfig(path, root ?? new JsonObject());
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, Path, overwrite: true);
    }

    // ------------------------------------------------------------ generic access

    public JsonObject Section(string name)
    {
        if (Root[name] is not JsonObject section)
        {
            section = new JsonObject();
            Root[name] = section;
        }
        return section;
    }

    static T Get<T>(JsonNode? node, T fallback)
    {
        try
        {
            return node is null ? fallback : node.GetValue<T>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    static double Number(JsonNode? node, double fallback) => node is JsonValue value && value.TryGetValue<double>(out var d) ? d : fallback;

    // ------------------------------------------------------------ widget

    /// <summary>"Auto", "1080p", "2K" or "4K" (profiles.default, as in the older settings).</summary>
    public string Profile
    {
        get => Get<string?>(Root["profiles"]?["default"], null) ?? "Auto";
        set => Section("profiles")["default"] = value;
    }

    /// <summary>Custom size in percent of the 1080p layout; 0 = by profile.</summary>
    public double ScalePercent
    {
        get => Number(Root["widget"]?["scale"], 0);
        set => Section("widget")["scale"] = value;
    }

    public bool FitToScreen
    {
        get => Get(Root["widget"]?["fitToScreen"], true);
        set => Section("widget")["fitToScreen"] = value;
    }

    public bool Visible
    {
        get => Get(Root["widget"]?["visible"], true);
        set => Section("widget")["visible"] = value;
    }

    public bool ShowSection(string name) => Get(Root["widget"]?["show"]?[name], true);

    public void SetShowSection(string name, bool show)
    {
        var widget = Section("widget");
        if (widget["show"] is not JsonObject sections)
        {
            sections = new JsonObject();
            widget["show"] = sections;
        }
        sections[name] = show;
    }

    public int TopProcesses
    {
        get => Math.Clamp((int)Number(Root["widget"]?["topProcesses"], 3), 0, MaxTopRows);
        set => Section("widget")["topProcesses"] = Math.Clamp(value, 0, MaxTopRows);
    }

    public int MarginRight => (int)Number(Root["display"]?["marginRight"], 24);
    public int MarginTop => (int)Number(Root["display"]?["marginTop"], 24);

    // ------------------------------------------------------------ hardware

    public List<DiskEntry> Disks
    {
        get
        {
            var labels = Root["diskLabels"] as JsonObject;
            var drives = (Root["disks"] as JsonArray)?.Select(n => Get<string?>(n, null)).Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d!.Trim().TrimEnd('\\').ToUpperInvariant()).Distinct().Take(MaxDisks).ToList()
                ?? new List<string> { "C:" };
            return drives.Select(d => new DiskEntry(d, Get<string?>(labels?[d], null) ?? d)).ToList();
        }
        set
        {
            Root["disks"] = new JsonArray(value.Take(MaxDisks).Select(d => (JsonNode)d.Drive).ToArray());
            Root["diskLabels"] = new JsonObject(value.Where(d => d.Label != d.Drive)
                .Select(d => KeyValuePair.Create(d.Drive, (JsonNode?)d.Label)));
        }
    }

    /// <summary>The configured fan list, or null when the user has not made one (the bridge picks defaults).</summary>
    public List<FanEntry>? Fans
    {
        get => (Root["fans"]?["list"] as JsonArray)?.OfType<JsonObject>()
            .Where(f => !string.IsNullOrEmpty(Get<string?>(f["id"], null)))
            .Select(f => new FanEntry(Get(f["id"], ""), Get<string?>(f["name"], null) ?? Get(f["id"], ""), Get(f["warn"], true)))
            .ToList();
        set => Section("fans")["list"] = value is null ? null : new JsonArray(value.Select(f =>
            (JsonNode)new JsonObject { ["id"] = f.Id, ["name"] = f.Name, ["warn"] = f.Warn }).ToArray());
    }

    public List<TempEntry> Temps
    {
        get => (Root["temps"]?["list"] as JsonArray)?.OfType<JsonObject>()
            .Where(t => !string.IsNullOrEmpty(Get<string?>(t["id"], null)))
            .Select(t =>
            {
                var name = Get<string?>(t["name"], null) ?? Get(t["id"], "");
                var (warm, hot) = DefaultLimits(name);
                return new TempEntry(Get(t["id"], ""), name, Number(t["warm"], warm), Number(t["hot"], hot));
            }).ToList() ?? new List<TempEntry>();
        set => Section("temps")["list"] = new JsonArray(value.Select(t =>
            (JsonNode)new JsonObject { ["id"] = t.Id, ["name"] = t.Name, ["warm"] = t.Warm, ["hot"] = t.Hot }).ToArray());
    }

    /// <summary>Liquid runs much cooler than chips (same rule as the Linux bridge).</summary>
    public static (double Warm, double Hot) DefaultLimits(string? label) =>
        System.Text.RegularExpressions.Regex.IsMatch(label ?? "", "coolant|liquid|water", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? (40, 50) : (70, 85);

    /// <summary>"auto", "none" or a LibreHardwareMonitor hardware identifier ("/gpu-nvidia/0").</summary>
    public string GpuDevice
    {
        get => Get<string?>(Root["gpu"]?["device"], null) ?? "auto";
        set => Section("gpu")["device"] = value;
    }

    // ------------------------------------------------------------ updates

    /// <summary>"notify" (default), "install" (silent) or "off"; older configs store true/false.</summary>
    public string UpdateMode
    {
        get => Root["display"]?["autoUpdate"] switch
        {
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "install" : "off",
            JsonValue v when v.TryGetValue<string>(out var s) && s == "notify" => "notify",
            _ => "notify",
        };
        set => Section("display")["autoUpdate"] = value switch
        {
            "install" => JsonValue.Create(true),
            "off" => JsonValue.Create(false),
            _ => JsonValue.Create("notify"),
        };
    }
}
