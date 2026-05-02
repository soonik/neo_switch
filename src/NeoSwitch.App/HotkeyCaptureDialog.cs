using System.Drawing;
using System.Windows.Forms;

namespace NeoSwitch.App;

/// <summary>
/// Modal that asks the user to press a key chord and remembers it. Won't
/// accept a bare modifier (Ctrl alone, Alt alone, etc.) — every chord must
/// have at least one non-modifier key, otherwise <c>RegisterHotKey</c>
/// would fail or fire on every modifier press.
/// </summary>
public sealed class HotkeyCaptureDialog : Form
{
    private readonly Label _display;
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly Button _clear;

    public HotkeyMods CapturedMods { get; private set; }
    public uint CapturedVk { get; private set; }

    public HotkeyCaptureDialog(HotkeyMods mods, uint vk)
    {
        CapturedMods = mods;
        CapturedVk = vk;

        Text = "Set panic hotkey";
        ClientSize = new Size(400, 170);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        KeyPreview = true;

        var hint = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(16, 12, 16, 0),
            Text = "Press the key combination you want as the panic hotkey.\n" +
                   "Must include at least one non-modifier key (e.g. Ctrl+Alt+Shift+R).",
            ForeColor = Theme.Muted,
        };

        _display = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 14f),
            Text = FormatChord(mods, vk),
            ForeColor = Theme.Accent,
            BackColor = Theme.Panel,
            Margin = new Padding(0),
        };

        _ok     = new Button { Text = "OK",     DialogResult = DialogResult.OK,     AutoSize = true };
        _cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        _clear  = new Button { Text = "Clear",  AutoSize = true };
        ButtonStyler.Flat(_ok, primary: true);
        ButtonStyler.Flat(_cancel);
        ButtonStyler.Flat(_clear);
        AcceptButton = _ok;
        CancelButton = _cancel;

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(12, 8, 12, 8),
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.Panel,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _clear.Anchor  = AnchorStyles.None;
        _cancel.Anchor = AnchorStyles.None;
        _ok.Anchor     = AnchorStyles.None;
        _cancel.Margin = new Padding(0, 0, 8, 0);
        _ok.Margin     = new Padding(0);
        footer.Controls.Add(_clear,  0, 0);
        footer.Controls.Add(_cancel, 2, 0);
        footer.Controls.Add(_ok,     3, 0);

        Controls.Add(_display);
        Controls.Add(footer);
        Controls.Add(hint);

        _clear.Click += (_, _) =>
        {
            CapturedMods = HotkeyMods.None;
            CapturedVk = 0;
            _display.Text = FormatChord(CapturedMods, CapturedVk);
        };

        KeyDown += (_, e) =>
        {
            // A bare modifier press is not a valid hotkey — wait for an extra key.
            if (e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
                          or Keys.ShiftKey  or Keys.LShiftKey  or Keys.RShiftKey
                          or Keys.Menu      or Keys.LMenu      or Keys.RMenu
                          or Keys.LWin      or Keys.RWin)
            {
                return;
            }

            // Don't bind to bare Esc/Tab — let the dialog use them normally.
            if (e.KeyCode is Keys.Escape or Keys.Tab) return;

            HotkeyMods m = HotkeyMods.None;
            if (e.Control) m |= HotkeyMods.Control;
            if (e.Alt)     m |= HotkeyMods.Alt;
            if (e.Shift)   m |= HotkeyMods.Shift;

            CapturedMods = m;
            CapturedVk = (uint)e.KeyCode;
            _display.Text = FormatChord(CapturedMods, CapturedVk);

            e.SuppressKeyPress = true;
            e.Handled = true;
        };
    }

    public static string FormatChord(HotkeyMods mods, uint vk)
    {
        if (vk == 0 && mods == HotkeyMods.None) return "(none)";
        var parts = new List<string>(4);
        if ((mods & HotkeyMods.Control) != 0) parts.Add("Ctrl");
        if ((mods & HotkeyMods.Alt)     != 0) parts.Add("Alt");
        if ((mods & HotkeyMods.Shift)   != 0) parts.Add("Shift");
        if ((mods & HotkeyMods.Win)     != 0) parts.Add("Win");
        if (vk != 0) parts.Add(VkToFriendly(vk));
        return string.Join(" + ", parts);
    }

    private static string VkToFriendly(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
        0x08 => "Back",  0x09 => "Tab",  0x0D => "Enter",  0x14 => "Caps",
        0x1B => "Esc",   0x20 => "Space",
        0x21 => "PgUp",  0x22 => "PgDn", 0x23 => "End",   0x24 => "Home",
        0x25 => "Left",  0x26 => "Up",   0x27 => "Right", 0x28 => "Down",
        0x2D => "Ins",   0x2E => "Del",
        _    => $"VK{vk:X2}",
    };
}
