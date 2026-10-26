using System.Runtime.InteropServices;

namespace BoothFreeCollector;

internal static class CollectorTheme
{
    internal static readonly Color Background = Color.FromArgb(21, 32, 43);
    internal static readonly Color Surface = Color.FromArgb(25, 39, 52);
    internal static readonly Color Border = Color.FromArgb(56, 68, 77);
    internal static readonly Color Text = Color.FromArgb(245, 248, 250);
    internal static readonly Color Muted = Color.FromArgb(168, 184, 197);
    internal static readonly Color Accent = Color.FromArgb(29, 161, 242);
    internal static readonly Color Warning = Color.FromArgb(255, 141, 149);
    internal static readonly Color Success = Color.FromArgb(111, 205, 160);

    internal static void Apply(Control control)
    {
        control.BackColor = Background;
        control.ForeColor = control.ForeColor == Color.Firebrick ? Warning
            : control.ForeColor == Color.DarkGreen ? Success : Text;
        switch (control)
        {
            case Button button:
                button.UseVisualStyleBackColor = false;
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Surface;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.BorderSize = 0;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(34, 52, 68);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(32, 67, 91);
                button.Cursor = Cursors.Hand;
                break;
            case TextBox text:
                text.BackColor = Surface;
                text.BorderStyle = BorderStyle.FixedSingle;
                break;
            case CheckBox check:
                check.FlatStyle = FlatStyle.Flat;
                check.FlatAppearance.BorderColor = Muted;
                check.Cursor = Cursors.Hand;
                break;
            case LinkLabel link:
                link.LinkColor = Accent;
                link.ActiveLinkColor = Text;
                link.VisitedLinkColor = Accent;
                break;
            case ListBox list:
                list.BackColor = Surface;
                list.BorderStyle = BorderStyle.None;
                break;
            case DataGridView table:
                table.BackgroundColor = Surface;
                table.BorderStyle = BorderStyle.None;
                table.GridColor = Border;
                table.EnableHeadersVisualStyles = false;
                table.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
                table.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
                table.ColumnHeadersDefaultCellStyle.BackColor = Surface;
                table.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
                table.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
                table.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
                table.ColumnHeadersHeight = 38;
                table.DefaultCellStyle.BackColor = Background;
                table.DefaultCellStyle.ForeColor = Text;
                table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(25, 52, 73);
                table.DefaultCellStyle.SelectionForeColor = Text;
                table.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(23, 36, 48);
                table.RowTemplate.Height = 34;
                break;
        }
        foreach (Control child in control.Controls)
        {
            // The official web pages keep their own colors.
            if (child is not Microsoft.Web.WebView2.WinForms.WebView2) Apply(child);
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal static void ApplyWindow(Form form)
    {
        // Unsupported Windows versions ignore the optional title-bar colors.
        int dark = 1, caption = Background.R | Background.G << 8 | Background.B << 16;
        int text = Text.R | Text.G << 8 | Text.B << 16;
        DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
    }

    internal static void Primary(Button button)
    {
        button.BackColor = Color.FromArgb(23, 108, 158);
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(24, 122, 178);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(20, 91, 135);
    }
}
