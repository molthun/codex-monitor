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
            this.Size = new Size(480, 560);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(28, 28, 28);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9.75f, FontStyle.Regular);

            // Title Header Panel
            Panel pnlHeader = new Panel
            {
                Size = new Size(480, 60),
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(36, 36, 36)
            };
            Label lblTitle = new Label
            {
                Text = "CodexMonitor Setup Wizard",
                Font = new Font("Segoe UI Semibold", 14.25f, FontStyle.Bold),
                Location = new Point(16, 16),
                AutoSize = true,
                ForeColor = Color.FromArgb(0, 183, 195) // Accent color
            };
            pnlHeader.Controls.Add(lblTitle);
            this.Controls.Add(pnlHeader);

            // Main Body Content Container
            Panel pnlBody = new Panel
            {
                Size = new Size(480, 410),
                Dock = DockStyle.Fill,
                Padding = new Padding(20)
            };
            this.Controls.Add(pnlBody);

            int yOffset = 10;

            // Group 1: Display Scaling / Profile
            GroupBox grpProfile = CreateGroupBox("1. Widget Size / Profile Mode", yOffset, 80);
            _cmbProfile = new ComboBox
            {
                Location = new Point(15, 30),
                Size = new Size(390, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cmbProfile.Items.AddRange(new object[] { "Auto (Detect resolution)", "1080p (Compact size)", "4K (Large size)" });
            _cmbProfile.SelectedIndex = 0;
            grpProfile.Controls.Add(_cmbProfile);
            pnlBody.Controls.Add(grpProfile);
            yOffset += 95;

            // Group 2: Hard Drives Selection
            GroupBox grpDrives = CreateGroupBox("2. Storage Drives to Display", yOffset, 95);
            _pnlDrives = new FlowLayoutPanel
            {
                Location = new Point(15, 30),
                Size = new Size(390, 55),
                AutoScroll = true
            };
            grpDrives.Controls.Add(_pnlDrives);
            pnlBody.Controls.Add(grpDrives);
            yOffset += 110;

            // Group 3: Network Exclusions
            GroupBox grpNetwork = CreateGroupBox("3. Network Adapter Filters (Ignore list)", yOffset, 90);
            _txtNetworkExclusions = new TextBox
            {
                Location = new Point(15, 30),
                Size = new Size(390, 25),
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Label lblNetworkHelp = new Label
            {
                Text = "Comma-separated terms to filter virtual switches or ignored cards.",
                Font = new Font("Segoe UI", 8.25f, FontStyle.Italic),
                Location = new Point(15, 60),
                Size = new Size(390, 20),
                ForeColor = Color.DarkGray
            };
            grpNetwork.Controls.Add(_txtNetworkExclusions);
            grpNetwork.Controls.Add(lblNetworkHelp);
            pnlBody.Controls.Add(grpNetwork);
            yOffset += 105;

            // Group 4: Updates & Settings
            GroupBox grpAdvanced = CreateGroupBox("4. Telemetry Update Rate & Updates", yOffset, 75);
            _chkAutoUpdate = new CheckBox
            {
                Text = "Enable background automatic updates",
                Location = new Point(15, 25),
                Size = new Size(250, 25),
                FlatStyle = FlatStyle.Flat,
                Checked = true
            };
            Label lblRate = new Label
            {
                Text = "Update seconds:",
                Location = new Point(275, 27),
                Size = new Size(95, 25)
            };
            _numUpdateRate = new NumericUpDown
            {
                Location = new Point(370, 25),
                Size = new Size(45, 25),
                Minimum = 1,
                Maximum = 60,
                Value = 1,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            grpAdvanced.Controls.Add(_chkAutoUpdate);
            grpAdvanced.Controls.Add(lblRate);
            grpAdvanced.Controls.Add(_numUpdateRate);
            pnlBody.Controls.Add(grpAdvanced);

            // Bottom Command Button Panel
            Panel pnlBottom = new Panel
            {
                Size = new Size(480, 60),
                Dock = DockStyle.Bottom,
                BackColor = Color.FromArgb(36, 36, 36)
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
            _btnCancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
            _btnCancel.Click += (s, e) => this.Close();

            pnlBottom.Controls.Add(_btnSave);
            pnlBottom.Controls.Add(_btnCancel);
        }

        private GroupBox CreateGroupBox(string title, int y, int height)
        {
            return new GroupBox
            {
                Text = title,
                Location = new Point(20, y),
                Size = new Size(420, height),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
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
