namespace BoothFreeCollector;

internal static class CollectorTheme
{
    internal static readonly Color Background = Color.FromArgb(18, 18, 20);
    internal static readonly Color Surface = Color.FromArgb(28, 28, 31);
    internal static readonly Color Border = Color.FromArgb(57, 57, 63);
    internal static readonly Color Text = Color.FromArgb(239, 239, 242);
    internal static readonly Color Muted = Color.FromArgb(171, 171, 181);
    internal static readonly Color Accent = Color.FromArgb(227, 67, 99);
    internal static readonly Color Warning = Color.FromArgb(255, 116, 132);
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
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 39, 44);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(69, 40, 49);
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
                link.LinkColor = Warning;
                link.ActiveLinkColor = Text;
                link.VisitedLinkColor = Warning;
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
                table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(91, 37, 53);
                table.DefaultCellStyle.SelectionForeColor = Text;
                table.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(24, 24, 27);
                table.RowTemplate.Height = 34;
                break;
        }
        foreach (Control child in control.Controls)
        {
            // The official web pages keep their own colors.
            if (child is not Microsoft.Web.WebView2.WinForms.WebView2) Apply(child);
        }
    }

    internal static void Primary(Button button)
    {
        button.BackColor = Color.FromArgb(142, 39, 65);
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(175, 44, 76);
    }
}
