namespace VoiceChange;

internal static class WindowStyle
{
    internal static readonly Color Accent = Color.FromArgb(121, 230, 205);
    internal static void Apply(Form form, TableLayoutPanel panel, string subtitle)
    {
        form.BackColor = Color.FromArgb(16, 22, 32);
        form.ForeColor = Color.White;
        form.Font = new Font("Microsoft YaHei UI", 10);
        panel.AutoScroll = true;
        using var stream = typeof(WindowStyle).Assembly.GetManifestResourceStream("XVoice.Logo")!;
        using var logo = Image.FromStream(stream);
        panel.Controls.Add(new PictureBox { Image = new Bitmap(logo), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(65, 65) }, 0, panel.RowCount++);
        var title = new Label { Text = "声线工坊", Font = new Font(form.Font.FontFamily, 24, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(title, 0, panel.RowCount++);
        panel.SetColumnSpan(title, 2);
        var detail = new Label { Text = subtitle, ForeColor = Accent, AutoSize = true, Margin = new Padding(3, 3, 3, 16) };
        panel.Controls.Add(detail, 0, panel.RowCount++);
        panel.SetColumnSpan(detail, 2);
    }

    internal static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Accent;
        button.ForeColor = Color.FromArgb(16, 22, 32);
        button.Padding = new Padding(8, 4, 8, 4);
    }
}
