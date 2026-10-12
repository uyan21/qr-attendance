using System.Drawing.Drawing2D;

namespace QrAttendance;

static class Theme
{
    public static readonly Color Side = Color.FromArgb(30, 41, 59);
    public static readonly Color SideActive = Color.FromArgb(51, 65, 85);
    public static readonly Color SideText = Color.FromArgb(203, 213, 225);
    public static readonly Color Bg = Color.FromArgb(241, 245, 249);
    public static readonly Color Text = Color.FromArgb(30, 41, 59);
    public static readonly Color Muted = Color.FromArgb(100, 116, 139);
    public static readonly Color Line = Color.FromArgb(226, 232, 240);
    public static readonly Color Accent = Color.FromArgb(79, 70, 229);
    public static readonly Color AccentDark = Color.FromArgb(67, 56, 202);
    public static readonly Color AccentSoft = Color.FromArgb(224, 231, 255);
    public static readonly Color Ok = Color.FromArgb(22, 163, 74);
    public static readonly Color Warn = Color.FromArgb(234, 88, 12);
    public static readonly Color Err = Color.FromArgb(220, 38, 38);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

class RoundButton : Button
{
    public Color Fill = Theme.Accent, Hover = Theme.AccentDark, TextColor = Color.White;
    public Color? Border;
    bool over;

    public RoundButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        AutoSize = true;
    }

    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, Math.Min(Height / 4f, 10));
        using (var b = new SolidBrush(over && Enabled ? Hover : Fill)) g.FillPath(b, path);
        if (Border is Color bc)
        {
            using var pen = new Pen(bc);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, TextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

class RoundedLabel : Label
{
    public int Radius = 14;

    public RoundedLabel() => DoubleBuffered = true;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Radius);
        using var b = new SolidBrush(BackColor);
        g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
}
