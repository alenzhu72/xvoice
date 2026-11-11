using System.Diagnostics;

namespace VoiceChange;

internal sealed class SettingsForm : Form
{
    private readonly TextBox endpoint = new() { PlaceholderText = "例如 southeastasia", MaxLength = 128 };
    private readonly TextBox key = new() { UseSystemPasswordChar = true, MaxLength = 256 };
    private readonly TextBox workspace = new() { PlaceholderText = "建议填写北京工作空间 ID，也可留空", MaxLength = 63 };
    private readonly TextBox miniMax = new() { UseSystemPasswordChar = true, MaxLength = 256 };
    private readonly Button save = MainForm.MakeButton("保存设置", primary: true);
    private readonly Button test = MainForm.MakeButton("测试连接");
    private readonly Label status = MainForm.Caption("密钥由 Windows 加密保存在当前账户中。");
    private CancellationTokenSource? testing;
    public SpeechSettings Settings { get; private set; }

    public SettingsForm(SpeechSettings current)
    {
        Settings = current;
        endpoint.Text = current.Endpoint;
        key.Text = current.Key;

        var domestic = SpeechSettings.IsDomesticBuild;
        workspace.Text = current.WorkspaceId;
        miniMax.Text = current.MiniMaxKey;
        Text = domestic ? "国内语音服务设置" : "Azure Speech 设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ClientSize = new Size(560, domestic ? 355 : 255);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = MainForm.Background;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = domestic ? 6 : 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in domestic ? new[] { 57, 57, 57, 30, 46 } : new[] { 57, 57, 46 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        if (domestic)
        {
            layout.Controls.Add(MainForm.Field("阿里云百炼 API Key · 华北2（北京）", key), 0, 0);
            layout.Controls.Add(MainForm.Field("阿里云百炼业务空间 ID（可选）", workspace), 0, 1);
            layout.Controls.Add(MainForm.Field("MiniMax 中国站 API Key", miniMax), 0, 2);
            var links = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            links.Controls.Add(Link("创建百炼 Key", "https://bailian.console.aliyun.com/cn-beijing/model/settings/api-key"));
            links.Controls.Add(Link("查找业务空间 ID", "https://help.aliyun.com/zh/model-studio/obtain-the-app-id-and-workspace-id"));
            links.Controls.Add(Link("创建 MiniMax Key", "https://platform.minimax.cn/console/keys"));
            layout.Controls.Add(links, 0, 3);
            status.Text = "密钥由 Windows 加密保存。测试连接会合成一句「你好」，可能计费。";
        }
        else
        {
            layout.Controls.Add(MainForm.Field("区域或终结点", endpoint), 0, 0);
            layout.Controls.Add(MainForm.Field("Speech 密钥", key), 0, 1);
        }
        layout.Controls.Add(MainForm.ButtonRow(save, test), 0, domestic ? 4 : 2);
        layout.Controls.Add(status, 0, domestic ? 5 : 3);
        AcceptButton = save;

        save.Click += (_, _) =>
        {
            try
            {
                var candidate = ReadSettings();
                _ = candidate.CreateService();
                candidate.Save();
                Settings = candidate;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error)
            {
                status.Text = error is ArgumentException ? error.Message : "无法保存设置，请检查当前 Windows 账户的写入权限。";
            }
        };
        test.Click += async (_, _) => await TestAsync();
    }

    private SpeechSettings ReadSettings() => new(endpoint.Text.Trim(), key.Text.Trim(), workspace.Text.Trim(), miniMax.Text.Trim());

    private LinkLabel Link(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, LinkColor = Color.LightSkyBlue, Margin = new Padding(0, 1, 14, 0) };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { status.Text = "无法打开浏览器。请复制官方文档地址后手动访问。"; }
        };
        return link;
    }

    private async Task TestAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(25));
        testing = cancellation;
        test.Enabled = save.Enabled = endpoint.Enabled = key.Enabled = workspace.Enabled = miniMax.Enabled = false;
        try
        {
            var candidate = ReadSettings();
            status.Text = SpeechSettings.IsDomesticBuild ? "正在验证百炼实时识别和 MiniMax 合成…" : "正在验证 Azure Speech 区域和密钥…";
            await candidate.CreateService().CheckConnectionAsync(cancellation.Token);
            status.Text = "连接成功。点击「保存设置」供下次使用。";
        }
        catch (Exception error)
        {
            if (!IsDisposed && Visible)
                status.Text = AzureSpeech.DescribeError(error);
        }
        finally
        {
            testing = null;
            if (!IsDisposed)
                test.Enabled = save.Enabled = endpoint.Enabled = key.Enabled = workspace.Enabled = miniMax.Enabled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        testing?.Cancel();
        base.OnFormClosing(e);
    }
}
