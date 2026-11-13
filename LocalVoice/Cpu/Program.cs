using System.Threading.Channels;

namespace VoiceChange;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new LocalForm());
    }
}

internal sealed class LocalForm : Form
{
    private static readonly Color Accent = Color.FromArgb(121, 230, 205);
#if DIRECTML
    private readonly ComboBox backend = Choice();
#endif
    private readonly ComboBox input = Choice();
    private readonly ComboBox output = Choice();
    private readonly ComboBox preset = Choice();
    private readonly TrackBar pitch = new() { Minimum = -12, Maximum = 12, TickFrequency = 3, Dock = DockStyle.Fill };
    private readonly TrackBar volume = new() { Minimum = 0, Maximum = 150, Value = 85, TickFrequency = 25, Dock = DockStyle.Fill };
    private readonly Label pitchLabel = new() { Dock = DockStyle.Fill };
    private readonly Label volumeLabel = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Text = "就绪 · 建议戴耳机试听，避免扬声器声音回到麦克风。", Dock = DockStyle.Fill };
    private readonly ProgressBar meter = new() { Dock = DockStyle.Fill };
    private readonly Button toggle = new() { Text = "开始变声", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
    private readonly Button refresh = new() { Text = "刷新设备和音色", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
    private CancellationTokenSource? session;
    private bool closeAfterStop;
    private int semitones;
    private int outputVolume = 85;

    public LocalForm()
    {
        Text = "声线工坊 · RVC CPU 原型";
        Font = new Font("Microsoft YaHei UI", 10f);
        ClientSize = new Size(610, 540);
        MinimumSize = new Size(520, 570);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(16, 22, 32);
        ForeColor = Color.White;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 14), ColumnCount = 2, RowCount = 12
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var height in new[] { 54, 30, 42, 26, 44, 26, 50, 26, 50, 22, 48, 40 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(layout);

        AddWide(new Label { Text = "声线工坊", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), Dock = DockStyle.Fill }, 0);
        layout.Controls.Add(Caption("输入麦克风"), 0, 1);
        layout.Controls.Add(Caption("输出设备"), 1, 1);
        layout.Controls.Add(input, 0, 2);
        layout.Controls.Add(output, 1, 2);
        AddWide(Caption("音色预设"), 3);
        AddWide(preset, 4);
        AddWide(pitchLabel, 5);
        AddWide(pitch, 6);
        AddWide(volumeLabel, 7);
        AddWide(volume, 8);
        AddWide(meter, 9);
        layout.Controls.Add(toggle, 0, 10);
        layout.Controls.Add(refresh, 1, 10);
        AddWide(status, 11);
#if DIRECTML
        Text = "声线工坊 · RVC GPU";
        ClientSize = new Size(610, 610);
        layout.RowCount = 13;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.Controls.Add(Caption("推理设备（停止后切换）"), 0, 12);
        layout.Controls.Add(backend, 1, 12);
        backend.Items.AddRange(["GPU 0 · DirectML", "GPU 1 · DirectML", "GPU 2 · DirectML", "CPU"]);
        backend.SelectedIndex = 0;
#endif
        toggle.BackColor = Accent;
        toggle.ForeColor = Color.FromArgb(16, 22, 32);
        refresh.BackColor = Color.FromArgb(28, 38, 53);

        RefreshVoices();
        pitch.ValueChanged += (_, _) => UpdateSettings();
        volume.ValueChanged += (_, _) => UpdateSettings();
        UpdateSettings();
        refresh.Click += (_, _) => { RefreshDevices(); RefreshVoices(); };
        toggle.Click += async (_, _) =>
        {
            if (session is not null)
            {
                toggle.Enabled = false;
                session.Cancel();
                return;
            }
            await StartAsync();
        };
        FormClosing += (_, e) =>
        {
            if (session is null)
                return;
            e.Cancel = true;
            closeAfterStop = true;
            session.Cancel();
        };
        RefreshDevices();

        void AddWide(Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }
    }

    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private static Label Caption(string text) => new() { Text = text, Dock = DockStyle.Fill };

    private static string ModelDirectory => Path.Combine(AppContext.BaseDirectory, "models");

    private void RefreshVoices()
    {
        var previous = preset.SelectedItem as string ?? "guanguanV1";
        preset.Items.Clear();
        if (Directory.Exists(ModelDirectory))
        {
            var voices = Directory.EnumerateFiles(ModelDirectory, "*.onnx")
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .Where(name => !name.StartsWith("contentvec", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            preset.Items.AddRange(voices);
        }
        if (preset.Items.Count > 0)
            preset.SelectedIndex = Math.Max(0, preset.Items.IndexOf(previous));
    }

    private void RefreshDevices()
    {
        Fill(input, WaveAudio.Devices(input: true));
        Fill(output, WaveAudio.Devices(input: false));
    }

    private static void Fill(ComboBox box, List<AudioDevice> devices)
    {
        var current = box.SelectedItem as AudioDevice;
        box.Items.Clear();
        box.Items.AddRange(devices.ToArray());
        box.SelectedIndex = Math.Max(0, devices.FindIndex(device => device == current));
    }

    private void UpdateSettings()
    {
        Volatile.Write(ref semitones, pitch.Value);
        Volatile.Write(ref outputVolume, volume.Value);
        pitchLabel.Text = $"音高  {pitch.Value:+0;-0;0} 半音";
        volumeLabel.Text = $"输出音量  {volume.Value}%";
    }

    private async Task StartAsync()
    {
        if (input.SelectedItem is not AudioDevice source || output.SelectedItem is not AudioDevice destination)
            return;
        if (preset.SelectedItem is not string voice)
        {
            status.Text = "请把兼容的音色 ONNX 文件放入 models 文件夹。";
            return;
        }
        using var current = new CancellationTokenSource();
        session = current;
        preset.Enabled = input.Enabled = output.Enabled = refresh.Enabled = false;
        toggle.Text = "结束变声";
        int? adapter = null;
#if DIRECTML
        adapter = backend.SelectedIndex is >= 0 and < 3 ? backend.SelectedIndex : null;
        backend.Enabled = false;
#endif
        var device = adapter is int id ? $"GPU {id} · DirectML" : "CPU";
        status.Text = $"正在加载模型 · {device}";
        var level = new Progress<int>(peak =>
        {
            if (session == current && !current.IsCancellationRequested)
            {
                meter.Value = Math.Min(100, (int)(Math.Sqrt(peak / 32768.0) * 100));
                status.Text = peak == 0 ? "实时变声中 · 当前输入为静音" : $"实时变声中 · {device}";
            }
        });
        try { await Task.Run(() => RunAsync(source, destination, voice, adapter, level, current.Token)); }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { status.Text = "已结束"; }
        catch (Exception error) { status.Text = error.GetBaseException().Message; }
        finally
        {
            session = null;
#if DIRECTML
            backend.Enabled = true;
#endif
            meter.Value = 0;
            preset.Enabled = input.Enabled = output.Enabled = refresh.Enabled = toggle.Enabled = true;
            toggle.Text = "开始变声";
            if (closeAfterStop)
                Close();
        }
    }

    private async Task RunAsync(AudioDevice source, AudioDevice destination, string voice, int? adapter, IProgress<int> level, CancellationToken token)
    {
        using var processor = new CpuConverter(ModelDirectory, voice + ".onnx", adapter);
        var frameBytes = CpuConverter.InputSamples * 2;
        _ = processor.Convert(new byte[frameBytes], 0, 1);
        token.ThrowIfCancellationRequested();

        using var player = new WaveAudio.StreamingOutput(destination, CpuConverter.BlockMs);
        using var capture = WaveAudio.CreateCapture(source, WaveAudio.InputRate, CpuConverter.BlockMs);
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        var accumulator = new byte[frameBytes];
        var offset = 0;
        capture.DataAvailable += (_, frame) =>
        {
            for (int read = 0; read < frame.BytesRecorded && !token.IsCancellationRequested;)
            {
                int count = Math.Min(frame.BytesRecorded - read, frameBytes - offset);
                frame.Buffer.AsSpan(read, count).CopyTo(accumulator.AsSpan(offset));
                read += count;
                offset += count;
                if (offset != frameBytes)
                    continue;
                frames.Writer.TryWrite(accumulator);
                accumulator = new byte[frameBytes];
                offset = 0;
            }
        };
        capture.RecordingStopped += (_, result) => frames.Writer.TryComplete(
            result.Exception ?? new InvalidOperationException("麦克风已停止。"));

        capture.StartRecording();
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                int peak = 0;
                for (int i = 0; i < frame.Length; i += 2)
                    peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(frame, i)));
                var output = processor.Convert(frame, Volatile.Read(ref semitones), Volatile.Read(ref outputVolume) / 100f);
                await player.QueueAsync(output, token).ConfigureAwait(false);
                level.Report(peak);
            }
        }
        finally { capture.StopRecording(); }
    }
}
