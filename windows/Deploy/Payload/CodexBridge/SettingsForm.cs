using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace CodexBridge
{
    /// <summary>
    /// The settings window (CodexBridge.exe --settings, the tray menu or the skin's context menu),
    /// with the same sections as the Linux one: Widget, Hardware, Network, Updates. Hardware lists
    /// come from inventory.json, which the elevated bridge writes every 2 s, so fan speeds and
    /// temperatures update live while the window is open. Save rebuilds the skin and restarts the bridge.
    /// </summary>
    public class SettingsForm : Form
    {
        private readonly WidgetControl _widget;
        private readonly AppConfig _config;
        private JsonNode? _inventory;

        // Widget
        private ComboBox _cmbSize = null!;
        private NumericUpDown _numScale = null!;
        private CheckBox _chkFit = null!;
        private CheckBox _chkVisible = null!;
        private NumericUpDown _numTopRows = null!;
        private readonly Dictionary<string, CheckBox> _sections = new();

        // Hardware
        private ComboBox _cmbGpu = null!;
        private ListView _lstFans = null!;
        private ListView _lstTemps = null!;
        private NumericUpDown _numWarm = null!;
        private NumericUpDown _numHot = null!;
        private ListView _lstDrives = null!;

        // Network
        private FlowLayoutPanel _pnlNetworkAdapters = null!;
        private TextBox _txtNetworkExclusions = null!;
        private NumericUpDown _numPlanDown = null!;
        private NumericUpDown _numPlanUp = null!;
        private NumericUpDown _numLanMbps = null!;

        // Updates
        private ComboBox _cmbUpdateMode = null!;
        private ComboBox _cmbUpdateRate = null!;
        private Label _lblUpdate = null!;
        private Button _btnInstall = null!;

        private Label _lblApplyStatus = null!;
        private Label _lblSensorStatus = null!;
        private Button _btnPawnIo = null!;
        private readonly ToolTip _tip = new();
        private Button _btnSave = null!;
        private readonly List<(Button Tab, Panel Page)> _pages = new();
        private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 2000 };

        private static readonly Color Back = Color.FromArgb(18, 20, 24);
        private static readonly Color Card = Color.FromArgb(27, 31, 36);
        private static readonly Color Border = Color.FromArgb(58, 68, 76);
        private static readonly Color Field = Color.FromArgb(38, 44, 50);
        private static readonly Color TextMain = Color.FromArgb(238, 243, 247);
        private static readonly Color TextMuted = Color.FromArgb(166, 178, 188);
        private static readonly Color Accent = Color.FromArgb(0, 210, 230);
        private static readonly Color AccentSoft = Color.FromArgb(74, 226, 181);
        private static readonly string[] NetworkRoles = { "Auto", "Ethernet", "Wi-Fi", "Wi-Fi hotspot", "Ignore" };
        private static readonly string[] HiddenNetworkAdapterTerms =
        {
            "qos packet scheduler", "wfp native mac layer", "wfp 802.3 mac layer", "lightweight filter",
            "virtual switch extension", "virtual filtering platform", "wan miniport", "teredo tunneling",
            "pseudo-interface", "native wifi filter driver", "virtual wifi filter driver", "vswitch", "vethernet",
            "hyper-v virtual",
        };
        private static readonly (string Key, string Title)[] SectionTitles =
        {
            ("health", "Health strip"), ("performance", "Performance"), ("temperatures", "Temperatures"),
            ("cooling", "Cooling"), ("network", "Network traffic"), ("diskIO", "Disk I/O"), ("drives", "Drives used"),
        };
        private static readonly (string Value, string Title)[] Sizes =
        {
            ("Auto", "Automatic (by screen)"), ("1080p", "1080p"), ("2K", "2K"), ("4K", "4K"), ("Custom", "Custom size"),
        };

        public SettingsForm(string configPath)
        {
            _widget = new WidgetControl(configPath);
            _config = _widget.LoadConfig();
            _inventory = ReadInventory();
            InitializeComponent();
            LoadSettings();
            _liveTimer.Tick += (_, _) => RefreshLiveValues();
            _liveTimer.Start();
            FormClosed += (_, _) => _liveTimer.Dispose();
        }

        private JsonNode? ReadInventory()
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(_widget.InventoryPath));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // ------------------------------------------------------------ layout

        private void InitializeComponent()
        {
            Text = "CodexMonitor Settings";
            ClientSize = new Size(760, 880);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Back;
            ForeColor = TextMain;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (ArgumentException)
            {
                // No icon resource: keep the default.
            }

            var header = new Panel { Location = new Point(0, 0), Size = new Size(760, 86), BackColor = Color.FromArgb(12, 16, 21) };
            header.Controls.Add(new Label
            {
                Text = "CodexMonitor Settings", Location = new Point(24, 14), AutoSize = true,
                Font = new Font("Segoe UI Semibold", 16.0f, FontStyle.Bold), ForeColor = Accent,
            });
            header.Controls.Add(new Label
            {
                Text = "What the desktop widget shows and how it looks on this PC. CodexMonitor only monitors; fan control stays with your BIOS or tools.",
                Location = new Point(24, 48), Size = new Size(712, 34), ForeColor = TextMuted,
            });
            Controls.Add(header);

            var nav = new FlowLayoutPanel { Location = new Point(0, 86), Size = new Size(760, 44), BackColor = Color.FromArgb(14, 18, 23), Padding = new Padding(20, 6, 0, 0) };
            Controls.Add(nav);

            var pages = new[] { ("Widget", BuildWidgetPage()), ("Hardware", BuildHardwarePage()), ("Network", BuildNetworkPage()), ("Updates", BuildUpdatesPage()) };
            foreach (var (title, page) in pages)
            {
                var tab = new Button
                {
                    Text = title, Size = new Size(120, 32), FlatStyle = FlatStyle.Flat, ForeColor = TextMuted, BackColor = Color.Transparent,
                    Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(0, 0, 6, 0),
                };
                tab.FlatAppearance.BorderSize = 0;
                tab.Click += (_, _) => ShowPage(page);
                nav.Controls.Add(tab);
                page.Location = new Point(0, 130);
                page.Size = new Size(760, 686);
                page.AutoScroll = true;
                page.BackColor = Back;
                page.Visible = false;
                Controls.Add(page);
                _pages.Add((tab, page));
            }
            ShowPage(_pages[0].Page);

            var footer = new Panel { Location = new Point(0, 816), Size = new Size(760, 64), BackColor = Color.FromArgb(12, 16, 21) };
            _lblApplyStatus = new Label { Text = "Changes apply when you save.", Location = new Point(24, 22), Size = new Size(450, 24), ForeColor = TextMuted };
            _btnSave = new Button { Text = "Save and apply", Location = new Point(490, 15), Size = new Size(140, 34), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.Black };
            _btnSave.FlatAppearance.BorderSize = 0;
            _btnSave.Click += BtnSave_Click;
            var cancel = new Button { Text = "Cancel", Location = new Point(640, 15), Size = new Size(96, 34), FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = TextMain };
            cancel.FlatAppearance.BorderColor = Border;
            cancel.Click += (_, _) => Close();
            footer.Controls.Add(_lblApplyStatus);
            footer.Controls.Add(_btnSave);
            footer.Controls.Add(cancel);
            Controls.Add(footer);
        }

        private void ShowPage(Panel page)
        {
            foreach (var (tab, p) in _pages)
            {
                p.Visible = p == page;
                tab.ForeColor = p == page ? Accent : TextMuted;
                tab.BackColor = p == page ? Card : Color.Transparent;
            }
        }

        private static Panel Page() => new();

        private static Panel CreateCard(Control page, int y, int height, string title, string description)
        {
            var card = new Panel { Location = new Point(28, y), Size = new Size(704, height), BackColor = Card };
            card.Paint += (_, e) =>
            {
                using var pen = new Pen(Border, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            card.Controls.Add(new Label { Text = title, Location = new Point(18, 12), AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = TextMain });
            card.Controls.Add(new Label { Text = description, Location = new Point(18, 34), Size = new Size(668, 36), ForeColor = TextMuted, Font = new Font("Segoe UI", 8.5f) });
            page.Controls.Add(card);
            return card;
        }

        private static Label FieldLabel(string text, int x, int y, int width) =>
            new() { Text = text, Location = new Point(x, y), Size = new Size(width, 22), ForeColor = TextMain };

        private static NumericUpDown Number(int x, int y, int min, int max, int width = 82) => new()
        {
            Location = new Point(x, y), Size = new Size(width, 25), Minimum = min, Maximum = max, BackColor = Field, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle,
        };

        private static ComboBox Combo(int x, int y, int width, IEnumerable<string> items)
        {
            var combo = new ComboBox { Location = new Point(x, y), Size = new Size(width, 25), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Field, ForeColor = TextMain, FlatStyle = FlatStyle.Flat };
            combo.Items.AddRange(items.Cast<object>().ToArray());
            return combo;
        }

        private static CheckBox Check(string text, int x, int y, int width = 300) =>
            new() { Text = text, Location = new Point(x, y), Size = new Size(width, 24), ForeColor = TextMain };

        private static Button SmallButton(string text, int x, int y, int width, EventHandler onClick)
        {
            var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, 28), FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = TextMain };
            button.FlatAppearance.BorderColor = Border;
            button.Click += onClick;
            return button;
        }

        /// <summary>Details list with a dark header (the default header ignores BackColor).</summary>
        private static ListView DarkList(int x, int y, int width, int height, params (string Title, int Width)[] columns)
        {
            var list = new ListView
            {
                Location = new Point(x, y), Size = new Size(width, height), View = View.Details, FullRowSelect = true, HideSelection = false,
                BackColor = Field, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle, OwnerDraw = true, MultiSelect = false,
            };
            foreach (var (title, w) in columns)
            {
                list.Columns.Add(title, w);
            }
            list.DrawColumnHeader += (_, e) =>
            {
                using var back = new SolidBrush(Card);
                e.Graphics.FillRectangle(back, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header?.Text, list.Font, e.Bounds, TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            list.DrawItem += (_, e) => e.DrawDefault = true;
            list.DrawSubItem += (_, e) => e.DrawDefault = true;
            return list;
        }

        private Panel BuildWidgetPage()
        {
            var page = Page();
            var size = CreateCard(page, 16, 150, "Size", "Automatic picks a size for the screen resolution; Custom sets it in percent of the 1080p layout (430 px wide). Fit to screen shrinks the widget when it is taller than the screen.");
            size.Controls.Add(FieldLabel("Widget size", 18, 78, 100));
            _cmbSize = Combo(130, 75, 220, Sizes.Select(s => s.Title));
            _numScale = Number(370, 75, 50, 250);
            size.Controls.Add(FieldLabel("%", 456, 78, 30));
            _cmbSize.SelectedIndexChanged += (_, _) => _numScale.Enabled = _cmbSize.SelectedIndex == Sizes.Length - 1;
            _chkFit = Check("Fit to screen height", 18, 112);
            size.Controls.AddRange(new Control[] { _cmbSize, _numScale, _chkFit });

            var sections = CreateCard(page, 182, 196, "Sections", "Hide what you don't need: the widget gets shorter, which helps on small screens.");
            for (var i = 0; i < SectionTitles.Length; i++)
            {
                var (key, title) = SectionTitles[i];
                var check = Check(title, 18 + (i % 2) * 340, 78 + (i / 2) * 28);
                _sections[key] = check;
                sections.Controls.Add(check);
            }

            var desktop = CreateCard(page, 394, 120, "Desktop", "The tray icon also has a Show widget switch.");
            _chkVisible = Check("Show the widget", 18, 78, 200);
            desktop.Controls.Add(_chkVisible);
            desktop.Controls.Add(FieldLabel("Top processes rows", 330, 80, 140));
            _numTopRows = Number(480, 77, 0, AppConfig.MaxTopRows, 60);
            desktop.Controls.Add(_numTopRows);
            return page;
        }

        private Panel BuildHardwarePage()
        {
            var page = Page();
            var access = CreateCard(page, 16, 112, "Sensor access",
                "CPU temperatures and board fans need the bridge to run as administrator and the PawnIO driver; graphics cards work without them.");
            _lblSensorStatus = new Label { Location = new Point(18, 78), Size = new Size(520, 28), ForeColor = TextMain, AutoEllipsis = true };
            _btnPawnIo = SmallButton("Install PawnIO…", 548, 74, 138, (_, _) => InstallPawnIo());
            _btnPawnIo.Visible = false;
            access.Controls.AddRange(new Control[] { _lblSensorStatus, _btnPawnIo });

            var gpu = CreateCard(page, 144, 112, "Graphics card", "Automatic prefers NVIDIA, then the AMD card with the most memory, then Intel.");
            _cmbGpu = Combo(18, 76, 520, Array.Empty<string>());
            gpu.Controls.Add(_cmbGpu);

            var fans = CreateCard(page, 272, 300, "Fans",
                "Board fans, AIO pumps and radiator fans, in the widget's order. Watch the live speeds to tell them apart: load the CPU and its cooler speeds up. Double-click a name to rename it; Warn marks fans whose stop is an alarm.");
            _lstFans = DarkList(18, 76, 668, 170, ("Name", 190), ("Sensor", 300), ("Speed", 90), ("Warn", 70));
            _lstFans.LabelEdit = true;
            fans.Controls.Add(_lstFans);
            fans.Controls.Add(SmallButton("Add…", 18, 256, 90, (s, _) => ShowAddMenu((Control)s!, true)));
            fans.Controls.Add(SmallButton("Remove", 116, 256, 90, (_, _) => RemoveSelected(_lstFans)));
            fans.Controls.Add(SmallButton("Up", 214, 256, 60, (_, _) => MoveSelected(_lstFans, -1)));
            fans.Controls.Add(SmallButton("Down", 282, 256, 70, (_, _) => MoveSelected(_lstFans, 1)));
            fans.Controls.Add(SmallButton("Warn on/off", 360, 256, 110, (_, _) =>
            {
                if (_lstFans.SelectedItems.Count == 1 && _lstFans.SelectedItems[0].Tag is FanEntry fan)
                {
                    var item = _lstFans.SelectedItems[0];
                    item.Tag = fan with { Warn = !fan.Warn };
                    item.SubItems[3].Text = fan.Warn ? "" : "yes";
                }
            }));

            var temps = CreateCard(page, 588, 300, "More temperatures",
                "Shown after CPU and GPU: liquid temperature of an AIO cooler, board, drives. Amber and red thresholds per sensor; liquid defaults to 40 / 50 °C.");
            _lstTemps = DarkList(18, 76, 668, 170, ("Name", 190), ("Sensor", 300), ("Now", 80), ("Amber", 50), ("Red", 50));
            _lstTemps.LabelEdit = true;
            temps.Controls.Add(_lstTemps);
            temps.Controls.Add(SmallButton("Add…", 18, 256, 90, (s, _) => ShowAddMenu((Control)s!, false)));
            temps.Controls.Add(SmallButton("Remove", 116, 256, 90, (_, _) => RemoveSelected(_lstTemps)));
            temps.Controls.Add(SmallButton("Up", 214, 256, 60, (_, _) => MoveSelected(_lstTemps, -1)));
            temps.Controls.Add(SmallButton("Down", 282, 256, 70, (_, _) => MoveSelected(_lstTemps, 1)));
            temps.Controls.Add(FieldLabel("Amber from", 380, 259, 80));
            _numWarm = Number(462, 256, 0, 150, 60);
            temps.Controls.Add(FieldLabel("Red", 534, 259, 34));
            _numHot = Number(570, 256, 0, 150, 60);
            temps.Controls.AddRange(new Control[] { _numWarm, _numHot });
            _lstTemps.SelectedIndexChanged += (_, _) =>
            {
                if (_lstTemps.SelectedItems.Count == 1 && _lstTemps.SelectedItems[0].Tag is TempEntry t)
                {
                    _numWarm.Value = (decimal)Math.Clamp(t.Warm, 0, 150);
                    _numHot.Value = (decimal)Math.Clamp(t.Hot, 0, 150);
                }
            };
            void UpdateLimits()
            {
                if (_lstTemps.SelectedItems.Count == 1 && _lstTemps.SelectedItems[0].Tag is TempEntry t)
                {
                    var item = _lstTemps.SelectedItems[0];
                    item.Tag = t with { Warm = (double)_numWarm.Value, Hot = (double)_numHot.Value };
                    item.SubItems[3].Text = _numWarm.Value.ToString();
                    item.SubItems[4].Text = _numHot.Value.ToString();
                }
            }
            _numWarm.ValueChanged += (_, _) => UpdateLimits();
            _numHot.ValueChanged += (_, _) => UpdateLimits();

            var drives = CreateCard(page, 904, 240, "Drives", $"Up to {AppConfig.MaxDisks} drives for Disk I/O and Drives used, in this order. Double-click a name to rename it.");
            _lstDrives = DarkList(18, 76, 668, 150, ("Name in the widget", 230), ("Drive", 120), ("Size", 300));
            _lstDrives.CheckBoxes = true;
            _lstDrives.LabelEdit = true;
            _lstDrives.ItemCheck += (_, e) =>
            {
                if (e.NewValue == CheckState.Checked && _lstDrives.CheckedItems.Count >= AppConfig.MaxDisks)
                {
                    e.NewValue = CheckState.Unchecked;
                    _lblApplyStatus.Text = $"The widget has room for {AppConfig.MaxDisks} drives.";
                }
            };
            drives.Controls.Add(_lstDrives);

            var note = CreateCard(page, 1160, 80, "Other USB devices",
                "Fan hubs and controllers that LibreHardwareMonitor does not know can be added with a sensor plugin; ask on GitHub, the format is shared with Linux.");
            return page;
        }

        private Panel BuildNetworkPage()
        {
            var page = Page();
            var network = CreateCard(page, 16, 320, "Network adapters", "Only real network adapters are shown. Windows virtual/filter adapters are ignored automatically.");
            _pnlNetworkAdapters = new FlowLayoutPanel { Location = new Point(18, 76), Size = new Size(668, 170), BackColor = Color.Transparent, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            network.Controls.Add(_pnlNetworkAdapters);
            network.Controls.Add(new Label { Text = "Advanced ignore words", Location = new Point(18, 254), Size = new Size(180, 20), ForeColor = TextMuted, Font = new Font("Segoe UI", 8.25f) });
            _txtNetworkExclusions = new TextBox { Location = new Point(18, 278), Size = new Size(668, 25), BackColor = Field, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
            network.Controls.Add(_txtNetworkExclusions);

            var speeds = CreateCard(page, 352, 128, "Speeds",
                "Your Internet plan marks the bars and turns amber when it is maxed out (0 = not set). LAN full scale 0 = the network card's link speed.");
            speeds.Controls.Add(FieldLabel("Internet ↓ Mbps", 18, 84, 112));
            _numPlanDown = Number(132, 81, 0, 100000);
            speeds.Controls.Add(FieldLabel("↑ Mbps", 232, 84, 60));
            _numPlanUp = Number(294, 81, 0, 100000);
            speeds.Controls.Add(FieldLabel("LAN Mbps", 410, 84, 80));
            _numLanMbps = Number(492, 81, 0, 100000);
            speeds.Controls.AddRange(new Control[] { _numPlanDown, _numPlanUp, _numLanMbps });
            return page;
        }

        private Panel BuildUpdatesPage()
        {
            var page = Page();
            var updates = CreateCard(page, 16, 220, "Updates", "New releases from GitHub install in the background; the widget restarts by itself, no sign-out needed.");
            updates.Controls.Add(FieldLabel($"Installed version: {_widget.LocalVersion}", 18, 78, 400));
            updates.Controls.Add(FieldLabel("When a new version is out", 18, 112, 190));
            _cmbUpdateMode = Combo(214, 109, 300, new[] { "Show a notification with an Update button", "Install it automatically", "Don't check" });
            updates.Controls.Add(_cmbUpdateMode);
            updates.Controls.Add(SmallButton("Check now", 18, 152, 110, async (_, _) =>
            {
                _lblUpdate.Text = "Checking…";
                try
                {
                    var result = await _widget.CheckForUpdateAsync();
                    _btnInstall.Visible = result.Newer;
                    _btnInstall.Tag = result.Latest;
                    _lblUpdate.Text = result.Newer ? $"{result.Latest} is available." : $"Up to date (latest release {result.Latest}).";
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
                {
                    _lblUpdate.Text = "GitHub is not reachable right now.";
                }
            }));
            _btnInstall = SmallButton("Install", 136, 152, 90, (_, _) =>
            {
                if (_btnInstall.Tag is string tag)
                {
                    _widget.RequestUpdate(tag);
                    _lblUpdate.Text = $"Installing {tag} in the background; a notification tells you when it is done.";
                    _btnInstall.Visible = false;
                }
            });
            _btnInstall.Visible = false;
            _btnInstall.BackColor = AccentSoft;
            _btnInstall.ForeColor = Color.Black;
            _lblUpdate = new Label { Location = new Point(236, 157), Size = new Size(450, 40), ForeColor = TextMuted };
            if (_widget.AvailableUpdate is { } available)
            {
                _lblUpdate.Text = $"{available} is available.";
                _btnInstall.Tag = available;
                _btnInstall.Visible = true;
            }
            updates.Controls.AddRange(new Control[] { _btnInstall, _lblUpdate });

            var refresh = CreateCard(page, 252, 112, "Sensor refresh", "How often the bridge reads the sensors.");
            _cmbUpdateRate = Combo(18, 76, 140, new[] { "1 second", "2 seconds", "3 seconds", "5 seconds", "10 seconds", "30 seconds" });
            refresh.Controls.Add(_cmbUpdateRate);
            return page;
        }

        // ------------------------------------------------------------ sensors

        private IEnumerable<(string Id, string Name, string Hardware, double Value)> Sensors(string chipsKey, string valuesKey)
        {
            foreach (var chip in (_inventory?[chipsKey] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var hardware = chip["name"]?.GetValue<string>() ?? "";
                var labels = chip["labels"] as JsonObject;
                foreach (var (id, value) in chip[valuesKey] as JsonObject ?? new JsonObject())
                {
                    yield return (id, labels?[id]?.GetValue<string>() ?? id, hardware, value?.GetValue<double>() ?? 0);
                }
            }
        }

        private Dictionary<string, (string Name, string Hardware, double Value)> SensorMap(bool fans) =>
            Sensors(fans ? "fanChips" : "tempChips", fans ? "fans" : "temps")
                .GroupBy(s => s.Id).ToDictionary(g => g.Key, g => (g.First().Name, g.First().Hardware, g.First().Value));

        private static string FormatValue(bool fans, double? value) =>
            value is null ? "not found" : fans ? $"{value:0} RPM" : $"{value:0.0} °C";

        private void AddFanItem(FanEntry fan, Dictionary<string, (string Name, string Hardware, double Value)> sensors)
        {
            sensors.TryGetValue(fan.Id, out var s);
            var item = new ListViewItem(new[] { fan.Name, s.Hardware is null ? fan.Id : $"{s.Name} · {s.Hardware}", FormatValue(true, s.Hardware is null ? null : s.Value), fan.Warn ? "yes" : "" }) { Tag = fan };
            _lstFans.Items.Add(item);
        }

        private void AddTempItem(TempEntry t, Dictionary<string, (string Name, string Hardware, double Value)> sensors)
        {
            sensors.TryGetValue(t.Id, out var s);
            var item = new ListViewItem(new[] { t.Name, s.Hardware is null ? t.Id : $"{s.Name} · {s.Hardware}", FormatValue(false, s.Hardware is null ? null : s.Value), $"{t.Warm:0}", $"{t.Hot:0}" }) { Tag = t };
            _lstTemps.Items.Add(item);
        }

        /// <summary>"Add…": every sensor not in the list yet, with its live value.</summary>
        private void ShowAddMenu(Control anchor, bool fans)
        {
            var list = fans ? _lstFans : _lstTemps;
            var used = list.Items.Cast<ListViewItem>().Select(i => i.Tag is FanEntry f ? f.Id : ((TempEntry)i.Tag!).Id).ToHashSet();
            var menu = new ContextMenuStrip { BackColor = Card, ForeColor = TextMain, ShowImageMargin = false };
            var sensors = SensorMap(fans);
            foreach (var (id, (name, hardware, value)) in sensors.Where(s => !used.Contains(s.Key)))
            {
                menu.Items.Add($"{name} · {hardware} · {FormatValue(fans, value)}", null, (_, _) =>
                {
                    if (fans)
                    {
                        AddFanItem(new FanEntry(id, name, name.Contains("Pump", StringComparison.OrdinalIgnoreCase) || name.Contains("CPU", StringComparison.OrdinalIgnoreCase)), sensors);
                    }
                    else
                    {
                        var (warm, hot) = AppConfig.DefaultLimits(name);
                        AddTempItem(new TempEntry(id, name, warm, hot), sensors);
                    }
                });
            }
            if (menu.Items.Count == 0)
            {
                menu.Items.Add(_inventory is null ? "The bridge has not listed the sensors yet" : "Every sensor is already in the list").Enabled = false;
            }
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        private static void RemoveSelected(ListView list)
        {
            if (list.SelectedItems.Count == 1)
            {
                list.Items.Remove(list.SelectedItems[0]);
            }
        }

        private static void MoveSelected(ListView list, int delta)
        {
            if (list.SelectedItems.Count != 1)
            {
                return;
            }
            var item = list.SelectedItems[0];
            var index = item.Index + delta;
            if (index < 0 || index >= list.Items.Count)
            {
                return;
            }
            list.Items.Remove(item);
            list.Items.Insert(index, item);
            item.Selected = true;
        }

        /// <summary>Live fan speeds and temperatures: the bridge rewrites inventory.json every 2 s.</summary>
        private void RefreshLiveValues()
        {
            _inventory = ReadInventory() ?? _inventory;
            ShowSensorStatus();
            var fans = SensorMap(true);
            foreach (ListViewItem item in _lstFans.Items)
            {
                if (item.Tag is FanEntry f)
                {
                    item.SubItems[2].Text = FormatValue(true, fans.TryGetValue(f.Id, out var s) ? s.Value : null);
                }
            }
            var temps = SensorMap(false);
            foreach (ListViewItem item in _lstTemps.Items)
            {
                if (item.Tag is TempEntry t)
                {
                    item.SubItems[2].Text = FormatValue(false, temps.TryGetValue(t.Id, out var s) ? s.Value : null);
                }
            }
        }

        /// <summary>What the bridge reports about sensor access (inventory.json "status").</summary>
        private void ShowSensorStatus()
        {
            var status = _inventory?["status"] as JsonObject;
            bool Flag(string key) => status?[key]?.GetValue<bool>() ?? false;
            var devices = (status?["devices"] as JsonArray ?? new JsonArray()).Select(d => d?.GetValue<string>()).OfType<string>().ToList();
            var (text, warn) = status switch
            {
                null when _inventory is null => ("The bridge has not reported yet. If this stays, choose Restart in the tray menu.", true),
                null => ("This bridge version does not report sensor access.", false),
                _ when status["error"]?.GetValue<string>() is { Length: > 0 } error => ($"LibreHardwareMonitor did not start: {error}", true),
                _ when !Flag("admin") => ("The bridge runs without administrator rights: no CPU temperatures or board fans. Run Install-CodexMonitor.cmd again.", true),
                _ when !Flag("pawnIO") => ("The PawnIO driver is not installed: no CPU temperatures or board fans.", true),
                _ when !Flag("cpuTemp") => ($"No CPU temperature on this PC. Found: {string.Join(", ", devices)}", true),
                _ => ($"OK. Found: {string.Join(", ", devices)}", false),
            };
            _lblSensorStatus.Text = text;
            _lblSensorStatus.ForeColor = warn ? Color.FromArgb(255, 193, 94) : TextMain;
            _tip.SetToolTip(_lblSensorStatus, devices.Count == 0 ? text : text + "\n\n" + string.Join("\n", devices));
            _btnPawnIo.Visible = status is not null && Flag("admin") && !Flag("pawnIO");
        }

        /// <summary>PawnIO from winget (asks for administrator rights), then a bridge restart to load it.</summary>
        private async void InstallPawnIo()
        {
            _btnPawnIo.Enabled = false;
            _lblSensorStatus.Text = "Installing the PawnIO driver…";
            try
            {
                var winget = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("winget.exe",
                    "install --id namazso.PawnIO --exact --silent --accept-source-agreements --accept-package-agreements")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                });
                if (winget is not null)
                {
                    await winget.WaitForExitAsync();
                }
                _widget.RestartBridge();
                _lblSensorStatus.Text = "Installed. The bridge restarts and reads the sensors in a few seconds.";
            }
            catch (Exception ex)
            {
                // Cancelled UAC prompt or no winget.
                _lblSensorStatus.Text = $"Could not install PawnIO: {ex.Message}. Get it from pawnio.eu.";
            }
            finally
            {
                _btnPawnIo.Enabled = true;
            }
        }

        // ------------------------------------------------------------ load / save

        private void LoadSettings()
        {
            // Widget
            var profile = _config.ScalePercent > 0 ? "Custom" : _config.Profile;
            _cmbSize.SelectedIndex = Math.Max(0, Array.FindIndex(Sizes, s => string.Equals(s.Value, profile, StringComparison.OrdinalIgnoreCase)));
            _numScale.Value = (decimal)Math.Clamp(_config.ScalePercent > 0 ? _config.ScalePercent : 100, 50, 250);
            _numScale.Enabled = profile == "Custom";
            _chkFit.Checked = _config.FitToScreen;
            foreach (var (key, check) in _sections)
            {
                check.Checked = _config.ShowSection(key);
            }
            _chkVisible.Checked = _config.Visible;
            _numTopRows.Value = _config.TopProcesses;

            // Hardware
            ShowSensorStatus();
            // Readable names: "Automatic — <the card it picks>", "<name> · 12 GB", "<name> · integrated".
            var gpus = (_inventory?["gpus"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(g =>
            {
                var name = g["name"]?.GetValue<string>() ?? "";
                var memory = g["memoryMB"] is JsonValue m && m.TryGetValue<double>(out var mb) ? mb : 0;
                var label = g["integrated"]?.GetValue<bool>() == true ? $"{name} · integrated"
                    : memory > 0 ? $"{name} · {Math.Round(memory / 1024)} GB" : name;
                return (Id: g["id"]?.GetValue<string>() ?? "", Name: name, Label: label);
            }).ToList();
            var autoName = gpus.FirstOrDefault(g => g.Id == _inventory?["autoGpu"]?.GetValue<string>()).Name;
            var gpuChoices = new List<(string Id, string Name)> { ("auto", autoName is { Length: > 0 } ? $"Automatic — {autoName}" : "Automatic") };
            gpuChoices.AddRange(gpus.Select(g => (g.Id, g.Label)));
            gpuChoices.Add(("none", "Don't show a graphics card"));
            if (!gpuChoices.Any(g => g.Id == _config.GpuDevice))
            {
                gpuChoices.Add((_config.GpuDevice, _config.GpuDevice));
            }
            _cmbGpu.Items.AddRange(gpuChoices.Select(g => g.Name).Cast<object>().ToArray());
            _cmbGpu.Tag = gpuChoices.Select(g => g.Id).ToList();
            // Wide enough for the longest name, so nothing is cut off.
            _cmbGpu.DropDownWidth = Math.Max(_cmbGpu.Width, gpuChoices.Max(g => TextRenderer.MeasureText(g.Name, _cmbGpu.Font).Width) + 30);
            _cmbGpu.SelectedIndex = gpuChoices.FindIndex(g => g.Id == _config.GpuDevice);

            var fanSensors = SensorMap(true);
            var fanList = _config.Fans ?? (_inventory?["fanList"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(f => new FanEntry(f["id"]?.GetValue<string>() ?? "", f["name"]?.GetValue<string>() ?? "", f["warn"]?.GetValue<bool>() ?? true)).ToList();
            foreach (var fan in fanList)
            {
                AddFanItem(fan, fanSensors);
            }
            var tempSensors = SensorMap(false);
            foreach (var t in _config.Temps)
            {
                AddTempItem(t, tempSensors);
            }
            _lstFans.AfterLabelEdit += (_, e) =>
            {
                if (e.Label is { Length: > 0 } && _lstFans.Items[e.Item].Tag is FanEntry f)
                {
                    _lstFans.Items[e.Item].Tag = f with { Name = e.Label };
                }
            };
            _lstTemps.AfterLabelEdit += (_, e) =>
            {
                if (e.Label is { Length: > 0 } && _lstTemps.Items[e.Item].Tag is TempEntry t)
                {
                    _lstTemps.Items[e.Item].Tag = t with { Name = e.Label };
                }
            };

            var configured = _config.Disks;
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable).ToList();
            // Configured drives first, in their order, then the rest.
            foreach (var drive in drives.OrderBy(d => configured.FindIndex(c => c.Drive == d.Name.Substring(0, 2)) is var i && i >= 0 ? i : 100).ThenBy(d => d.Name))
            {
                var letter = drive.Name.Substring(0, 2).ToUpperInvariant();
                var label = configured.FirstOrDefault(c => c.Drive == letter)?.Label ?? letter;
                var info = "";
                try
                {
                    if (drive.IsReady)
                    {
                        info = $"{drive.VolumeLabel} · {drive.TotalFreeSpace / 1e9:0} GB free of {drive.TotalSize / 1e9:0} GB".Trim(' ', '·');
                    }
                }
                catch (IOException)
                {
                    info = "not ready";
                }
                _lstDrives.Items.Add(new ListViewItem(new[] { label, letter, info }) { Tag = letter, Checked = configured.Any(c => c.Drive == letter) });
            }

            // Network
            PopulateNetworkAdapters();
            var network = _config.Root["network"] as JsonObject;
            _txtNetworkExclusions.Text = string.Join(", ", StringList(network, "ignoreAdaptersContaining"));
            ApplyNetworkRoles(network);
            _numPlanDown.Value = Clamp(network?["internetDownMbps"]);
            _numPlanUp.Value = Clamp(network?["internetUpMbps"]);
            _numLanMbps.Value = Clamp(network?["lanMbps"]);

            // Updates
            _cmbUpdateMode.SelectedIndex = _config.UpdateMode switch { "install" => 1, "off" => 2, _ => 0 };
            var rate = Math.Max(1, (int)(_config.Root["bridge"]?["updateSeconds"]?.GetValue<double>() ?? 1));
            var rateText = rate == 1 ? "1 second" : $"{rate} seconds";
            if (_cmbUpdateRate.FindStringExact(rateText) < 0)
            {
                _cmbUpdateRate.Items.Add(rateText);
            }
            _cmbUpdateRate.SelectedIndex = _cmbUpdateRate.FindStringExact(rateText);
        }

        private static decimal Clamp(JsonNode? node)
        {
            var value = node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            return (decimal)Math.Clamp(value, 0, 100000);
        }

        private static List<string> StringList(JsonObject? parent, string key) =>
            (parent?[key] as JsonArray)?.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
            ?? new List<string>();

        private async void BtnSave_Click(object? sender, EventArgs e)
        {
            _btnSave.Enabled = false;
            try
            {
                SaveSettings();
                _lblApplyStatus.Text = "Saved. Rebuilding the widget and restarting the bridge…";
                await Task.Run(_widget.ApplySettings);
                _lblApplyStatus.Text = "Saved and applied.";
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _btnSave.Enabled = true;
                _lblApplyStatus.Text = "Save failed. No further changes were applied.";
                MessageBox.Show($"Failed to save settings: {ex.Message}", "CodexMonitor Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SaveSettings()
        {
            var config = _widget.LoadConfig(); // fresh copy: keep edits made elsewhere meanwhile

            var size = Sizes[Math.Max(0, _cmbSize.SelectedIndex)].Value;
            config.ScalePercent = size == "Custom" ? (double)_numScale.Value : 0;
            if (size != "Custom")
            {
                config.Profile = size;
            }
            config.FitToScreen = _chkFit.Checked;
            foreach (var (key, check) in _sections)
            {
                config.SetShowSection(key, check.Checked);
            }
            config.Visible = _chkVisible.Checked;
            config.TopProcesses = (int)_numTopRows.Value;

            config.GpuDevice = ((List<string>)_cmbGpu.Tag!)[Math.Max(0, _cmbGpu.SelectedIndex)];
            config.Fans = _lstFans.Items.Cast<ListViewItem>().Select(i => (FanEntry)i.Tag!).ToList();
            config.Temps = _lstTemps.Items.Cast<ListViewItem>().Select(i => (TempEntry)i.Tag!).ToList();
            config.Disks = _lstDrives.Items.Cast<ListViewItem>().Where(i => i.Checked)
                .Select(i => new DiskEntry((string)i.Tag!, string.IsNullOrWhiteSpace(i.Text) ? (string)i.Tag! : i.Text)).ToList();

            var network = config.Section("network");
            var ignoreTerms = _txtNetworkExclusions.Text.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList();
            foreach (var hidden in HiddenNetworkAdapterTerms)
            {
                AddTerm(ignoreTerms, hidden);
            }
            var ethernet = StringList(network, "ethernetNamesContaining");
            var wifi = StringList(network, "wifiNamesContaining");
            var wifiAp = StringList(network, "wifiApNamesContaining");
            foreach (var (name, role) in GetSelectedNetworkRoles())
            {
                foreach (var list in new[] { ethernet, wifi, wifiAp, ignoreTerms })
                {
                    list.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                }
                var target = role switch { "Ethernet" => ethernet, "Wi-Fi" => wifi, "Wi-Fi hotspot" => wifiAp, "Ignore" => ignoreTerms, _ => null };
                if (target is not null)
                {
                    AddTerm(target, name);
                }
            }
            JsonArray Array(List<string> items) => new(items.Select(x => (JsonNode)x).ToArray());
            network["ignoreAdaptersContaining"] = Array(ignoreTerms);
            network["ethernetNamesContaining"] = Array(ethernet);
            network["wifiNamesContaining"] = Array(wifi);
            network["wifiApNamesContaining"] = Array(wifiAp);
            network["internetDownMbps"] = (int)_numPlanDown.Value;
            network["internetUpMbps"] = (int)_numPlanUp.Value;
            network["lanMbps"] = (int)_numLanMbps.Value;

            config.UpdateMode = _cmbUpdateMode.SelectedIndex switch { 1 => "install", 2 => "off", _ => "notify" };
            var rate = System.Text.RegularExpressions.Regex.Match(_cmbUpdateRate.SelectedItem?.ToString() ?? "1", @"\d+");
            config.Section("bridge")["updateSeconds"] = rate.Success ? int.Parse(rate.Value) : 1;

            config.Save();
        }

        // ------------------------------------------------------------ network roles (unchanged behavior)

        private static void AddTerm(List<string> terms, string value)
        {
            if (!terms.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
            {
                terms.Add(value);
            }
        }

        private void PopulateNetworkAdapters()
        {
            _pnlNetworkAdapters.Controls.Clear();
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback && !IsHiddenNetworkAdapter(nic))
                .OrderBy(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .ThenBy(nic => nic.Name)
                .ToList();
            if (adapters.Count == 0)
            {
                _pnlNetworkAdapters.Controls.Add(new Label { Text = "No active network adapters detected.", Size = new Size(610, 28), ForeColor = TextMuted });
                return;
            }
            _pnlNetworkAdapters.AutoScroll = adapters.Count > 4;
            foreach (var adapter in adapters)
            {
                var row = new Panel { Size = new Size(640, 34), Margin = new Padding(0, 0, 0, 5), BackColor = Color.Transparent, Tag = adapter };
                row.Controls.Add(new Label { Text = GetNetworkAdapterDisplayName(adapter), Location = new Point(0, 4), Size = new Size(420, 24), AutoEllipsis = true, ForeColor = TextMain, Font = new Font("Segoe UI", 8.8f) });
                var role = Combo(452, 2, 176, NetworkRoles);
                role.Tag = adapter.Name;
                role.SelectedItem = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Auto";
                row.Controls.Add(role);
                _pnlNetworkAdapters.Controls.Add(row);
            }
        }

        private void ApplyNetworkRoles(JsonObject? network)
        {
            var ignore = StringList(network, "ignoreAdaptersContaining");
            var ethernet = StringList(network, "ethernetNamesContaining");
            var wifi = StringList(network, "wifiNamesContaining");
            var wifiAp = StringList(network, "wifiApNamesContaining");
            foreach (var row in _pnlNetworkAdapters.Controls.OfType<Panel>())
            {
                var role = row.Controls.OfType<ComboBox>().FirstOrDefault();
                if (role?.Tag is not string adapterName)
                {
                    continue;
                }
                if (ContainsTerm(ignore, adapterName)) role.SelectedItem = "Ignore";
                else if (ContainsTerm(wifiAp, adapterName)) role.SelectedItem = "Wi-Fi hotspot";
                else if (ContainsTerm(wifi, adapterName)) role.SelectedItem = "Wi-Fi";
                else if (ContainsTerm(ethernet, adapterName)) role.SelectedItem = "Ethernet";
            }
        }

        private static bool IsHiddenNetworkAdapter(NetworkInterface adapter)
        {
            var combined = $"{adapter.Name} {adapter.Description}";
            return HiddenNetworkAdapterTerms.Any(term => combined.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetNetworkAdapterDisplayName(NetworkInterface adapter)
        {
            var name = adapter.Name ?? "";
            var description = adapter.Description ?? "";
            return string.IsNullOrWhiteSpace(description) || name.Contains(description, StringComparison.OrdinalIgnoreCase) ? name : $"{name} - {description}";
        }

        private static bool ContainsTerm(IEnumerable<string> terms, string adapterName) =>
            terms.Any(term => adapterName.Contains(term, StringComparison.OrdinalIgnoreCase) || term.Contains(adapterName, StringComparison.OrdinalIgnoreCase));

        private List<(string Name, string Role)> GetSelectedNetworkRoles() =>
            _pnlNetworkAdapters.Controls.OfType<Panel>()
                .Select(row => row.Controls.OfType<ComboBox>().FirstOrDefault())
                .Where(role => role?.Tag is string && role.SelectedItem is not null)
                .Select(role => ((string)role!.Tag!, role.SelectedItem!.ToString() ?? "Auto"))
                .ToList();
    }
}
