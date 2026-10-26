using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BoothFreeCollector;

internal sealed class BufferedPanel : Panel
{
    public BufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}

internal sealed class CollectorGrid : DataGridView
{
    public CollectorGrid() { DoubleBuffered = true; }
}

internal sealed class CollectorButton : Button
{
    bool hovering, pressed;
    public CollectorButton() { DoubleBuffered = true; FlatStyle = FlatStyle.Flat; }
    protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovering = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? CollectorTheme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = !Enabled ? CollectorTheme.Surface : pressed ? FlatAppearance.MouseDownBackColor : hovering ? FlatAppearance.MouseOverBackColor : BackColor;
        var rect = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        int corner = Math.Min(12, Math.Min(rect.Width, rect.Height));
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, corner, corner, 180, 90); path.AddArc(rect.Right - corner, rect.Top, corner, corner, 270, 90);
        path.AddArc(rect.Right - corner, rect.Bottom - corner, corner, corner, 0, 90); path.AddArc(rect.Left, rect.Bottom - corner, corner, corner, 90, 90); path.CloseFigure();
        using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
        if (Focused && ShowFocusCues) { using var pen = new Pen(CollectorTheme.Accent); e.Graphics.DrawPath(pen, path); }
        var textRect = new Rectangle(Padding.Left, 0, Math.Max(0, Width - Padding.Horizontal), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, textRect, Enabled ? ForeColor : CollectorTheme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
    }
}

// Keep every page alive. Switching pages only changes visibility; no native
// tab frames, handle recreation, database work or browser initialization.
internal sealed class CollectorNavigation : UserControl
{
    readonly Panel rail = new BufferedPanel { Dock = DockStyle.Left, Width = 174, Padding = new Padding(12, 24, 12, 12) };
    readonly FlowLayoutPanel links = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly Panel content = new BufferedPanel { Dock = DockStyle.Fill };
    readonly List<Button> buttons = [];
    int selectedIndex = -1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Collection<Control> TabPages { get; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (value == selectedIndex || value < 0 || value >= TabPages.Count) return;
            content.SuspendLayout();
            if (selectedIndex >= 0) TabPages[selectedIndex].Visible = false;
            selectedIndex = value;
            TabPages[value].Visible = true; TabPages[value].BringToFront();
            content.ResumeLayout(true);
            ApplyPalette();
        }
    }
    internal CollectorNavigation()
    {
        DoubleBuffered = true;
        TabPages = new Pages(this);
        rail.Controls.Add(links);
        Controls.Add(content); Controls.Add(rail);
    }
    void AddPage(Control page)
    {
        page.Dock = DockStyle.Fill; page.Visible = false;
        content.Controls.Add(page);
        int index = buttons.Count;
        var button = new CollectorButton { Text = page.Text, AccessibleName = page.Text, Width = 150, Height = 44,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), Margin = new Padding(0, 0, 0, 8), FlatStyle = FlatStyle.Flat };
        button.Click += (_, _) => SelectedIndex = index;
        buttons.Add(button); links.Controls.Add(button);
        if (selectedIndex < 0) SelectedIndex = 0;
    }
    internal void ApplyPalette()
    {
        rail.BackColor = Color.FromArgb(23, 23, 26); links.BackColor = rail.BackColor;
        for (int i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            button.UseVisualStyleBackColor = false; button.FlatAppearance.BorderSize = 0;
            button.BackColor = i == selectedIndex ? Color.FromArgb(60, 35, 45) : rail.BackColor;
            button.ForeColor = i == selectedIndex ? CollectorTheme.Text : CollectorTheme.Muted;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(39, 39, 45);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 35, 45);
        }
    }
    sealed class Pages(CollectorNavigation owner) : Collection<Control>
    {
        protected override void InsertItem(int index, Control item)
        { base.InsertItem(index, item); owner.AddPage(item); }
    }
}
