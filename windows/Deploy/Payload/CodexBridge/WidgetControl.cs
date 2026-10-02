using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexBridge;

/// <summary>
/// Actions on the running widget shared by the settings window and the tray icon: restart the
/// bridge, rebuild and refresh the skin, show or hide it, and check for / request updates.
/// Everything here works without admin rights; the elevated bridge is restarted through its task.
/// </summary>
sealed class WidgetControl
{
    const string ReleasesUrl = "https://api.github.com/repos/molthun/codex-monitor/releases/latest";

    public string ConfigPath { get; }
    public string InstallRoot { get; }

    public WidgetControl(string configPath)
    {
        ConfigPath = configPath.Trim('"');
        // The install root is the folder with Deploy\ above the config (normally C:\CodexMonitor).
        var dir = Path.GetDirectoryName(Path.GetFullPath(ConfigPath));
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Deploy")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        InstallRoot = dir ?? Path.GetDirectoryName(Path.GetFullPath(ConfigPath)) ?? @"C:\CodexMonitor";
    }

    public AppConfig LoadConfig() => AppConfig.Load(ConfigPath);

    string RainmeterExe => LoadConfig().Root["rainmeter"]?["executable"]?.GetValue<string>() ?? @"C:\Program Files\Rainmeter\Rainmeter.exe";
    string TaskName => LoadConfig().Root["bridge"]?["taskName"]?.GetValue<string>() ?? "CodexMonitor Bridge Elevated";

    /// <summary>temps.txt sits in the skin's @Resources; inventory.json next to it.</summary>
    public string InventoryPath
    {
        get
        {
            var temps = LoadConfig().Root["bridge"]?["outputFile"]?.GetValue<string>() ?? Path.Combine(InstallRoot, @"@Resources\temps.txt");
            return Path.Combine(Path.GetDirectoryName(temps)!, "inventory.json");
        }
    }

    public string LocalVersion => ReadFirstLine(Path.Combine(InstallRoot, ".local_version")) ?? "unknown";

    /// <summary>Newer release the display watcher found (update-status.txt), or null.</summary>
    public string? AvailableUpdate => ReadFirstLine(Path.Combine(InstallRoot, "update-status.txt")) is { Length: > 0 } tag ? tag : null;

    static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    static void Run(string file, string arguments, int waitMs)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { CreateNoWindow = true, UseShellExecute = false });
            process?.WaitForExit(waitMs);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Missing tool (e.g. Rainmeter not installed yet): nothing to apply to.
        }
    }

    void Rainmeter(string bang) => Run(RainmeterExe, bang, 3000);

    /// <summary>
    /// The bridge restarts itself when config.json changes, keeping its administrator rights; this
    /// app is not elevated and may not be allowed to end and start the scheduled task. /run only
    /// starts the bridge when it is not running (a running task ignores a second start).
    /// </summary>
    public void RestartBridge()
    {
        try
        {
            File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only config: the task start below still covers a stopped bridge.
        }
        Run("schtasks.exe", $"/run /tn \"{TaskName}\"", 5000);
    }

    /// <summary>Regenerates the skin from the config, positions and refreshes it (Switch-WidgetSize.ps1).</summary>
    public void RebuildSkin()
    {
        var switcher = Path.Combine(InstallRoot, @"Deploy\Switch-WidgetSize.ps1");
        if (File.Exists(switcher))
        {
            Run("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{switcher}\" -InstallRoot \"{InstallRoot}\" -ConfigPath \"{ConfigPath}\"", 20000);
        }
        else
        {
            Rainmeter("!Refresh CodexMonitor");
        }
    }

    /// <summary>Applies saved settings: the bridge first (new fan/temperature keys), then the skin that reads them.</summary>
    public void ApplySettings()
    {
        // Saving config.json already restarts the bridge; give the new copy time to write its keys.
        Run("schtasks.exe", $"/run /tn \"{TaskName}\"", 5000);
        Thread.Sleep(3000);
        RebuildSkin();
    }

    /// <summary>Restart button: bridge and skin, like after an update.</summary>
    public void RestartWidget() => ApplySettings();

    public void SetVisible(bool visible)
    {
        var config = LoadConfig();
        config.Visible = visible;
        config.Save();
        Rainmeter(visible ? "!Show CodexMonitor" : "!Hide CodexMonitor");
    }

    /// <summary>Asks the display watcher (which owns the install logic) to install a release.</summary>
    public void RequestUpdate(string tag)
    {
        if (!Regex.IsMatch(tag, @"^[\w.\-]+$"))
        {
            throw new ArgumentException($"Not a release tag: {tag}");
        }
        File.WriteAllText(Path.Combine(InstallRoot, "update-request.txt"), tag);
    }

    public sealed record UpdateCheck(string Local, string Latest, bool Newer, string? Url);

    /// <summary>Asks GitHub for the latest release now (the watcher checks every 6 hours by itself).</summary>
    public async Task<UpdateCheck> CheckForUpdateAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexMonitor-Settings");
        using var document = JsonDocument.Parse(await http.GetStringAsync(ReleasesUrl));
        var latest = document.RootElement.GetProperty("tag_name").GetString() ?? "";
        var url = document.RootElement.TryGetProperty("html_url", out var html) ? html.GetString() : null;
        var local = LocalVersion;
        return new UpdateCheck(local, latest, IsNewer(latest, local), url);
    }

    /// <summary>
    /// "v2.2.0" newer than "v2.2.0-beta.1" or "v2.0.0"? A final release ranks above its own
    /// pre-releases, so a beta installed for testing is not "updated" back to an older release.
    /// Unknown local versions count as older.
    /// </summary>
    public static bool IsNewer(string latest, string local)
    {
        // Always three parts: Version("2.0") and Version("2.0.0") would not compare equal.
        static (Version Version, bool Final)? Parse(string tag)
        {
            var match = Regex.Match(tag ?? "", @"^v?(\d+(?:\.\d+){0,2})(-.+)?");
            if (!match.Success)
            {
                return null;
            }
            var parts = match.Groups[1].Value.Split('.').Select(int.Parse).Concat(new[] { 0, 0 }).Take(3).ToArray();
            return (new Version(parts[0], parts[1], parts[2]), !match.Groups[2].Success);
        }
        var remote = Parse(latest);
        var installed = Parse(local);
        if (remote is null)
        {
            return false;
        }
        if (installed is null)
        {
            return true;
        }
        return remote.Value.Version != installed.Value.Version
            ? remote.Value.Version > installed.Value.Version
            : remote.Value.Final && !installed.Value.Final;
    }
}
