using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NeoSwitch.App;

// =========================================================================
// StatusDot — small coloured circle with a soft glow, used in the header.
// =========================================================================
public sealed class StatusDot : Control
{
    private Color _dotColor = Theme.Idle;

    public Color DotColor
    {
        get => _dotColor;
        set { if (_dotColor == value) return; _dotColor = value; Invalidate(); }
    }

    public StatusDot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(18, 18);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var cx = Width / 2;
        var cy = Height / 2;
        const int dotR = 4;
        const int glowR = 8;

        // Soft halo (ignored for grey/idle).
        if (_dotColor != Theme.Idle)
        {
            using var halo = new SolidBrush(Color.FromArgb(40, _dotColor));
            g.FillEllipse(halo, cx - glowR, cy - glowR, glowR * 2, glowR * 2);
        }
        using var core = new SolidBrush(_dotColor);
        g.FillEllipse(core, cx - dotR, cy - dotR, dotR * 2, dotR * 2);
    }
}

// =========================================================================
// ChipLabel — rounded accent pill, e.g. "Active: profile 1".
// =========================================================================
public sealed class ChipLabel : Control
{
    public Color ChipBackColor  { get; set; } = Theme.AccentTrack;
    public Color ChipBorderColor { get; set; } = Theme.AccentBorder;
    public Color ChipForeColor  { get; set; } = Theme.Accent;

    public ChipLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.Chip;
        Padding = new Padding(10, 3, 10, 3);
        AutoSize = true;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); AdjustSize(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Invalidate(); AdjustSize(); }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (string.IsNullOrEmpty(Text)) return new Size(Padding.Horizontal + 24, Padding.Vertical + 14);
        var sz = TextRenderer.MeasureText(Text, Font);
        return new Size(sz.Width + Padding.Horizontal, sz.Height + Padding.Vertical);
    }

    private void AdjustSize()
    {
        if (!AutoSize) return;
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = Height - 1;
        using var path = RoundedRect(rect, radius);
        using (var b = new SolidBrush(ChipBackColor)) g.FillPath(b, path);
        using (var p = new Pen(ChipBorderColor))     g.DrawPath(p, path);

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ChipForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        radius = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X,              r.Y,              d, d, 180, 90);
        path.AddArc(r.Right - d,       r.Y,              d, d, 270, 90);
        path.AddArc(r.Right - d,       r.Bottom - d,     d, d, 0,   90);
        path.AddArc(r.X,              r.Bottom - d,     d, d, 90,  90);
        path.CloseFigure();
        return path;
    }
}

// =========================================================================
// ToggleSwitch — iOS/Win11-style on/off pill. No animation, just state.
// =========================================================================
public sealed class ToggleSwitch : Control
{
    private bool _checked;
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(36, 20);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !_checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { Checked = !_checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var trackRect = new Rectangle(0, 1, 32, Height - 2);
        var trackColor = _checked ? Theme.Accent : Color.FromArgb(0x44, 0x44, 0x44);
        using (var path = ChipLabel.RoundedRect(trackRect, trackRect.Height / 2))
        using (var b    = new SolidBrush(trackColor))
        {
            g.FillPath(b, path);
        }

        int thumbD = trackRect.Height - 4;
        int thumbX = _checked ? trackRect.Right - thumbD - 2 : trackRect.X + 2;
        int thumbY = trackRect.Y + 2;
        var thumbColor = _checked ? Color.FromArgb(0x0A, 0x15, 0x20) : Color.FromArgb(0xCC, 0xCC, 0xCC);
        using (var b = new SolidBrush(thumbColor))
        {
            g.FillEllipse(b, thumbX, thumbY, thumbD, thumbD);
        }
    }
}

// =========================================================================
// ButtonStyler — apply a consistent flat dark look to any Button.
// =========================================================================
public static class ButtonStyler
{
    public static void Flat(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.Font = Theme.Body;
        if (primary)
        {
            b.BackColor = Theme.Accent;
            b.ForeColor = Color.FromArgb(0x0A, 0x15, 0x20);
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.AccentDark;
            b.FlatAppearance.MouseDownBackColor = Theme.AccentDark;
        }
        else
        {
            b.BackColor = Theme.PanelAlt;
            b.ForeColor = Theme.Text;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x3C, 0x3C, 0x3C);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0x3C, 0x3C, 0x3C);
        }
        b.AutoSize = true;
    }
}
