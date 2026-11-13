using System.Threading.Channels;

namespace VoiceChange;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ClientForm());
    }
}

internal sealed class ClientForm : Form
{
    private readonly TextBox address = new() { Text = "192.168.10.163:8767", Dock = DockStyle.Fill };
    private readonly ComboBox input = Choice(), output = Choice(), voice = Choice();
    private readonly NumericUpDown pitch = new() { Minimum = -12, Maximum = 12 };
    private readonly NumericUpDown volume = new() { Minimum = 0, Maximum = 150, Value = 85 };
    private readonly CheckBox custom = new() { Text = "使用本机设置（下次开始生效）", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(500, 0), Text = "建议佩戴耳机试听。" };
    private readonly Button toggle = new() { Text = "开始变声", AutoSize = true };
    private CancellationTokenSource? session;
    private bool closing;

    internal ClientForm()
    {
        Text = "声线工坊 · GPU 服务器客户端";
        ClientSize = new Size(575, 500);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);
        WindowStyle.Apply(this, panel, "局域网 GPU 实时变声");
        WindowStyle.StyleButton(toggle);
        AddRow("服务器地址", address);
        AddRow("麦克风", input);
        AddRow("播放设备", output);
        AddRow("音色", voice);
        AddRow("变声设置", custom);
        AddRow("音高（半音）", pitch);
        AddRow("输出音量（%）", volume);
        pitch.Enabled = volume.Enabled = false;
        custom.CheckedChanged += (_, _) => pitch.Enabled = volume.Enabled = custom.Checked;
        panel.Controls.Add(toggle, 1, panel.RowCount++);
        panel.Controls.Add(status, 0, panel.RowCount++);
        panel.SetColumnSpan(status, 2);
        input.Items.AddRange(WaveAudio.Devices(true).ToArray());
        output.Items.AddRange(WaveAudio.Devices(false).ToArray());
        voice.Items.AddRange(["guanguanV1", "kikiV1", "keruanV1"]);
        input.SelectedIndex = output.SelectedIndex = voice.SelectedIndex = 0;
        toggle.Click += async (_, _) => await ToggleAsync();
        FormClosing += (_, args) =>
        {
            if (session is null) return;
            closing = args.Cancel = true;
            session.Cancel();
        };

        void AddRow(string label, Control control)
        {
            int row = panel.RowCount++;
            panel.Controls.Add(new Label { Text = label, AutoSize = true }, 0, row);
            panel.Controls.Add(control, 1, row);
        }
    }

    private static ComboBox Choice() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

    private async Task ToggleAsync()
    {
        if (session is not null)
        {
            session.Cancel();
            return;
        }
        var path = "/gpu/" + voice.SelectedItem;
        if (!Uri.TryCreate("ws://" + address.Text.Trim() + path, UriKind.Absolute, out var endpoint) ||
            endpoint.AbsolutePath != path || endpoint.Query.Length > 0 || endpoint.UserInfo.Length > 0)
        {
            status.Text = "请输入服务器 IP 和端口。";
            return;
        }
        if (custom.Checked)
            endpoint = new UriBuilder(endpoint) { Query = $"pitch={(int)pitch.Value}&volume={(int)volume.Value}" }.Uri;
        var source = (AudioDevice)input.SelectedItem!;
        var destination = (AudioDevice)output.SelectedItem!;
        using var current = new CancellationTokenSource();
        session = current;
        address.Enabled = input.Enabled = output.Enabled = voice.Enabled = false;
        custom.Enabled = pitch.Enabled = volume.Enabled = false;
        toggle.Text = "结束变声";
        status.Text = "正在连接并预热模型…";
        var progress = new Progress<double>(ms =>
        {
            if (session == current && !current.IsCancellationRequested)
                status.Text = $"实时变声中 · 单块往返 {ms:F0} 毫秒";
        });
        try { await Task.Run(() => RunAsync(endpoint, source, destination, progress, current.Token)); }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { status.Text = "已结束"; }
        catch (Exception error) { status.Text = error.GetBaseException().Message; }
        finally
        {
            session = null;
            address.Enabled = input.Enabled = output.Enabled = voice.Enabled = true;
            custom.Enabled = true;
            pitch.Enabled = volume.Enabled = custom.Checked;
            toggle.Text = "开始变声";
            if (closing) Close();
        }
    }

    private static async Task RunAsync(Uri endpoint, AudioDevice source, AudioDevice destination,
        IProgress<double> progress, CancellationToken token)
    {
        using var connection = await GpuConnection.ConnectAsync(endpoint, token);
        using var player = new WaveAudio.StreamingOutput(destination, GpuConnection.BlockMs);
        using var capture = WaveAudio.CreateCapture(source, 16000, GpuConnection.BlockMs);
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });
        var pending = new byte[GpuConnection.InputBytes];
        int offset = 0;
        capture.DataAvailable += (_, e) =>
        {
            for (int read = 0; read < e.BytesRecorded && !token.IsCancellationRequested;)
            {
                int count = Math.Min(pending.Length - offset, e.BytesRecorded - read);
                Buffer.BlockCopy(e.Buffer, read, pending, offset, count);
                read += count;
                offset += count;
                if (offset == pending.Length)
                {
                    frames.Writer.TryWrite(pending);
                    pending = new byte[GpuConnection.InputBytes];
                    offset = 0;
                }
            }
        };
        capture.RecordingStopped += (_, e) => frames.Writer.TryComplete(e.Exception);
        capture.StartRecording();
        try
        {
            var playback = new byte[GpuConnection.OutputBytes];
            await foreach (var block in frames.Reader.ReadAllAsync(token))
            {
                var elapsed = await connection.ConvertAsync(block, playback, token);
                await player.QueueAsync(playback, token);
                progress.Report(elapsed);
            }
        }
        finally { capture.StopRecording(); }
    }
}

