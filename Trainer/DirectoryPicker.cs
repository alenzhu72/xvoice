sealed class DirectoryPicker : Form
{
    readonly TextBox address = new() { Dock = DockStyle.Fill };
    readonly ListBox folders = new() { Dock = DockStyle.Fill };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = true };
    readonly Button choose = new() { Text = "选择此文件夹", AutoSize = true };
    int request;
    public string SelectedPath { get; private set; } = "";

    public DirectoryPicker(string initial)
    {
        Text = "选择文件夹";
        Font = new Font("Microsoft YaHei UI", 10);
        Size = new Size(680, 480);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        var open = new Button { Text = "打开" };
        var up = new Button { Text = "上一级" };
        layout.Controls.Add(address, 0, 0);
        layout.Controls.Add(open, 1, 0);
        layout.Controls.Add(up, 2, 0);
        layout.Controls.Add(folders, 0, 1); layout.SetColumnSpan(folders, 3);
        layout.Controls.Add(status, 0, 2); layout.SetColumnSpan(status, 3);
        layout.Controls.Add(choose, 1, 3);
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        layout.Controls.Add(cancel, 2, 3);
        CancelButton = cancel;
        address.Text = string.IsNullOrWhiteSpace(initial) ? AppContext.BaseDirectory : initial;
        open.Click += async (_, _) => await LoadDirectory(address.Text);
        up.Click += async (_, _) =>
        {
            try { await LoadDirectory(Directory.GetParent(SelectedPath)?.FullName ?? ""); }
            catch (Exception error) { status.Text = error.Message; }
        };
        folders.DoubleClick += async (_, _) =>
        {
            if (folders.SelectedItem is string directory) await LoadDirectory(directory);
        };
        address.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await LoadDirectory(address.Text);
        };
        choose.Click += (_, _) => { if (choose.Enabled) { DialogResult = DialogResult.OK; Close(); } };
        Shown += async (_, _) => await LoadDirectory(address.Text);
    }

    async Task LoadDirectory(string input)
    {
        int current = ++request;
        choose.Enabled = false;
        folders.Items.Clear();
        status.Text = "正在读取文件夹…";
        try
        {
            // File system enumeration runs off the UI thread, without loading Explorer extensions.
            var result = await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(input))
                    return (Path: "", Folders: Directory.GetLogicalDrives());
                string path = Path.GetFullPath(input.Trim().Trim('"'));
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("文件夹不存在或无法访问。");
                return (Path: path, Folders: Directory.GetDirectories(path).Order(StringComparer.OrdinalIgnoreCase).ToArray());
            });
            if (IsDisposed || current != request) return;
            SelectedPath = result.Path;
            address.Text = SelectedPath;
            folders.Items.AddRange(result.Folders);
            choose.Enabled = SelectedPath.Length > 0;
            status.Text = "双击进入子文件夹，或直接选择当前文件夹。";
        }
        catch (Exception error)
        {
            if (!IsDisposed && current == request) status.Text = error.Message;
        }
    }
}
