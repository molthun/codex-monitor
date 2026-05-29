using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Forms;

namespace CodexBridge
{
    public class SettingsForm : Form
    {
        private readonly string _configPath;
        private readonly string _installRoot;
        private string _rainmeterExe = @"C:\Program Files\Rainmeter\Rainmeter.exe";

        private RadioButton _rdoAuto = null!;
        private RadioButton _rdo1080p = null!;
        private RadioButton _rdo4K = null!;
        private FlowLayoutPanel _pnlDrives = null!;
        private FlowLayoutPanel _pnlNetworkAdapters = null!;
        private TextBox _txtNetworkExclusions = null!;
        private CheckBox _chkAutoUpdate = null!;
        private ComboBox _cmbUpdateRate = null!;
        private Label _lblApplyStatus = null!;
        private Button _btnSave = null!;
        private Button _btnCancel = null!;

        private static readonly Color Back = Color.FromArgb(18, 20, 24);
        private static readonly Color Card = Color.FromArgb(27, 31, 36);
        private static readonly Color Border = Color.FromArgb(58, 68, 76);
        private static readonly Color TextMain = Color.FromArgb(238, 243, 247);
        private static readonly Color TextMuted = Color.FromArgb(166, 178, 188);
        private static readonly Color Accent = Color.FromArgb(0, 210, 230);
        private static readonly Color AccentSoft = Color.FromArgb(74, 226, 181);
        private static readonly string[] NetworkRoles = { "Auto", "Ethernet", "Wi-Fi", "Wi-Fi hotspot", "Ignore" };

        public SettingsForm(string configPath)
        {
            _configPath = configPath.Trim('"');

            var currentDir = Path.GetDirectoryName(_configPath);
            while (currentDir != null && !Directory.Exists(Path.Combine(currentDir, "Deploy")))
            {
                currentDir = Path.GetDirectoryName(currentDir);
            }
            _installRoot = currentDir ?? Path.GetDirectoryName(_configPath) ?? @"C:\CodexMonitor";

            InitializeComponent();
            LoadSettings();
        }

        private void InitializeComponent()
        {
            Text = "CodexMonitor Settings";
            ClientSize = new Size(760, 840);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Back;
            ForeColor = TextMain;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            var header = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(760, 96),
                BackColor = Color.FromArgb(12, 16, 21)
            };
            header.Paint += (s, e) =>
            {
                using var accentPen = new Pen(Accent, 2);
                e.Graphics.DrawLine(accentPen, 0, 94, 760, 94);
            };

            var title = new Label
            {
                Text = "CodexMonitor Settings",
                Location = new Point(24, 18),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 16.0f, FontStyle.Bold),
                ForeColor = Accent
            };
            var subtitle = new Label
            {
                Text = "Choose what the desktop widget displays. CodexMonitor only monitors hardware; fan control stays with your BIOS, drivers, or existing tools.",
                Location = new Point(24, 50),
                Size = new Size(700, 38),
                Font = new Font("Segoe UI", 9.25f, FontStyle.Regular),
                ForeColor = TextMuted
            };
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            Controls.Add(header);

            var body = new Panel
            {
                Location = new Point(0, 96),
                Size = new Size(760, 680),
                BackColor = Back
            };
            Controls.Add(body);

            var profile = CreateCard(28, 18, 704, 96, "Widget size", "Auto follows the primary display. Use a fixed size only if you prefer a specific layout.");
            _rdoAuto = CreateRadio("Auto, recommended", 18, 50, 160, true);
            _rdo1080p = CreateRadio("Compact 1080p", 240, 50, 150, false);
            _rdo4K = CreateRadio("Large 4K", 460, 50, 120, false);
            profile.Controls.Add(_rdoAuto);
            profile.Controls.Add(_rdo1080p);
            profile.Controls.Add(_rdo4K);
            body.Controls.Add(profile);

            var drives = CreateCard(28, 128, 704, 158, "Disk rows", "These drives are detected on this PC. A different PC will show its own local drives.");
            _pnlDrives = new FlowLayoutPanel
            {
                Location = new Point(18, 60),
                Size = new Size(668, 82),
                AutoScroll = true,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };
            drives.Controls.Add(_pnlDrives);
            body.Controls.Add(drives);

