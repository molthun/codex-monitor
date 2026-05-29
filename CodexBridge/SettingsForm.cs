using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using System.Text.Json;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;

namespace CodexBridge
{
    public class SettingsForm : Form
    {
        private readonly string _configPath;
        private readonly string _installRoot;
        private string _rainmeterExe = @"C:\Program Files\Rainmeter\Rainmeter.exe";

        private ComboBox _cmbProfile = null!;
        private FlowLayoutPanel _pnlDrives = null!;
        private TextBox _txtNetworkExclusions = null!;
        private CheckBox _chkAutoUpdate = null!;
        private NumericUpDown _numUpdateRate = null!;
        private Button _btnSave = null!;
        private Button _btnCancel = null!;

        public SettingsForm(string configPath)
        {
            _configPath = configPath;

            // Traverse upwards to locate the install root containing the Deploy directory
            var currentDir = Path.GetDirectoryName(configPath);
            while (currentDir != null && !Directory.Exists(Path.Combine(currentDir, "Deploy")))
            {
                currentDir = Path.GetDirectoryName(currentDir);
            }
            _installRoot = currentDir ?? Path.GetDirectoryName(configPath) ?? @"C:\CodexMonitor";

            InitializeComponent();
            LoadSettings();
        }

        private void InitializeComponent()
        {
            this.Text = "CodexMonitor Settings";
            this.ClientSize = new Size(480, 540);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(18, 18, 18);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9.75f, FontStyle.Regular);

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
                Size = new Size(480, 415),
                BackColor = Color.FromArgb(18, 18, 18)
            };
            this.Controls.Add(pnlBody);

            // Card 1: Widget Size / Profile Mode
            Panel cardProfile = CreateCard(20, 12, 440, 80);
            Label lblProfileTitle = CreateCardTitle("1. Widget Size / Profile Mode", 15, 12);
            _cmbProfile = new ComboBox
            {
                Location = new Point(15, 38),
                Size = new Size(410, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cmbProfile.Items.AddRange(new object[] { "Auto (Detect resolution)", "1080p (Compact size)", "4K (Large size)" });
            _cmbProfile.SelectedIndex = 0;
            cardProfile.Controls.Add(lblProfileTitle);
            cardProfile.Controls.Add(_cmbProfile);
            pnlBody.Controls.Add(cardProfile);

            // Card 2: Hard Drives Selection
            Panel cardDrives = CreateCard(20, 104, 440, 85);
            Label lblDrivesTitle = CreateCardTitle("2. Storage Drives to Display", 15, 12);
            _pnlDrives = new FlowLayoutPanel
            {
                Location = new Point(15, 38),
                Size = new Size(410, 38),
                AutoScroll = true,
                BackColor = Color.Transparent
            };
            cardDrives.Controls.Add(lblDrivesTitle);
            cardDrives.Controls.Add(_pnlDrives);
            pnlBody.Controls.Add(cardDrives);

            // Card 3: Network Exclusions
            Panel cardNetwork = CreateCard(20, 201, 440, 95);
            Label lblNetworkTitle = CreateCardTitle("3. Network Adapter Filters (Ignore list)", 15, 12);
            _txtNetworkExclusions = new TextBox
            {
                Location = new Point(15, 38),
                Size = new Size(410, 25),
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Label lblNetworkHelp = new Label
            {
                Text = "Comma-separated terms to filter virtual switches or ignored cards.",
                Font = new Font("Segoe UI", 8.0f, FontStyle.Italic),
                Location = new Point(15, 68),
                Size = new Size(410, 20),
                ForeColor = Color.DarkGray
            };
            cardNetwork.Controls.Add(lblNetworkTitle);
            cardNetwork.Controls.Add(_txtNetworkExclusions);
            cardNetwork.Controls.Add(lblNetworkHelp);
            pnlBody.Controls.Add(cardNetwork);

            // Card 4: Updates & Telemetry Rate
            Panel cardAdvanced = CreateCard(20, 308, 440, 90);
            Label lblAdvancedTitle = CreateCardTitle("4. Telemetry Update Rate & Updates", 15, 12);
            _chkAutoUpdate = new CheckBox
            {
                Text = "Enable background automatic updates",
                Location = new Point(15, 42),
                Size = new Size(250, 25),
                FlatStyle = FlatStyle.Flat,
                Checked = true
            };
            Label lblRate = new Label
            {
                Text = "Update interval (sec):",
                Location = new Point(265, 44),
                Size = new Size(125, 25),
                ForeColor = Color.White
            };
            _numUpdateRate = new NumericUpDown
            {
                Location = new Point(380, 42),
                Size = new Size(45, 25),
                Minimum = 1,
                Maximum = 60,
                Value = 1,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            cardAdvanced.Controls.Add(lblAdvancedTitle);
            cardAdvanced.Controls.Add(_chkAutoUpdate);
            cardAdvanced.Controls.Add(lblRate);
            cardAdvanced.Controls.Add(_numUpdateRate);
            pnlBody.Controls.Add(cardAdvanced);

            // Bottom Command Button Panel
            Panel pnlBottom = new Panel
            {
                Location = new Point(0, 480),
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
                .Select(d => d.Name.Substring(0, 2))
                .OrderBy(name => name)
                .ToList();

            foreach (var drive in availableDrives)
            {
                var chk = new CheckBox
                {
                    Text = drive,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 15, 0),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
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

                // 1. Load Profile indexes
                if (rootElement.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
                {
                    if (profiles.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.String)
                    {
                        var mode = def.GetString();
                        if (string.Equals(mode, "1080p", StringComparison.OrdinalIgnoreCase)) _cmbProfile.SelectedIndex = 1;
                        else if (string.Equals(mode, "4K", StringComparison.OrdinalIgnoreCase)) _cmbProfile.SelectedIndex = 2;
                        else _cmbProfile.SelectedIndex = 0;
                    }
                }

                // 2. Set drive checkboxes
                if (rootElement.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
                {
                    var configuredDrives = disks.EnumerateArray().Select(x => x.GetString()!.Substring(0, 2)).ToList();
                    foreach (CheckBox chk in _pnlDrives.Controls)
                    {
                        chk.Checked = configuredDrives.Contains(chk.Text, StringComparer.OrdinalIgnoreCase);
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
                        _numUpdateRate.Value = Math.Max(1, (int)rate);
                    }
                }

                // 5. Auto Updates
                if (rootElement.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.Object)
                {
                    if (display.TryGetProperty("autoUpdate", out var autoUpdate) && autoUpdate.ValueKind == JsonValueKind.True || autoUpdate.ValueKind == JsonValueKind.False)
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
                    { "auto", _cmbProfile.SelectedIndex == 0 }
                };
                var mode = "Auto";
                if (_cmbProfile.SelectedIndex == 1) mode = "1080p";
                else if (_cmbProfile.SelectedIndex == 2) mode = "4K";
                profilesDict.Add("default", mode);
                profilesDict.Add("compact", "1080p");
                profilesDict.Add("large", "4K");
                configDict["profiles"] = profilesDict;

                // Set disks array
                var selectedDrives = new List<string>();
                foreach (CheckBox chk in _pnlDrives.Controls)
                {
                    if (chk.Checked)
                    {
                        selectedDrives.Add(chk.Text + "\\");
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
                bridgeDict["updateSeconds"] = (int)_numUpdateRate.Value;
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
