using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using System.Text.Json;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Net.NetworkInformation;

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
        private TextBox _txtNetworkExclusions = null!;
        private CheckBox _chkAutoUpdate = null!;
        private ComboBox _cmbUpdateRate = null!;
        private Button _btnSave = null!;
        private Button _btnCancel = null!;

        public SettingsForm(string configPath)
        {
            _configPath = configPath.Trim('\"');

            // Traverse upwards to locate the install root containing the Deploy directory
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
            this.Text = "CodexMonitor Settings";
            this.ClientSize = new Size(480, 640);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(18, 18, 18);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9.75f, FontStyle.Regular);

            // Fetch active network adapters for guidance
            string activeAdapters = "Active adapters: None";
            try
            {
                var names = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                  nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .Select(nic => nic.Name)
                    .Distinct()
                    .ToList();
                if (names.Count > 0)
                {
                    activeAdapters = "Active: " + string.Join(", ", names);
                }
            }
            catch { }

            // Title Header Panel
            Panel pnlHeader = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(480, 65),
                BackColor = Color.FromArgb(26, 26, 26)
            };
            pnlHeader.Paint += (s, e) => {
                using var pen = new Pen(Color.FromArgb(0, 183, 195), 2);
                e.Graphics.DrawLine(pen, 0, 63, 480, 63); // Bottom accent line
            };
            Label lblTitle = new Label
            {
                Text = "CodexMonitor Setup Wizard",
                Font = new Font("Segoe UI Semibold", 14.25f, FontStyle.Bold),
                Location = new Point(20, 18),
                AutoSize = true,
                ForeColor = Color.FromArgb(0, 229, 255) // Vibrant accent
            };
            pnlHeader.Controls.Add(lblTitle);
            this.Controls.Add(pnlHeader);

            // Main Body Content Container
            Panel pnlBody = new Panel
            {
                Location = new Point(0, 65),
                Size = new Size(480, 515),
                BackColor = Color.FromArgb(18, 18, 18)
            };
            this.Controls.Add(pnlBody);

            // Card 1: Widget Size / Profile Mode
            Panel cardProfile = CreateCard(20, 12, 440, 75);
            Label lblProfileTitle = CreateCardTitle("1. Widget Size / Profile Mode", 15, 12);
            _rdoAuto = new RadioButton
            {
                Text = "Auto (Detect)",
                Location = new Point(15, 38),
                Size = new Size(130, 25),
                Checked = true
            };
            _rdo1080p = new RadioButton
            {
                Text = "1080p (Compact)",
                Location = new Point(150, 38),
                Size = new Size(140, 25)
            };
            _rdo4K = new RadioButton
            {
                Text = "4K (Large)",
                Location = new Point(295, 38),
                Size = new Size(110, 25)
            };
            cardProfile.Controls.Add(lblProfileTitle);
            cardProfile.Controls.Add(_rdoAuto);
            cardProfile.Controls.Add(_rdo1080p);
            cardProfile.Controls.Add(_rdo4K);
            pnlBody.Controls.Add(cardProfile);

            // Card 2: Hard Drives Selection
            Panel cardDrives = CreateCard(20, 97, 440, 150);
            Label lblDrivesTitle = CreateCardTitle("2. Storage Drives to Display", 15, 12);
            _pnlDrives = new FlowLayoutPanel
            {
                Location = new Point(15, 38),
                Size = new Size(410, 102),
                AutoScroll = true,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            cardDrives.Controls.Add(lblDrivesTitle);
            cardDrives.Controls.Add(_pnlDrives);
            pnlBody.Controls.Add(cardDrives);

            // Card 3: Network Exclusions
            Panel cardNetwork = CreateCard(20, 257, 440, 125);
            Label lblNetworkTitle = CreateCardTitle("3. Network Adapter Filters (Ignore list)", 15, 12);
            _txtNetworkExclusions = new TextBox
            {
                Location = new Point(15, 36),
                Size = new Size(410, 25),
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Label lblActiveNetwork = new Label
            {
                Text = activeAdapters,
                Font = new Font("Segoe UI", 8.0f, FontStyle.Regular),
                Location = new Point(15, 66),
                Size = new Size(410, 20),
                ForeColor = Color.FromArgb(0, 183, 195)
            };
            Label lblNetworkHelp = new Label
            {
                Text = "Comma-separated terms to filter virtual switches or ignored cards.",
                Font = new Font("Segoe UI", 8.0f, FontStyle.Italic),
                Location = new Point(15, 88),
                Size = new Size(410, 20),
                ForeColor = Color.DarkGray
            };
            cardNetwork.Controls.Add(lblNetworkTitle);
            cardNetwork.Controls.Add(_txtNetworkExclusions);
            cardNetwork.Controls.Add(lblActiveNetwork);
            cardNetwork.Controls.Add(lblNetworkHelp);
            pnlBody.Controls.Add(cardNetwork);

            // Card 4: Updates and Telemetry Rate
            Panel cardAdvanced = CreateCard(20, 392, 440, 110);
            Label lblAdvancedTitle = CreateCardTitle("4. Telemetry Update Rate and Updates", 15, 12);
            _chkAutoUpdate = new CheckBox
            {
                Text = "Enable background automatic updates",
                Location = new Point(15, 35),
                Size = new Size(410, 25),
                Checked = true
            };
            Label lblRate = new Label
            {
                Text = "Update interval:",
                Location = new Point(15, 71),
                Size = new Size(160, 25),
                ForeColor = Color.White
            };
            _cmbUpdateRate = new ComboBox
            {
                Location = new Point(180, 68),
                Size = new Size(120, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cmbUpdateRate.Items.AddRange(new object[] { "1 second", "2 seconds", "3 seconds", "5 seconds", "10 seconds", "30 seconds" });
            _cmbUpdateRate.SelectedIndex = 0;
            cardAdvanced.Controls.Add(lblAdvancedTitle);
            cardAdvanced.Controls.Add(_chkAutoUpdate);
            cardAdvanced.Controls.Add(lblRate);
            cardAdvanced.Controls.Add(_cmbUpdateRate);
            pnlBody.Controls.Add(cardAdvanced);

            // Bottom Command Button Panel
            Panel pnlBottom = new Panel
            {
                Location = new Point(0, 580),
                Size = new Size(480, 60),
                BackColor = Color.FromArgb(26, 26, 26)
            };
            pnlBottom.Paint += (s, e) => {
                using var pen = new Pen(Color.FromArgb(45, 45, 45), 1);
                e.Graphics.DrawLine(pen, 0, 0, 480, 0); // Top separator line
            };
            this.Controls.Add(pnlBottom);

            _btnSave = new Button
            {
                Text = "Save settings",
                Size = new Size(130, 32),
                Location = new Point(180, 14),
                BackColor = Color.FromArgb(0, 183, 195),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnSave.FlatAppearance.BorderSize = 0;
            // Hover effect
            _btnSave.MouseEnter += (s, e) => _btnSave.BackColor = Color.FromArgb(0, 229, 255);
            _btnSave.MouseLeave += (s, e) => _btnSave.BackColor = Color.FromArgb(0, 183, 195);
            _btnSave.Click += BtnSave_Click;

            _btnCancel = new Button
            {
                Text = "Cancel",
                Size = new Size(100, 32),
                Location = new Point(330, 14),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnCancel.FlatAppearance.BorderSize = 1;
            _btnCancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
            _btnCancel.MouseEnter += (s, e) => {
                _btnCancel.BackColor = Color.FromArgb(70, 70, 70);
                _btnCancel.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            };
            _btnCancel.MouseLeave += (s, e) => {
                _btnCancel.BackColor = Color.FromArgb(50, 50, 50);
                _btnCancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
            };
            _btnCancel.Click += (s, e) => this.Close();

            pnlBottom.Controls.Add(_btnSave);
            pnlBottom.Controls.Add(_btnCancel);
        }

        private Panel CreateCard(int x, int y, int width, int height)
        {
            var pnl = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = Color.FromArgb(24, 24, 24)
            };
            pnl.Paint += (s, e) => {
                using var pen = new Pen(Color.FromArgb(48, 48, 48), 1);
                e.Graphics.DrawRectangle(pen, 0, 0, width - 1, height - 1);
            };
            return pnl;
        }

        private Label CreateCardTitle(string title, int x, int y)
        {
            return new Label
            {
                Text = title,
                Location = new Point(x, y),
                Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 183, 195),
                AutoSize = true
            };
        }

        private void LoadSettings()
        {
            // Populate Drives Panel dynamically
            var availableDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)
                .OrderBy(d => d.Name)
                .ToList();

            foreach (var drive in availableDrives)
            {
                string rawPrefix = drive.Name.Substring(0, 2); // e.g., "C:"
                string infoText = rawPrefix;
                try
                {
                    if (drive.IsReady)
                    {
                        string label = string.IsNullOrEmpty(drive.VolumeLabel) ? "" : $" [{drive.VolumeLabel}]";
                        double freeGb = drive.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0);
                        double totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                        infoText = $"{rawPrefix}{label} ({freeGb:F0} GB free of {totalGb:F0} GB)";
                    }
                }
                catch { }

                var chk = new CheckBox
                {
                    Text = infoText,
                    Tag = rawPrefix,
                    Size = new Size(380, 25),
                    Margin = new Padding(0, 2, 0, 2),
                    ForeColor = Color.White
                };
                _pnlDrives.Controls.Add(chk);
            }

            if (!File.Exists(_configPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_configPath);
                using var document = JsonDocument.Parse(json);
                var rootElement = document.RootElement;

                // 1. Load Profile mode
                if (rootElement.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
                {
                    if (profiles.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.String)
                    {
                        var mode = def.GetString();
                        if (string.Equals(mode, "1080p", StringComparison.OrdinalIgnoreCase))
                        {
                            _rdo1080p.Checked = true;
                        }
                        else if (string.Equals(mode, "4K", StringComparison.OrdinalIgnoreCase))
                        {
                            _rdo4K.Checked = true;
                        }
                        else
                        {
                            _rdoAuto.Checked = true;
                        }
                    }
                }

                // 2. Set drive checkboxes
                if (rootElement.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
                {
                    var configuredDrives = disks.EnumerateArray()
                        .Select(x => x.GetString()?.TrimEnd('\\') ?? "")
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToList();
                    foreach (CheckBox chk in _pnlDrives.Controls)
                    {
                        var tag = chk.Tag as string;
                        if (tag != null)
                        {
                            chk.Checked = configuredDrives.Contains(tag, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }

                // 3. Network ignore list
                if (rootElement.TryGetProperty("network", out var network) && network.ValueKind == JsonValueKind.Object)
                {
                    if (network.TryGetProperty("ignoreAdaptersContaining", out var ignore) && ignore.ValueKind == JsonValueKind.Array)
                    {
                        var list = ignore.EnumerateArray().Select(x => x.GetString()!).ToList();
                        _txtNetworkExclusions.Text = string.Join(", ", list);
                    }
                }

                // 4. Update Rate
                if (rootElement.TryGetProperty("bridge", out var bridge) && bridge.ValueKind == JsonValueKind.Object)
                {
                    if (bridge.TryGetProperty("updateSeconds", out var seconds) && seconds.TryGetDouble(out var rate))
                    {
                        int rateSec = Math.Max(1, (int)rate);
                        string targetText = rateSec == 1 ? "1 second" : $"{rateSec} seconds";
                        int idx = _cmbUpdateRate.FindStringExact(targetText);
                        if (idx >= 0)
                        {
                            _cmbUpdateRate.SelectedIndex = idx;
                        }
                        else
                        {
                            _cmbUpdateRate.Items.Add(targetText);
                            _cmbUpdateRate.SelectedIndex = _cmbUpdateRate.Items.Count - 1;
                        }
                    }
                }

                // 5. Auto Updates
                if (rootElement.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.Object)
                {
                    if (display.TryGetProperty("autoUpdate", out var autoUpdate) && (autoUpdate.ValueKind == JsonValueKind.True || autoUpdate.ValueKind == JsonValueKind.False))
                    {
                        _chkAutoUpdate.Checked = autoUpdate.GetBoolean();
                    }
                }

                // 6. Rainmeter path
                if (rootElement.TryGetProperty("rainmeter", out var rainmeter) && rainmeter.ValueKind == JsonValueKind.Object)
                {
                    if (rainmeter.TryGetProperty("executable", out var executable) && executable.ValueKind == JsonValueKind.String)
                    {
                        _rainmeterExe = executable.GetString() ?? _rainmeterExe;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load existing config: {ex.Message}", "Configuration Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            try
            {
                var configDict = new Dictionary<string, object>();
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    configDict = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? configDict;
                }

                // Set profiles object
                var profilesDict = new Dictionary<string, object>
                {
                    { "auto", _rdoAuto.Checked }
                };
                var mode = "Auto";
                if (_rdo1080p.Checked) mode = "1080p";
                else if (_rdo4K.Checked) mode = "4K";
                profilesDict.Add("default", mode);
                profilesDict.Add("compact", "1080p");
                profilesDict.Add("large", "4K");
                configDict["profiles"] = profilesDict;

                // Set disks array
                var selectedDrives = new List<string>();
                foreach (CheckBox chk in _pnlDrives.Controls)
                {
                    if (chk.Checked && chk.Tag is string driveTag)
                    {
                        selectedDrives.Add(driveTag + "\\");
                    }
                }
                if (selectedDrives.Count == 0)
                {
                    selectedDrives.Add("C:\\"); // Fallback
                }
                configDict["disks"] = selectedDrives;

                // Set network object
                var networkDict = new Dictionary<string, object>();
                if (configDict.ContainsKey("network") && configDict["network"] is JsonElement netElement && netElement.ValueKind == JsonValueKind.Object)
                {
                    networkDict = JsonSerializer.Deserialize<Dictionary<string, object>>(netElement.GetRawText()) ?? networkDict;
                }
                var ignoreList = _txtNetworkExclusions.Text.Split(',')
                    .Select(x => x.Trim().ToLower())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToList();
                networkDict["ignoreAdaptersContaining"] = ignoreList;
                configDict["network"] = networkDict;

                // Set display object
                var displayDict = new Dictionary<string, object>();
                if (configDict.ContainsKey("display") && configDict["display"] is JsonElement dispElement && dispElement.ValueKind == JsonValueKind.Object)
                {
                    displayDict = JsonSerializer.Deserialize<Dictionary<string, object>>(dispElement.GetRawText()) ?? displayDict;
                }
                displayDict["autoUpdate"] = _chkAutoUpdate.Checked;
                configDict["display"] = displayDict;

                // Set bridge object
                var bridgeDict = new Dictionary<string, object>();
                if (configDict.ContainsKey("bridge") && configDict["bridge"] is JsonElement briElement && briElement.ValueKind == JsonValueKind.Object)
                {
                    bridgeDict = JsonSerializer.Deserialize<Dictionary<string, object>>(briElement.GetRawText()) ?? bridgeDict;
                }

                int rateSeconds = 1;
                if (_cmbUpdateRate.SelectedItem != null)
                {
                    string selectedText = _cmbUpdateRate.SelectedItem.ToString() ?? "";
                    var match = System.Text.RegularExpressions.Regex.Match(selectedText, @"\d+");
                    if (match.Success && int.TryParse(match.Value, out int parsedRate))
                    {
                        rateSeconds = parsedRate;
                    }
                }
                bridgeDict["updateSeconds"] = rateSeconds;
                configDict["bridge"] = bridgeDict;

                // Save back with UTF-8 encoding
                var serializeOptions = new JsonSerializerOptions { WriteIndented = true };
                var updatedJson = JsonSerializer.Serialize(configDict, serializeOptions);
                File.WriteAllText(_configPath, updatedJson, System.Text.Encoding.UTF8);

                // Run Switch-WidgetSize.ps1
                var switcherScript = Path.Combine(_installRoot, @"Deploy\Switch-WidgetSize.ps1");
                if (File.Exists(switcherScript))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{switcherScript}\" -Mode {mode} -InstallRoot \"{_installRoot}\" -ConfigPath \"{_configPath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);
                }

                // Refresh Rainmeter Config
                if (File.Exists(_rainmeterExe))
                {
                    Process.Start(_rainmeterExe, "!Refresh CodexMonitor");
                }

                MessageBox.Show("Configuration updated and widget size profile applied successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Error Saving Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
