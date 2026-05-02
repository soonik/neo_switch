using System.Diagnostics;
using System.Drawing.Drawing2D;
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
    private readonly StatusDot _statusDot = new() { Margin = new Padding(0, 6, 8, 0) };
    private readonly ComboBox _cbDevice = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        Width = 300,
        BackColor = Theme.InputBg,
        ForeColor = Theme.Text,
    };
    private readonly Label _kbInfo = new()
    {
        AutoSize = true, ForeColor = Theme.Muted, Text = "",
        Font = Theme.Body,
    };
    private readonly ChipLabel _profileChip = new() { Text = "", Visible = false };
    private readonly Button _btnReconnect = new() { Text = "Reconnect", AutoSize = true };

    // Suppress flags — prevent setter-driven events from re-entering handlers.
    private bool _cbDeviceSuppressEvent;
    private bool _startupSuppressEvent;
    private readonly List<DeviceChoice> _deviceChoices = new();

    private sealed record DeviceChoice(int? VendorId, int? ProductId, string Display)
    {
        public override string ToString() => Display;
    }

    // Watched apps
    private readonly ListBox _listApps = new()
    {
        IntegralHeight = false,
        SelectionMode = SelectionMode.MultiExtended,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Theme.InputBg,
        ForeColor = Theme.Text,
        Font = Theme.Body,
    };
    private readonly Button _btnAddFile = new() { Text = "＋ Add from file…" };
    private readonly Button _btnAddRunning = new() { Text = "＋ Add running process…" };
    private readonly Button _btnRemove = new() { Text = "Remove" };

    // Profile mapping
    private readonly NumericUpDown _nudFg = new() { Minimum = 0, Maximum = 15, Width = 70 };
    private readonly NumericUpDown _nudBg = new() { Minimum = 0, Maximum = 15, Width = 70 };
    private readonly Label _fgPreview = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Body };
    private readonly Label _bgPreview = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Body };
    private readonly ToggleSwitch _tsPause = new();
    private readonly ToggleSwitch _tsStartup = new();
    private readonly ToggleSwitch _tsGateAnyKey = new();
    private readonly ToggleSwitch _tsAutoRelease = new();
    private readonly NumericUpDown _nudAutoReleaseDelay = new() { Minimum = 0, Maximum = 2000, Increment = 25, Width = 90 };
    private readonly ToggleSwitch _tsPanicHotkey = new();
    private readonly Label _lblPanicHotkey = new() { AutoSize = true };
    private readonly Button _btnChangePanicHotkey = new() { Text = "Change…" };

    /// <summary>Raised when the panic-hotkey settings change so the host can re-register.</summary>
    public event Action? PanicHotkeyChanged;
    private readonly NumericUpDown _nudSwitchDelay = new() { Minimum = 0, Maximum = 5000, Increment = 50, Width = 90 };
    private readonly NumericUpDown _nudGateTimeout = new() { Minimum = 0, Maximum = 10000, Increment = 100, Width = 90 };

    // Footer
    private readonly Label _statusFooter = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.Muted,
        Font = Theme.Code,
        Text = "(no events yet)",
        Padding = new Padding(12, 0, 12, 0),
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

        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;

        // Style every themable control at construction time.
        ButtonStyler.Flat(_btnReconnect);
        ButtonStyler.Flat(_btnAddFile, primary: true);
        ButtonStyler.Flat(_btnAddRunning);
        ButtonStyler.Flat(_btnRemove);
        ButtonStyler.Flat(_btnChangePanicHotkey);
        StyleNumericUpDown(_nudFg);
        StyleNumericUpDown(_nudBg);
        StyleNumericUpDown(_nudSwitchDelay);
        StyleNumericUpDown(_nudGateTimeout);
        StyleNumericUpDown(_nudAutoReleaseDelay);

        BuildLayout();
        WireEvents();
        LoadFromSettings();
        RefreshUiFromRuntime();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (OperatingSystem.IsWindows())
        {
            DwmInterop.UseDarkTitleBar(Handle);
            DwmInterop.UseRoundedCorners(Handle);
        }
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
            BackColor = Theme.Background,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68f));
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
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = Theme.Panel,
        };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var namePanel = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 2, AutoSize = true,
            Margin = new Padding(0, 0, 12, 0),
            BackColor = Color.Transparent,
        };
        _cbDevice.Margin = new Padding(0, 0, 0, 2);
        namePanel.Controls.Add(_cbDevice, 0, 0);
        namePanel.Controls.Add(_kbInfo,   0, 1);

        // AnchorStyles with neither Top nor Bottom lets TableLayoutPanel centre
        // the control vertically in its (fixed-height) cell. That keeps the
        // status dot, device combo, profile chip, and Reconnect button on the
        // same horizontal sight-line.
        _statusDot.Anchor    = AnchorStyles.None;
        namePanel.Anchor     = AnchorStyles.Left;
        _profileChip.Anchor  = AnchorStyles.Right;
        _btnReconnect.Anchor = AnchorStyles.Right;
        _profileChip.Margin  = new Padding(0, 0, 12, 0);
        _btnReconnect.Margin = new Padding(0);

        p.Controls.Add(_statusDot,    0, 0);
        p.Controls.Add(namePanel,     1, 0);
        p.Controls.Add(_profileChip,  2, 0);
        p.Controls.Add(_btnReconnect, 3, 0);

        // Subtle bottom border between header and body.
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, p.Height - 1, p.Width, p.Height - 1);
        };

        return p;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            BackColor = Theme.Background,
        };
        split.Panel1.BackColor = Theme.Background;
        split.Panel2.BackColor = Theme.Background;
        split.SplitterWidth = 1;

        this.Shown += (_, _) =>
        {
            try
            {
                int w = split.Width;
                int sw = split.SplitterWidth;
                const int p1Min = 280;
                const int p2Min = 360;
                if (w < p1Min + p2Min + sw) return;
                split.Panel1MinSize = p1Min;
                split.Panel2MinSize = p2Min;
                int target = Math.Clamp(w - 420, p1Min, w - p2Min - sw);
                split.SplitterDistance = target;
            }
            catch { }
        };

        // ---- left: watched apps ----
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
            BackColor = Theme.Background,
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lblApps = new Label
        {
            Text = "WATCHED APPLICATIONS",
            AutoSize = true,
            Font = Theme.SectionLbl,
            ForeColor = Theme.Muted,
            Margin = new Padding(0, 0, 0, 8),
        };
        left.Controls.Add(lblApps, 0, 0);
        _listApps.Dock = DockStyle.Fill;
        left.Controls.Add(_listApps, 0, 1);

        // TableLayoutPanel (not FlowLayout) so all three buttons share the
        // same baseline even when their text widths differ — Anchor=None then
        // centres each button vertically within the row height.
        var btnRow = new TableLayoutPanel
        {
            ColumnCount = 4, RowCount = 1, AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
            BackColor = Color.Transparent,
        };
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _btnAddFile.Anchor    = AnchorStyles.None;
        _btnAddRunning.Anchor = AnchorStyles.None;
        _btnRemove.Anchor     = AnchorStyles.None;
        _btnAddFile.Margin    = new Padding(0, 0, 8, 0);
        _btnAddRunning.Margin = new Padding(0, 0, 8, 0);
        _btnRemove.Margin     = new Padding(0);
        btnRow.Controls.Add(_btnAddFile,    0, 0);
        btnRow.Controls.Add(_btnAddRunning, 1, 0);
        btnRow.Controls.Add(_btnRemove,     2, 0);
        left.Controls.Add(btnRow, 0, 2);
        split.Panel1.Controls.Add(left);

        // ---- right: profile mapping ----
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = false,
            AutoScroll = true,
            Padding = new Padding(16),
            BackColor = Theme.Background,
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        right.Controls.Add(new Label
        {
            Text = "PROFILE MAPPING",
            AutoSize = true,
            Font = Theme.SectionLbl,
            ForeColor = Theme.Muted,
            Margin = new Padding(0, 0, 0, 12),
        });
        right.Controls.Add(MakeFieldRow("Foreground profile", _nudFg, _fgPreview, "when a watched app is focused"));
        right.Controls.Add(MakeFieldRow("Background profile", _nudBg, _bgPreview, "when nothing watched is focused"));
        right.Controls.Add(MakeFieldRow("Switch delay (ms)",  _nudSwitchDelay, null, "debounce before the HID write"));
        right.Controls.Add(MakeFieldRow("Gate timeout (ms)",  _nudGateTimeout, null, "max wait for keys to release before writing anyway"));
        right.Controls.Add(MakeToggleRow(_tsGateAnyKey, "Wait for all keys to release",
            "on: wait for any key (letters, arrows, F-keys) before switching — safest\n" +
            "off: only wait for modifier keys (Alt/Ctrl/Shift/Win) — lower latency"));
        right.Controls.Add(MakeToggleRow(_tsAutoRelease, "Auto-release stuck keys after switch",
            "force a key-up for any key still held immediately after the switch.\n" +
            "Recovers from firmware-dropped key-ups; brief flicker if you're really still holding the key."));
        right.Controls.Add(MakeFieldRow("Auto-release delay (ms)", _nudAutoReleaseDelay, null,
            "how long to wait after the HID write before sweeping for stuck keys"));
        right.Controls.Add(MakeToggleRow(_tsPanicHotkey, "Panic hotkey",
            "global chord that releases all stuck keys, regardless of focus"));
        right.Controls.Add(MakePanicHotkeyRow());
        right.Controls.Add(MakeToggleRow(_tsPause,   "Pause switching", "observe foreground changes but send no HID"));
        right.Controls.Add(MakeToggleRow(_tsStartup, "Start with Windows", "auto-launch NeoSwitch on logon"));
        split.Panel2.Controls.Add(right);

        return split;
    }

    private Control MakePanicHotkeyRow()
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 14),
            BackColor = Color.Transparent,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var lbl = new Label
        {
            Text = "Hotkey chord", AutoSize = true,
            Margin = new Padding(0, 6, 6, 0),
            ForeColor = Theme.Text, Font = Theme.Body,
        };
        row.Controls.Add(lbl, 0, 0);

        _lblPanicHotkey.ForeColor = Theme.Accent;
        _lblPanicHotkey.Font      = new Font("Segoe UI Semibold", 10f);
        _lblPanicHotkey.Margin    = new Padding(0, 5, 12, 0);
        row.Controls.Add(_lblPanicHotkey, 1, 0);

        _btnChangePanicHotkey.Anchor = AnchorStyles.None;
        row.Controls.Add(_btnChangePanicHotkey, 2, 0);

        var hintLbl = new Label
        {
            Text = "Click Change… and press the chord you want.",
            AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Body,
            Margin = new Padding(0, 2, 0, 0),
        };
        row.SetColumnSpan(hintLbl, 3);
        row.RowCount = 2;
        row.Controls.Add(hintLbl, 0, 1);
        return row;
    }

    private Control MakeFieldRow(string label, Control input, Control? preview, string hint)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 14),
            BackColor = Color.Transparent,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var lbl = new Label
        {
            Text = label, AutoSize = true,
            Margin = new Padding(0, 6, 6, 0),
            ForeColor = Theme.Text, Font = Theme.Body,
        };
        row.Controls.Add(lbl, 0, 0);
        input.Margin = new Padding(0, 0, 10, 0);
        row.Controls.Add(input, 1, 0);
        if (preview != null)
        {
            preview.Margin = new Padding(0, 6, 0, 0);
            row.Controls.Add(preview, 2, 0);
        }
        var hintLbl = new Label
        {
            Text = hint, AutoSize = true,
            ForeColor = Theme.Muted, Font = Theme.Body,
            Margin = new Padding(0, 2, 0, 0),
        };
        row.SetColumnSpan(hintLbl, 3);
        row.RowCount = 2;
        row.Controls.Add(hintLbl, 0, 1);
        return row;
    }

    private Control MakeToggleRow(ToggleSwitch toggle, string label, string hint)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 10),
            BackColor = Color.Transparent,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        toggle.Margin = new Padding(0, 2, 10, 0);
        row.Controls.Add(toggle, 0, 0);

        var textPanel = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 2, AutoSize = true,
            BackColor = Color.Transparent,
        };
        textPanel.Controls.Add(new Label
        {
            Text = label, AutoSize = true,
            ForeColor = Theme.Text, Font = Theme.Body,
            Margin = new Padding(0),
        }, 0, 0);
        textPanel.Controls.Add(new Label
        {
            Text = hint, AutoSize = true,
            ForeColor = Theme.Muted, Font = Theme.Body,
            Margin = new Padding(0, 1, 0, 0),
        }, 0, 1);
        row.Controls.Add(textPanel, 1, 0);
        return row;
    }

    private Control BuildFooter()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, p.Width, 0);
        };
        p.Controls.Add(_statusFooter);
        return p;
    }

    private static void StyleNumericUpDown(NumericUpDown n)
    {
        n.BackColor = Theme.InputBg;
        n.ForeColor = Theme.Text;
        n.BorderStyle = BorderStyle.FixedSingle;
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

        _tsPause.CheckedChanged += (_, _) =>
        {
            if (S.Paused == _tsPause.Checked) return;
            _runtime.TogglePause();
            _store.Save();
        };

        _tsGateAnyKey.CheckedChanged += (_, _) =>
        {
            if (S.GateOnAnyKey == _tsGateAnyKey.Checked) return;
            S.GateOnAnyKey = _tsGateAnyKey.Checked;
            _store.Save();
            // No need to restart the runtime — the gate predicate reads the
            // setting directly on each poll.
        };

        _tsAutoRelease.CheckedChanged += (_, _) =>
        {
            if (S.AutoReleaseAfterSwitch == _tsAutoRelease.Checked) return;
            S.AutoReleaseAfterSwitch = _tsAutoRelease.Checked;
            _store.Save();
        };

        _nudAutoReleaseDelay.ValueChanged += (_, _) =>
        {
            S.AutoReleaseDelayMs = (int)_nudAutoReleaseDelay.Value;
            _store.Save();
        };

        _tsPanicHotkey.CheckedChanged += (_, _) =>
        {
            if (S.PanicHotkeyEnabled == _tsPanicHotkey.Checked) return;
            S.PanicHotkeyEnabled = _tsPanicHotkey.Checked;
            _store.Save();
            PanicHotkeyChanged?.Invoke();
        };

        _btnChangePanicHotkey.Click += (_, _) =>
        {
            using var dlg = new HotkeyCaptureDialog((HotkeyMods)S.PanicHotkeyModifiers, S.PanicHotkeyVk);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            // Reject empty bindings — would be impossible to fire.
            if (dlg.CapturedVk == 0)
            {
                MessageBox.Show(this,
                    "Hotkey is empty. Choose a key combination first.",
                    "NeoSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            S.PanicHotkeyModifiers = (uint)dlg.CapturedMods;
            S.PanicHotkeyVk        = dlg.CapturedVk;
            _store.Save();
            _lblPanicHotkey.Text = HotkeyCaptureDialog.FormatChord(dlg.CapturedMods, dlg.CapturedVk);
            PanicHotkeyChanged?.Invoke();
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

        _tsStartup.CheckedChanged += (_, _) =>
        {
            if (!OperatingSystem.IsWindows()) return;
            if (_startupSuppressEvent) return;
            bool desired = _tsStartup.Checked;
            bool ok = StartupRegistrar.SetEnabled(desired);
            S.StartWithWindows = ok && desired;
            _store.Save();
            if (!ok)
            {
                _startupSuppressEvent = true;
                _tsStartup.Checked = StartupRegistrar.IsEnabled();
                _startupSuppressEvent = false;
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
        _nudAutoReleaseDelay.Value = Math.Clamp(S.AutoReleaseDelayMs, (int)_nudAutoReleaseDelay.Minimum, (int)_nudAutoReleaseDelay.Maximum);
        _tsPause.Checked = S.Paused;
        _tsGateAnyKey.Checked = S.GateOnAnyKey;
        _tsAutoRelease.Checked = S.AutoReleaseAfterSwitch;
        _tsPanicHotkey.Checked = S.PanicHotkeyEnabled;
        _lblPanicHotkey.Text   = HotkeyCaptureDialog.FormatChord((HotkeyMods)S.PanicHotkeyModifiers, S.PanicHotkeyVk);

        if (OperatingSystem.IsWindows())
        {
            _startupSuppressEvent = true;
            bool actual = StartupRegistrar.IsEnabled();
            _tsStartup.Checked = actual;
            if (actual != S.StartWithWindows) { S.StartWithWindows = actual; _store.Save(); }
            _startupSuppressEvent = false;
        }
        else
        {
            _tsStartup.Enabled = false;
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

    private void AppendLog(string line) => _statusFooter.Text = line;

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
        _statusDot.DotColor = _runtime.State switch
        {
            RuntimeState.Connected  => Theme.Good,
            RuntimeState.Paused     => Theme.Warn,
            RuntimeState.Connecting => Theme.Accent,
            RuntimeState.Error      => Theme.Error,
            _                       => Theme.Idle,
        };

        _kbInfo.Text = _runtime.ConnectedProduct != null
            ? $"vid=0x{_runtime.ConnectedVid:X4}  ·  pid=0x{_runtime.ConnectedPid:X4}"
            : _runtime.StateDetail ?? "";

        if (_runtime.CurrentProfileIdx is byte idx)
        {
            string name = ProfileNameFor(_runtime.Profiles, idx);
            _profileChip.Text = string.IsNullOrWhiteSpace(name)
                ? $"Active: profile {idx}"
                : $"Active: profile {idx} · {name.Split(' ')[0]}";
            _profileChip.Visible = true;
        }
        else
        {
            _profileChip.Visible = false;
        }

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
    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill,
        SelectionMode = SelectionMode.MultiExtended,
        BackColor = Theme.InputBg,
        ForeColor = Theme.Text,
        Font = Theme.Body,
        BorderStyle = BorderStyle.FixedSingle,
    };

    public IReadOnlyList<string> SelectedExecutables { get; private set; } = Array.Empty<string>();

    public PickRunningDialog()
    {
        Text = "Pick a running process";
        ClientSize = new Size(460, 520);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;

        var ok     = new Button { Text = "Add",    DialogResult = DialogResult.OK,     AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ButtonStyler.Flat(ok, primary: true);
        ButtonStyler.Flat(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        // TableLayoutPanel + Anchor=None centres the Add / Cancel buttons on
        // the same baseline; FlowLayoutPanel doesn't vertical-centre its
        // children and leaves subtle 1-2 px offsets between them.
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(12, 8, 12, 8),
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Theme.Panel,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        cancel.Anchor = AnchorStyles.None;
        ok.Anchor     = AnchorStyles.None;
        cancel.Margin = new Padding(0, 0, 8, 0);
        ok.Margin     = new Padding(0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(ok,     2, 0);

        var container = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            BackColor = Theme.Background,
        };
        container.Controls.Add(_list);

        Controls.Add(container);
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (OperatingSystem.IsWindows())
        {
            DwmInterop.UseDarkTitleBar(Handle);
            DwmInterop.UseRoundedCorners(Handle);
        }
    }
}
