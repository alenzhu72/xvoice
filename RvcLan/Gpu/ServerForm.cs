using System.Net;

namespace VoiceChange;

internal sealed class ServerForm : Form
{
    private readonly TextBox bind = new() { Text = "0.0.0.0", Dock = DockStyle.Fill };
    private readonly TextBox models = new() { Text = Path.Combine(AppContext.BaseDirectory, "models"), Dock = DockStyle.Fill };
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Value = 8767 };
    private readonly NumericUpDown adapter = new() { Minimum = 0, Maximum = 15 };
    private readonly NumericUpDown clients = new() { Minimum = 1, Maximum = 5, Value = 5 };
    private readonly NumericUpDown pitch = new() { Minimum = -12, Maximum = 12 };
    private readonly NumericUpDown gain = new() { Minimum = 0, Maximum = 150, Value = 85 };
    private readonly Button toggle = new() { Text = "启动服务", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(550, 0), Text = "停止服务后可修改配置。" };
    private readonly Control[] settings;
    private WebApplication? server;
    private bool closing;

    internal ServerForm()
    {
        Text = "声线工坊 · GPU 服务端";
        ClientSize = new Size(640, 490);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);
        WindowStyle.Apply(this, panel, "局域网 GPU 推理服务");
        WindowStyle.StyleButton(toggle);
        AddRow("监听 IPv4", bind);
        AddRow("端口", port);
        AddRow("GPU 编号", adapter);
        AddRow("模型目录", models);
        AddRow("最大连接数", clients);
        AddRow("音高（半音）", pitch);
        AddRow("输出音量（%）", gain);
        panel.Controls.Add(toggle, 1, panel.RowCount++);
        panel.Controls.Add(status, 0, panel.RowCount++);
        panel.SetColumnSpan(status, 2);
        settings = [bind, port, adapter, models, pitch, gain, clients];
        toggle.Click += async (_, _) => await ChangeStateAsync();
        FormClosing += async (_, e) =>
        {
            if (closing) return;
            if (!toggle.Enabled) { e.Cancel = true; return; }
            if (server is null) return;
            e.Cancel = true;
            await ChangeStateAsync();
            closing = true;
            Close();
        };

        void AddRow(string label, Control control)
        {
            int row = panel.RowCount++;
            panel.Controls.Add(new Label { Text = label, AutoSize = true }, 0, row);
            panel.Controls.Add(control, 1, row);
        }
    }

    private async Task ChangeStateAsync()
    {
        toggle.Enabled = false;
        try
        {
            if (server is not null)
            {
                var current = server;
                server = null;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await current.StopAsync(deadline.Token); }
                finally { await current.DisposeAsync(); }
                status.Text = "服务已停止。";
                return;
            }
            if (!IPAddress.TryParse(bind.Text.Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("请输入 IPv4 地址，例如 0.0.0.0。");
            var directory = Path.GetFullPath(models.Text.Trim());
            foreach (var model in new[] { "contentvec-fp32", "guanguanV1", "kikiV1", "keruanV1" })
                if (!File.Exists(Path.Combine(directory, model + ".onnx")))
                    throw new FileNotFoundException($"缺少模型 {model}.onnx。");
            var url = $"http://{ip}:{(int)port.Value}";
            int gpu = (int)adapter.Value;
            int maxClients = (int)clients.Value;
            int shift = (int)pitch.Value;
            float volume = (float)gain.Value / 100;
            status.Text = "正在验证显卡并启动服务…";
            server = await Task.Run(async () =>
            {
                using (var probe = new CpuConverter(directory, graphicsAdapter: gpu))
                    probe.Convert(new byte[CpuConverter.InputSamples * 2], shift, volume);
                var app = Program.CreateServer(url, gpu, directory, shift, volume, maxClients);
                try { await app.StartAsync(); return app; }
                catch { await app.DisposeAsync(); throw; }
            });
            status.Text = $"服务已启动 · GPU {gpu} · 端口 {(int)port.Value} · 最多 {maxClients} 路。客户端填写这台电脑的局域网 IP 和端口。";
        }
        catch (Exception error) { status.Text = error.GetBaseException().Message; }
        finally
        {
            foreach (var field in settings) field.Enabled = server is null;
            toggle.Text = server is null ? "启动服务" : "停止服务";
            toggle.Enabled = true;
        }
    }
}

