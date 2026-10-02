using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CodexBridge;

/// <summary>
/// The tray icon (CodexBridge.exe --tray), started at sign-in without admin rights: show or hide
/// the widget, open the settings, check for and install updates, restart the widget. Updates are
/// installed by the display watcher, which also restarts this process with the new version.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly WidgetControl _widget;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _version;
    readonly ToolStripMenuItem _install;
    readonly ToolStripMenuItem _show;
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 30_000 };
    string? _announced;

    public TrayApp(string configPath)
    {
        _widget = new WidgetControl(configPath);
        _version = new ToolStripMenuItem { Enabled = false };
        _install = new ToolStripMenuItem("", null, (sender, _) => Install((sender as ToolStripItem)?.Tag as string)) { Visible = false };
        _show = new ToolStripMenuItem("Show widget", null, (_, _) => ToggleVisible()) { CheckOnClick = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_version);
        menu.Items.Add(_install);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_show);
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckNow());
        menu.Items.Add("Restart widget", null, (_, _) => Background(_widget.RestartWidget, "Widget restarted."));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Close this icon", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => Refresh();

        Icon? appIcon = null;
        try
        {
            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (ArgumentException)
        {
            // Fall back to the generic application icon.
        }
        _icon = new NotifyIcon { Icon = appIcon ?? SystemIcons.Application, ContextMenuStrip = menu, Visible = true, Text = "CodexMonitor" };
        _icon.DoubleClick += (_, _) => OpenSettings();
        _icon.BalloonTipClicked += (_, _) => Install(_install.Tag as string);
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        Refresh();
    }

    /// <summary>Version, visibility and the update the watcher found (update-status.txt).</summary>
    void Refresh()
    {
        _version.Text = $"CodexMonitor {_widget.LocalVersion}";
        _show.Checked = _widget.LoadConfig().Visible;
        var update = _widget.AvailableUpdate;
        _install.Visible = update is not null;
        _install.Tag = update;
        _install.Text = $"Install update {update}";
        _icon.Text = update is null ? "CodexMonitor" : $"CodexMonitor: update {update} available";
        if (update is not null && update != _announced)
        {
            _announced = update;
            _icon.ShowBalloonTip(10_000, $"CodexMonitor {update} is available", "Click here or use the tray menu to install it.", ToolTipIcon.Info);
        }
    }

    void ToggleVisible()
    {
        var visible = !_widget.LoadConfig().Visible;
        _widget.SetVisible(visible);
        _show.Checked = visible;
    }

    void OpenSettings()
    {
        // A separate process: it always runs the installed (possibly just updated) version.
        Process.Start(new ProcessStartInfo(Application.ExecutablePath, $"--settings --config \"{_widget.ConfigPath}\"") { UseShellExecute = false });
    }

    async Task CheckNow()
    {
        try
        {
            var result = await _widget.CheckForUpdateAsync();
            if (result.Newer)
            {
                _install.Tag = result.Latest;
                _install.Text = $"Install update {result.Latest}";
                _install.Visible = true;
                _announced = result.Latest;
                _icon.ShowBalloonTip(10_000, $"CodexMonitor {result.Latest} is available", "Click here or use the tray menu to install it.", ToolTipIcon.Info);
            }
            else
            {
                _icon.ShowBalloonTip(5_000, "CodexMonitor is up to date", $"Installed version: {result.Local}.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            _icon.ShowBalloonTip(5_000, "Could not check for updates", "GitHub is not reachable right now.", ToolTipIcon.Warning);
        }
    }

    void Install(string? tag)
    {
        if (tag is null)
        {
            return;
        }
        _widget.RequestUpdate(tag);
        _install.Visible = false;
        _icon.ShowBalloonTip(5_000, $"Installing CodexMonitor {tag}", "The widget restarts by itself when it is done.", ToolTipIcon.Info);
    }

    void Background(Action action, string done)
    {
        Task.Run(action).ContinueWith(_ => _icon.ShowBalloonTip(3_000, "CodexMonitor", done, ToolTipIcon.Info),
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
