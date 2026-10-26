using System.Drawing.Drawing2D;

namespace BoothFreeCollector;

// Keep standard CheckBox keyboard, click and accessibility behavior while
// painting a legible glyph independent of Windows' light checkbox theme.
internal sealed class CollectorCheckBox : CheckBox
{
    bool hovering;
    public CollectorCheckBox()
    {
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(text.Width + Scale(30) + Padding.Horizontal,
            Math.Max(text.Height, Scale(24)) + Padding.Vertical);
    }
    protected override void OnCheckStateChanged(EventArgs e) { base.OnCheckStateChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int size = Scale(18), x = Padding.Left + Scale(2), y = (Height - size) / 2;
        var box = new Rectangle(x, y, size, size);
        bool selected = CheckState != CheckState.Unchecked;
        using var fill = new SolidBrush(selected && Enabled ? Color.FromArgb(23, 108, 158) : CollectorTheme.Background);
        using var border = new Pen(!Enabled ? CollectorTheme.Border : selected || hovering ? CollectorTheme.Accent : Color.FromArgb(144, 164, 183), Scale(2));
        e.Graphics.FillRectangle(fill, box);
        e.Graphics.DrawRectangle(border, box);
        if (selected)
        {
            using var mark = new Pen(Enabled ? CollectorTheme.Text : CollectorTheme.Muted, Scale(2))
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (CheckState == CheckState.Indeterminate)
                e.Graphics.DrawLine(mark, x + Scale(4), y + size / 2, x + size - Scale(4), y + size / 2);
            else
                e.Graphics.DrawLines(mark, [new Point(x + Scale(4), y + Scale(9)),
                    new Point(x + Scale(8), y + Scale(13)), new Point(x + Scale(14), y + Scale(5))]);
        }
        var label = new Rectangle(x + size + Scale(9), 0, Math.Max(0, Width - x - size - Scale(9)), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, label, Enabled ? ForeColor : CollectorTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(label, -1, -Scale(3)), CollectorTheme.Accent, BackColor);
    }
}
