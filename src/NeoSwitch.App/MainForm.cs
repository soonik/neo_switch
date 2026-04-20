using System.Diagnostics;
using HidSharp;
using NeoSwitch.Core;

namespace NeoSwitch.App;

public sealed class MainForm : Form
{
    private readonly RuntimeController _runtime;
    private readonly SettingsStore _store;
    private readonly SynchronizationContext _ui;
    private Settings S => _store.Current;

    // Header
    private readonly Label _statusDot = new() { AutoSize = true, Font = new Font("Segoe UI", 14f), Text = "●" };
    private readonly ComboBox _cbDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly Label _kbInfo  = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "" };
    private readonly Label _kbProfile = new() { AutoSize = true, Text = "" };
    private readonly Button _btnReconnect = new() { Text = "Reconnect", AutoSize = true };

    // Device-picker state
    private bool _cbDeviceSuppressEvent;
    private readonly List<DeviceChoice> _deviceChoices = new();

    private sealed record DeviceChoice(int? VendorId, int? ProductId, string Display)
    {
        public override string ToString() => Display;
    }

    // Watched apps
    private readonly ListBox _listApps = new() { IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
    private readonly Button _btnAddFile = new() { Text = "Add from file…", AutoSize = true };
    private readonly Button _btnAddRunning = new() { Text = "Add running process…", AutoSize = true };
    private readonly Button _btnRemove = new() { Text = "Remove", AutoSize = true };

    // Profile mapping
    private readonly NumericUpDown _nudFg = new() { Minimum = 0, Maximum = 15, Width = 60 };
    private readonly NumericUpDown _nudBg = new() { Minimum = 0, Maximum = 15, Width = 60 };
    private readonly Label _fgPreview = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _bgPreview = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly CheckBox _cbPause = new() { Text = "Pause switching", AutoSize = true };
    private readonly CheckBox _cbStartup = new() { Text = "Start with Windows", AutoSize = true };
    private readonly NumericUpDown _nudSwitchDelay = new() { Minimum = 0, Maximum = 5000, Increment = 50, Width = 80 };
    private readonly NumericUpDown _nudGateTimeout = new() { Minimum = 0, Maximum = 10000, Increment = 100, Width = 80 };

    // Footer
    private readonly Label _statusFooter = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
        Text = "(no events yet)",
    };

    public MainForm(RuntimeController runtime, SettingsStore store, SynchronizationContext ui)
    {
        _runtime = runtime;
        _store   = store;
        _ui      = ui;

        Text = "NeoSwitch";
        ClientSize = new Size(1000, 600);
        MinimumSize = new Size(780, 480);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;

        BuildLayout();
        WireEvents();
        LoadFromSettings();
        RefreshUiFromRuntime();
    }

    // ---------------- layout ----------------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));

        var header = BuildHeader();
        var body   = BuildBody();
        var footer = BuildFooter();
        header.Dock = DockStyle.Fill;
        body.Dock   = DockStyle.Fill;
        footer.Dock = DockStyle.Fill;

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body,   0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var p = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 5,
            Height = 64,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = SystemColors.ControlLight,
        };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var namePanel = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 2, AutoSize = true,
            Margin = new Padding(8, 0, 0, 0),
        };
        _cbDevice.Margin = new Padding(0, 0, 0, 2);
        namePanel.Controls.Add(_cbDevice, 0, 0);
        namePanel.Controls.Add(_kbInfo,   0, 1);

        _statusDot.ForeColor = Color.Gray;
        _statusDot.Margin = new Padding(0, 4, 0, 0);

        p.Controls.Add(_statusDot,  0, 0);
        p.Controls.Add(namePanel,   1, 0);
        p.Controls.Add(_kbProfile,  2, 0);
        _kbProfile.Anchor = AnchorStyles.Right;
        _kbProfile.Margin = new Padding(0, 10, 12, 0);
        p.Controls.Add(_btnReconnect, 3, 0);
        return p;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            // Panel{1,2}MinSize intentionally NOT set in the initializer:
            // SplitContainer's default Width is ~150, and settings that violate
            // Panel1MinSize + Panel2MinSize + SplitterWidth <= Width put the
            // control into a state where every later SplitterDistance=N
            // assignment throws. Configure after first layout, under try/catch.
        };

        this.Shown += (_, _) =>
        {
            try
            {
                int w = split.Width;
                int sw = split.SplitterWidth;
                const int p1Min = 280;
                const int p2Min = 360;
                if (w < p1Min + p2Min + sw) return;  // too narrow for custom sizing
                split.Panel1MinSize = p1Min;
                split.Panel2MinSize = p2Min;
                int target = Math.Clamp(w - 420, p1Min, w - p2Min - sw);
                split.SplitterDistance = target;
            }
            catch { /* fall back to defaults — better than crashing */ }
        };

        // ---- left: watched apps ----
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lblApps = new Label { Text = "WATCHED APPLICATIONS", AutoSize = true, Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6) };
        left.Controls.Add(lblApps, 0, 0);
        _listApps.Dock = DockStyle.Fill;
        left.Controls.Add(_listApps, 0, 1);
        var btnRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        btnRow.Controls.Add(_btnAddFile);
        btnRow.Controls.Add(_btnAddRunning);
        btnRow.Controls.Add(_btnRemove);
        left.Controls.Add(btnRow, 0, 2);
        split.Panel1.Controls.Add(left);

        // ---- right: profile mapping ----
        // AutoScroll lets narrow windows show a scrollbar instead of clipping.
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = false,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        right.Controls.Add(new Label
        {
            Text = "PROFILE MAPPING", AutoSize = true, Font = new Font("Segoe UI Semibold", 8.5f),
            ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 10),
        });
        right.Controls.Add(MakeFieldRow("Foreground profile", _nudFg, _fgPreview, "when a watched app is focused"));
        right.Controls.Add(MakeFieldRow("Background profile", _nudBg, _bgPreview, "when nothing watched is focused"));
        right.Controls.Add(MakeFieldRow("Switch delay (ms)", _nudSwitchDelay, null, "debounce between foreground change and HID write"));
        right.Controls.Add(MakeFieldRow("Gate timeout (ms)", _nudGateTimeout, null, "max wait for modifier keys to release"));
        right.Controls.Add(_cbPause);
        _cbStartup.Margin = new Padding(0, 6, 0, 0);
        right.Controls.Add(_cbStartup);
        split.Panel2.Controls.Add(right);

        return split;
    }

    private Control MakeFieldRow(string label, Control input, Control? preview, string hint)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var lbl = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 5, 6, 0) };
        row.Controls.Add(lbl, 0, 0);
        input.Margin = new Padding(0, 0, 10, 0);
        row.Controls.Add(input, 1, 0);
        if (preview != null)
        {
            preview.Margin = new Padding(0, 5, 0, 0);
            row.Controls.Add(preview, 2, 0);
        }
        var hintLbl = new Label
        {
            Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 0),
        };
        row.SetColumnSpan(hintLbl, 3);
        row.RowCount = 2;
        row.Controls.Add(hintLbl, 0, 1);
        return row;
    }

    private Control BuildFooter()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Control };
        _statusFooter.Dock = DockStyle.Fill;
        _statusFooter.Padding = new Padding(12, 0, 12, 0);
        p.Controls.Add(_statusFooter);
        return p;
    }

    // ---------------- events ----------------

    private void WireEvents()
    {
        _btnReconnect.Click += (_, _) => _runtime.Reconnect();

        _btnAddFile.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                Multiselect = true,
                Title = "Add watched application",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (var path in dlg.FileNames)
            {
                var exe = Path.GetFileName(path).ToLowerInvariant();
                if (!S.WatchedApps.Contains(exe)) S.WatchedApps.Add(exe);
            }
            SaveAndReloadApps();
        };

        _btnAddRunning.Click += (_, _) =>
        {
            using var dlg = new PickRunningDialog();
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (var exe in dlg.SelectedExecutables)
            {
                var e = exe.ToLowerInvariant();
                if (!S.WatchedApps.Contains(e)) S.WatchedApps.Add(e);
            }
            SaveAndReloadApps();
        };

        _btnRemove.Click += (_, _) =>
        {
            var selected = _listApps.SelectedItems.Cast<string>().ToArray();
            foreach (var s in selected) S.WatchedApps.Remove(s);
            SaveAndReloadApps();
        };

        _nudFg.ValueChanged += (_, _) => { S.ForegroundProfile = (byte)_nudFg.Value; _store.Save(); _runtime.ApplySettings(); UpdateProfilePreview(); };
        _nudBg.ValueChanged += (_, _) => { S.BackgroundProfile = (byte)_nudBg.Value; _store.Save(); _runtime.ApplySettings(); UpdateProfilePreview(); };

        _nudSwitchDelay.ValueChanged += (_, _) => { S.SwitchDelayMs = (int)_nudSwitchDelay.Value; _store.Save(); };
        _nudGateTimeout.ValueChanged += (_, _) => { S.GateTimeoutMs = (int)_nudGateTimeout.Value; _store.Save(); };

        _cbPause.CheckedChanged += (_, _) =>
        {
            if (S.Paused == _cbPause.Checked) return;
            _runtime.TogglePause();
            _store.Save();
        };

        _cbDevice.SelectedIndexChanged += (_, _) =>
        {
            if (_cbDeviceSuppressEvent) return;
            int i = _cbDevice.SelectedIndex;
            if (i < 0 || i >= _deviceChoices.Count) return;
            var choice = _deviceChoices[i];
            if (S.VendorId == choice.VendorId && S.ProductId == choice.ProductId) return;
            S.VendorId  = choice.VendorId;
            S.ProductId = choice.ProductId;
            _store.Save();
            _runtime.Reconnect();
        };

        _cbStartup.CheckedChanged += (_, _) =>
        {
            if (!OperatingSystem.IsWindows()) return;
            bool desired = _cbStartup.Checked;
            bool ok = StartupRegistrar.SetEnabled(desired);
            S.StartWithWindows = ok && desired;
            _store.Save();
            if (!ok)
            {
                _cbDeviceSuppressEvent = true;
                _cbStartup.Checked = StartupRegistrar.IsEnabled();
                _cbDeviceSuppressEvent = false;
            }
        };

        _runtime.StateChanged += () =>
        {
            if (IsDisposed) return;
            _ui.Post(_ => { if (!IsDisposed) RefreshUiFromRuntime(); }, null);
        };
        _runtime.LogLine += line =>
        {
            if (IsDisposed) return;
            _ui.Post(_ => { if (!IsDisposed) AppendLog(line); }, null);
        };
        _runtime.DevicesChanged += () =>
        {
            if (IsDisposed) return;
            _ui.Post(_ => { if (!IsDisposed) RefreshDeviceList(); }, null);
        };
    }

    private void SaveAndReloadApps()
    {
        _store.Save();
        _runtime.ApplySettings();
        ReloadAppsListBox();
    }

    private void LoadFromSettings()
    {
        _nudFg.Value = Math.Min(_nudFg.Maximum, S.ForegroundProfile);
        _nudBg.Value = Math.Min(_nudBg.Maximum, S.BackgroundProfile);
        _nudSwitchDelay.Value = Math.Clamp(S.SwitchDelayMs, (int)_nudSwitchDelay.Minimum, (int)_nudSwitchDelay.Maximum);
        _nudGateTimeout.Value = Math.Clamp(S.GateTimeoutMs, (int)_nudGateTimeout.Minimum, (int)_nudGateTimeout.Maximum);
        _cbPause.Checked = S.Paused;

        if (OperatingSystem.IsWindows())
        {
            _cbDeviceSuppressEvent = true;
            bool actual = StartupRegistrar.IsEnabled();
            _cbStartup.Checked = actual;
            if (actual != S.StartWithWindows) { S.StartWithWindows = actual; _store.Save(); }
            _cbDeviceSuppressEvent = false;
        }
        else
        {
            _cbStartup.Enabled = false;
        }

        ReloadAppsListBox();
        RefreshDeviceList();
    }

    private void RefreshDeviceList()
    {
        var devices = RuntimeController.EnumerateDevices();

        _cbDeviceSuppressEvent = true;
        _cbDevice.BeginUpdate();
        _cbDevice.Items.Clear();
        _deviceChoices.Clear();

        if (devices.Count == 0)
        {
            _deviceChoices.Add(new DeviceChoice(null, null, "(no keyboard detected)"));
        }
        else
        {
            foreach (var d in devices)
            {
                string name = SafeGet(() => d.GetProductName()) ?? "(unknown)";
                _deviceChoices.Add(new DeviceChoice(
                    d.VendorID, d.ProductID,
                    $"{name}  (vid=0x{d.VendorID:X4} pid=0x{d.ProductID:X4})"));
            }
        }
        foreach (var c in _deviceChoices) _cbDevice.Items.Add(c);

        int select = 0;
        if (S.VendorId is int v && S.ProductId is int p)
        {
            for (int i = 0; i < _deviceChoices.Count; i++)
            {
                var c = _deviceChoices[i];
                if (c.VendorId == v && c.ProductId == p) { select = i; break; }
            }
        }
        _cbDevice.SelectedIndex = _deviceChoices.Count == 0 ? -1 : select;
        _cbDevice.EndUpdate();
        _cbDeviceSuppressEvent = false;
    }

    private static T? SafeGet<T>(Func<T> fn) where T : class
    {
        try { return fn(); } catch { return null; }
    }

    private void ReloadAppsListBox()
    {
        _listApps.BeginUpdate();
        _listApps.Items.Clear();
        foreach (var a in S.WatchedApps) _listApps.Items.Add(a);
        _listApps.EndUpdate();
    }

    private void AppendLog(string line)
    {
        _statusFooter.Text = line;
    }

    private void UpdateProfilePreview()
    {
        var list = _runtime.Profiles;
        _fgPreview.Text = ProfileNameFor(list, (byte)_nudFg.Value);
        _bgPreview.Text = ProfileNameFor(list, (byte)_nudBg.Value);
    }

    private static string ProfileNameFor(IReadOnlyList<ProfileInfo>? list, byte idx)
    {
        if (list == null) return "";
        var p = list.FirstOrDefault(x => x.Index == idx);
        if (p == null) return "(unknown profile)";
        return string.IsNullOrWhiteSpace(p.Name) ? $"(unnamed, {p.Color})" : $"{p.Name} · {p.Color}";
    }

    private void RefreshUiFromRuntime()
    {
        switch (_runtime.State)
        {
            case RuntimeState.Connected:
                _statusDot.ForeColor = Color.ForestGreen;
                break;
            case RuntimeState.Paused:
                _statusDot.ForeColor = Color.Goldenrod;
                break;
            case RuntimeState.Connecting:
                _statusDot.ForeColor = Color.DeepSkyBlue;
                break;
            case RuntimeState.Error:
                _statusDot.ForeColor = Color.IndianRed;
                break;
            default:
                _statusDot.ForeColor = Color.Gray;
                break;
        }

        _kbInfo.Text = _runtime.ConnectedProduct != null
            ? $"vid=0x{_runtime.ConnectedVid:X4}  pid=0x{_runtime.ConnectedPid:X4}"
            : _runtime.StateDetail ?? "";

        string profileLabel = _runtime.CurrentProfileIdx is byte idx
            ? $"Active: profile {idx}  {ProfileNameFor(_runtime.Profiles, idx)}"
            : "";
        _kbProfile.Text = profileLabel;

        // Clamp NumericUpDowns to actual profile count if known.
        if (_runtime.ProfileCount is byte count && count > 0)
        {
            _nudFg.Maximum = count - 1;
            _nudBg.Maximum = count - 1;
        }
        UpdateProfilePreview();
    }

    // ---------------- close → minimise to tray ----------------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void ShowAndActivate()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }
}

/// <summary>Modal picker for a running executable, used by "Add running process…".</summary>
internal sealed class PickRunningDialog : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended };

    public IReadOnlyList<string> SelectedExecutables { get; private set; } = Array.Empty<string>();

    public PickRunningDialog()
    {
        Text = "Pick a running process";
        Width = 420; Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 80 };
        AcceptButton = ok; CancelButton = cancel;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        footer.Controls.Add(cancel);
        footer.Controls.Add(ok);

        Controls.Add(_list);
        Controls.Add(footer);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses().OrderBy(x => x.ProcessName))
        {
            try
            {
                var name = p.ProcessName + ".exe";
                if (seen.Add(name)) _list.Items.Add(name);
            }
            catch { }
            finally { p.Dispose(); }
        }

        ok.Click += (_, _) =>
        {
            SelectedExecutables = _list.SelectedItems.Cast<string>().ToArray();
        };
    }
}
