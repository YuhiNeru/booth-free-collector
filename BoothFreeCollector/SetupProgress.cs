using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BoothFreeCollector;

internal enum SetupStage { Installation, Login, Connection, Complete }
internal static class SetupFlow
{
    internal static SetupStage Stage(bool installed, bool loggedIn, bool connectionChecking, bool completed)
        => !installed ? SetupStage.Installation : completed && loggedIn ? SetupStage.Complete
            : connectionChecking || loggedIn ? SetupStage.Connection : SetupStage.Login;
}

internal sealed class SetupProgress : Control
{
    SetupStage stage;
    internal int Revision;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal SetupStage Stage => stage;
    internal SetupProgress()
    {
        Height = 78; Width = 820;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleName = "セットアップの進行状況";
        TabStop = false;
    }
    internal void SetStage(SetupStage value)
    {
        if (stage == value && Revision != 0) return;
        stage = value; Revision++;
        AccessibleDescription = "導入確認、ログイン、接続確認、完了の順です。現在は" + new[] { "導入確認", "ログイン", "接続確認", "完了" }[(int)stage];
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f, radius = 12 * scale, y = 17 * scale;
        float cell = Width / 4f;
        string[] names = ["導入確認", "ログイン", "接続確認", "完了"];
        using var bold = new Font(Font, FontStyle.Bold);
        for (int i = 0; i < 4; i++)
        {
            float x = cell * (i + .5f);
            bool done = i < (int)stage || stage == SetupStage.Complete;
            bool current = i == (int)stage && !done;
            Color color = done ? CollectorTheme.Success : current ? CollectorTheme.Accent : CollectorTheme.Border;
            if (i < 3)
            {
                using var line = new Pen(i < (int)stage ? CollectorTheme.Success : CollectorTheme.Border, 2 * scale);
                e.Graphics.DrawLine(line, x + radius + 6 * scale, y, x + cell - radius - 6 * scale, y);
            }
            using var fill = new SolidBrush(color);
            e.Graphics.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
            if (current)
            {
                using var halo = new Pen(CollectorTheme.Accent, scale);
                e.Graphics.DrawEllipse(halo, x - radius - 4 * scale, y - radius - 4 * scale, (radius + 4 * scale) * 2, (radius + 4 * scale) * 2);
            }
            if (done)
            {
                using var tick = new Pen(CollectorTheme.Background, 2 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                e.Graphics.DrawLines(tick, [new PointF(x - 5 * scale, y), new PointF(x - scale, y + 4 * scale), new PointF(x + 6 * scale, y - 4 * scale)]);
            }
            else TextRenderer.DrawText(e.Graphics, (i + 1).ToString(), bold,
                Rectangle.Round(new RectangleF(x - radius, y - radius, radius * 2, radius * 2)), current ? CollectorTheme.Background : CollectorTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var name = Rectangle.Round(new RectangleF(cell * i, 36 * scale, cell, 24 * scale));
            TextRenderer.DrawText(e.Graphics, names[i], current ? bold : Font, name,
                done ? CollectorTheme.Success : current ? CollectorTheme.Text : CollectorTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var note = Rectangle.Round(new RectangleF(cell * i, 59 * scale, cell, 18 * scale));
            TextRenderer.DrawText(e.Graphics, done ? "確認済み" : current ? "いまここ" : "これから", Font, note,
                current ? CollectorTheme.Accent : CollectorTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