            var network = CreateCard(28, 300, 704, 280, "Network display", "Choose how active adapters are counted. Use Ignore for virtual, VPN, Bluetooth, or test adapters.");
            _pnlNetworkAdapters = new FlowLayoutPanel
            {
                Location = new Point(18, 60),
                Size = new Size(668, 144),
                AutoScroll = true,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };
            var advancedNetworkLabel = new Label
            {
                Text = "Advanced ignore words",
                Location = new Point(18, 214),
                Size = new Size(180, 20),
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Regular)
            };
            _txtNetworkExclusions = new TextBox
            {
                Location = new Point(18, 238),
                Size = new Size(668, 25),
                BackColor = Color.FromArgb(38, 44, 50),
                ForeColor = TextMain,
                BorderStyle = BorderStyle.FixedSingle
            };
            network.Controls.Add(_pnlNetworkAdapters);
            network.Controls.Add(advancedNetworkLabel);
            network.Controls.Add(_txtNetworkExclusions);
            body.Controls.Add(network);

            var update = CreateCard(28, 594, 704, 84, "Updates and sensor refresh", "Background updates download the latest widget files from GitHub. Sensor refresh controls how often telemetry is written.");
            _chkAutoUpdate = new CheckBox
            {
                Text = "Keep CodexMonitor updated automatically",
                Location = new Point(18, 50),
                Size = new Size(290, 25),
                Checked = true,
                ForeColor = TextMain
            };
            var rateLabel = new Label
            {
                Text = "Sensor refresh:",
                Location = new Point(472, 53),
                Size = new Size(100, 22),
                ForeColor = TextMain
            };
            _cmbUpdateRate = new ComboBox
            {
                Location = new Point(578, 49),
                Size = new Size(108, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(38, 44, 50),
                ForeColor = TextMain,
                FlatStyle = FlatStyle.Flat
            };
            _cmbUpdateRate.Items.AddRange(new object[] { "1 second", "2 seconds", "3 seconds", "5 seconds", "10 seconds", "30 seconds" });
            _cmbUpdateRate.SelectedIndex = 0;
            update.Controls.Add(_chkAutoUpdate);
            update.Controls.Add(rateLabel);
            update.Controls.Add(_cmbUpdateRate);
            body.Controls.Add(update);

            var footer = new Panel
            {
                Location = new Point(0, 776),
                Size = new Size(760, 64),
                BackColor = Color.FromArgb(12, 16, 21)
            };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, 760, 0);
            };
            _lblApplyStatus = new Label
            {
                Text = "Changes are saved to your local config.json and applied to the running widget.",
                Location = new Point(24, 22),
                Size = new Size(470, 24),
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 8.4f, FontStyle.Regular)
            };
            _btnSave = new Button
            {
                Text = "Save and apply",
                Size = new Size(132, 34),
                Location = new Point(490, 15),
                BackColor = Accent,
                ForeColor = Color.FromArgb(6, 12, 16),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnSave.FlatAppearance.BorderSize = 0;
            _btnSave.MouseEnter += (s, e) => _btnSave.BackColor = Color.FromArgb(58, 235, 250);
            _btnSave.MouseLeave += (s, e) => _btnSave.BackColor = Accent;
            _btnSave.Click += BtnSave_Click;

            _btnCancel = new Button
            {
                Text = "Cancel",
                Size = new Size(92, 34),
                Location = new Point(640, 15),
                BackColor = Color.FromArgb(43, 49, 56),
                ForeColor = TextMain,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnCancel.FlatAppearance.BorderColor = Border;
            _btnCancel.Click += (s, e) => Close();
            footer.Controls.Add(_lblApplyStatus);
            footer.Controls.Add(_btnSave);
            footer.Controls.Add(_btnCancel);
            Controls.Add(footer);
        }

        private Panel CreateCard(int x, int y, int width, int height, string title, string description)
        {
            var panel = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = Card
            };
            panel.Paint += (s, e) =>
            {
                using var pen = new Pen(Border, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, width - 1, height - 1);
            };
            panel.Controls.Add(new Label
            {
                Text = title,
                Location = new Point(18, 12),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 10.2f, FontStyle.Bold),
                ForeColor = Accent
            });
            panel.Controls.Add(new Label
            {
                Text = description,
                Location = new Point(18, 34),
                Size = new Size(width - 36, 20),
                Font = new Font("Segoe UI", 8.35f, FontStyle.Regular),
                ForeColor = TextMuted
            });
            return panel;
        }

        private RadioButton CreateRadio(string text, int x, int y, int width, bool isChecked)
        {
            return new RadioButton
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 26),
                Checked = isChecked,
                ForeColor = TextMain
            };
        }

        private void LoadSettings()
        {
            var availableDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)
                .OrderBy(d => d.Name)
                .ToList();

            foreach (var drive in availableDrives)
            {
                var rawPrefix = drive.Name.Substring(0, 2);
                var infoText = rawPrefix;
                try
                {
                    if (drive.IsReady)
                    {
                        var label = string.IsNullOrEmpty(drive.VolumeLabel) ? "" : $" [{drive.VolumeLabel}]";
                        var freeGb = drive.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0);
                        var totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                        infoText = $"{rawPrefix}{label} ({freeGb:F0} GB free of {totalGb:F0} GB)";
                    }
                }
                catch { }

                _pnlDrives.Controls.Add(new CheckBox
                {
                    Text = infoText,
                    Tag = rawPrefix,
                    Size = new Size(632, 25),
                    Margin = new Padding(0, 0, 0, 4),
                    ForeColor = TextMain
                });
            }

            PopulateNetworkAdapters();

            if (!File.Exists(_configPath))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_configPath));
                var rootElement = document.RootElement;

                if (rootElement.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object &&
                    profiles.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.String)
                {
                    var mode = def.GetString();
                    _rdo1080p.Checked = string.Equals(mode, "1080p", StringComparison.OrdinalIgnoreCase);
                    _rdo4K.Checked = string.Equals(mode, "4K", StringComparison.OrdinalIgnoreCase);
                    _rdoAuto.Checked = !_rdo1080p.Checked && !_rdo4K.Checked;
                }

                if (rootElement.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
                {
                    var configuredDrives = disks.EnumerateArray()
                        .Select(x => x.GetString()?.TrimEnd('\\') ?? "")
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToList();
                    foreach (CheckBox chk in _pnlDrives.Controls)
                    {
                        if (chk.Tag is string tag)
                        {
                            chk.Checked = configuredDrives.Contains(tag, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }

                if (rootElement.TryGetProperty("network", out var network) && network.ValueKind == JsonValueKind.Object &&
                    network.TryGetProperty("ignoreAdaptersContaining", out var ignore) && ignore.ValueKind == JsonValueKind.Array)
                {
                    _txtNetworkExclusions.Text = string.Join(", ", ignore.EnumerateArray().Select(x => x.GetString()!));
                }

                if (rootElement.TryGetProperty("network", out var roleNetwork) && roleNetwork.ValueKind == JsonValueKind.Object)
                {
                    ApplyNetworkRoles(roleNetwork);
                }

                if (rootElement.TryGetProperty("bridge", out var bridge) && bridge.ValueKind == JsonValueKind.Object &&
                    bridge.TryGetProperty("updateSeconds", out var seconds) && seconds.TryGetDouble(out var rate))
                {
                    var rateSec = Math.Max(1, (int)rate);
                    var targetText = rateSec == 1 ? "1 second" : $"{rateSec} seconds";
                    var idx = _cmbUpdateRate.FindStringExact(targetText);
                    if (idx < 0)
                    {
                        _cmbUpdateRate.Items.Add(targetText);
                        idx = _cmbUpdateRate.Items.Count - 1;
                    }
                    _cmbUpdateRate.SelectedIndex = idx;
                }

                if (rootElement.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.Object &&
                    display.TryGetProperty("autoUpdate", out var autoUpdate) &&
                    (autoUpdate.ValueKind == JsonValueKind.True || autoUpdate.ValueKind == JsonValueKind.False))
                {
                    _chkAutoUpdate.Checked = autoUpdate.GetBoolean();
                }

                if (rootElement.TryGetProperty("rainmeter", out var rainmeter) && rainmeter.ValueKind == JsonValueKind.Object &&
                    rainmeter.TryGetProperty("executable", out var executable) && executable.ValueKind == JsonValueKind.String)
                {
                    _rainmeterExe = executable.GetString() ?? _rainmeterExe;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load existing config: {ex.Message}", "Configuration Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            _btnSave.Enabled = false;
            _lblApplyStatus.Text = "Saving settings and applying them to the running widget...";
            try
            {
                var configDict = new Dictionary<string, object>();
                if (File.Exists(_configPath))
                {
                    configDict = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_configPath)) ?? configDict;
                }

                var mode = "Auto";
                if (_rdo1080p.Checked) mode = "1080p";
                else if (_rdo4K.Checked) mode = "4K";
                configDict["profiles"] = new Dictionary<string, object>
                {
                    { "auto", _rdoAuto.Checked },
                    { "default", mode },
                    { "compact", "1080p" },
                    { "large", "4K" }
                };

                var selectedDrives = new List<string>();
                foreach (CheckBox chk in _pnlDrives.Controls)
                {
                    if (chk.Checked && chk.Tag is string driveTag)
                    {
                        selectedDrives.Add(driveTag);
                    }
                }
                if (selectedDrives.Count == 0)
                {
                    throw new InvalidOperationException("Select at least one drive for the widget disk rows.");
                }
                if (selectedDrives.Count > 3)
                {
                    throw new InvalidOperationException("Select no more than three drives. The widget has three disk rows.");
                }
                configDict["disks"] = selectedDrives;

                var networkDict = ReadObject(configDict, "network");
                var ignoreTerms = _txtNetworkExclusions.Text.Split(',')
                    .Select(x => x.Trim().ToLowerInvariant())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToList();
                var ethernetNames = ReadStringList(networkDict, "ethernetNamesContaining");
                var wifiNames = ReadStringList(networkDict, "wifiNamesContaining");
                var wifiApNames = ReadStringList(networkDict, "wifiApNamesContaining");
                foreach (var role in GetSelectedNetworkRoles())
                {
                    RemoveTerm(ethernetNames, role.Name);
                    RemoveTerm(wifiNames, role.Name);
                    RemoveTerm(wifiApNames, role.Name);
                    RemoveTerm(ignoreTerms, role.Name);

                    if (role.Role == "Ethernet") AddTerm(ethernetNames, role.Name);
                    else if (role.Role == "Wi-Fi") AddTerm(wifiNames, role.Name);
                    else if (role.Role == "Wi-Fi hotspot") AddTerm(wifiApNames, role.Name);
                    else if (role.Role == "Ignore") AddTerm(ignoreTerms, role.Name);
                }
                networkDict["ignoreAdaptersContaining"] = ignoreTerms;
                networkDict["ethernetNamesContaining"] = ethernetNames;
                networkDict["wifiNamesContaining"] = wifiNames;
                networkDict["wifiApNamesContaining"] = wifiApNames;
                configDict["network"] = networkDict;

                var displayDict = ReadObject(configDict, "display");
                displayDict["autoUpdate"] = _chkAutoUpdate.Checked;
                configDict["display"] = displayDict;

                var bridgeDict = ReadObject(configDict, "bridge");
                bridgeDict["updateSeconds"] = GetSelectedRefreshSeconds();
                configDict["bridge"] = bridgeDict;

                var updatedJson = JsonSerializer.Serialize(configDict, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, updatedJson, System.Text.Encoding.UTF8);

                ApplyWidgetChanges(mode, GetBridgeTaskName(bridgeDict));

                _lblApplyStatus.Text = "Saved. The widget was refreshed and the hardware bridge was restarted.";
                MessageBox.Show("Settings saved and applied to the running widget.", "CodexMonitor Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        private static Dictionary<string, object> ReadObject(Dictionary<string, object> config, string key)
        {
            if (config.TryGetValue(key, out var existing) && existing is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText()) ?? new Dictionary<string, object>();
            }
            if (existing is Dictionary<string, object> typed)
            {
                return typed;
            }
            return new Dictionary<string, object>();
        }

        private static List<string> ReadStringList(Dictionary<string, object> config, string key)
        {
            if (config.TryGetValue(key, out var existing) && existing is JsonElement element && element.ValueKind == JsonValueKind.Array)
            {
                return element.EnumerateArray()
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .ToList();
            }
            if (existing is IEnumerable<string> typed)
            {
                return typed.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
            return new List<string>();
        }

        private static void AddTerm(List<string> terms, string value)
        {
            if (!terms.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
            {
                terms.Add(value);
            }
        }

        private static void RemoveTerm(List<string> terms, string value)
        {
            terms.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        }

        private void PopulateNetworkAdapters()
        {
            _pnlNetworkAdapters.Controls.Clear();
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderBy(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .ThenBy(nic => nic.Name)
                .ToList();

            if (adapters.Count == 0)
            {
                _pnlNetworkAdapters.Controls.Add(new Label
                {
                    Text = "No active network adapters detected.",
                    Size = new Size(610, 28),
                    ForeColor = TextMuted
                });
                return;
            }

            foreach (var adapter in adapters)
            {
                var row = new Panel
                {
                    Size = new Size(640, 34),
                    Margin = new Padding(0, 0, 0, 5),
                    BackColor = Color.Transparent,
                    Tag = adapter
                };
                var name = new Label
                {
                    Text = adapter.Name,
                    Location = new Point(0, 4),
                    Size = new Size(420, 24),
                    AutoEllipsis = true,
                    ForeColor = TextMain,
                    Font = new Font("Segoe UI", 8.8f, FontStyle.Regular)
                };
                var role = new ComboBox
                {
                    Location = new Point(452, 2),
                    Size = new Size(176, 25),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(38, 44, 50),
                    ForeColor = TextMain,
                    FlatStyle = FlatStyle.Flat,
                    Tag = adapter.Name
                };
                role.Items.AddRange(NetworkRoles.Cast<object>().ToArray());
                role.SelectedItem = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Auto";
                row.Controls.Add(name);
                row.Controls.Add(role);
                _pnlNetworkAdapters.Controls.Add(row);
            }
        }

        private void ApplyNetworkRoles(JsonElement network)
        {
            var ignore = ReadJsonStringList(network, "ignoreAdaptersContaining");
            var ethernet = ReadJsonStringList(network, "ethernetNamesContaining");
            var wifi = ReadJsonStringList(network, "wifiNamesContaining");
            var wifiAp = ReadJsonStringList(network, "wifiApNamesContaining");

            foreach (Panel row in _pnlNetworkAdapters.Controls.OfType<Panel>())
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

        private static List<string> ReadJsonStringList(JsonElement parent, string key)
        {
            if (!parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }
            return value.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList();
        }

        private static bool ContainsTerm(IEnumerable<string> terms, string adapterName)
        {
            return terms.Any(term => adapterName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     term.Contains(adapterName, StringComparison.OrdinalIgnoreCase));
        }

        private List<(string Name, string Role)> GetSelectedNetworkRoles()
        {
            var roles = new List<(string Name, string Role)>();
            foreach (Panel row in _pnlNetworkAdapters.Controls.OfType<Panel>())
            {
                var role = row.Controls.OfType<ComboBox>().FirstOrDefault();
                if (role?.Tag is string adapterName && role.SelectedItem is not null)
                {
                    roles.Add((adapterName, role.SelectedItem.ToString() ?? "Auto"));
                }
            }
            return roles;
        }

        private int GetSelectedRefreshSeconds()
        {
            var selectedText = _cmbUpdateRate.SelectedItem?.ToString() ?? "1 second";
            var match = System.Text.RegularExpressions.Regex.Match(selectedText, @"\d+");
            return match.Success && int.TryParse(match.Value, out var parsedRate) ? parsedRate : 1;
        }

        private static string GetBridgeTaskName(Dictionary<string, object> bridgeConfig)
        {
            if (bridgeConfig.TryGetValue("taskName", out var taskNameValue))
            {
                if (taskNameValue is string taskName && !string.IsNullOrWhiteSpace(taskName))
                {
                    return taskName;
                }
                if (taskNameValue is JsonElement element && element.ValueKind == JsonValueKind.String)
                {
                    return element.GetString() ?? "CodexMonitor Bridge Elevated";
                }
            }
            return "CodexMonitor Bridge Elevated";
        }

        private void ApplyWidgetChanges(string mode, string taskName)
        {
            var switcherScript = Path.Combine(_installRoot, @"Deploy\Switch-WidgetSize.ps1");
            if (File.Exists(switcherScript))
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{switcherScript}\" -Mode {mode} -InstallRoot \"{_installRoot}\" -ConfigPath \"{_configPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                proc?.WaitForExit(8000);
            }

            RestartBridgeTask(taskName);

            if (File.Exists(_rainmeterExe))
            {
                Process.Start(_rainmeterExe, "!Refresh CodexMonitor");
            }
        }

        private void RestartBridgeTask(string taskName)
        {
            using var stop = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/end /tn \"{taskName}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            stop?.WaitForExit(5000);

            using var start = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/run /tn \"{taskName}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            start?.WaitForExit(5000);
        }
    }
}
