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
    private readonly Button refresh = new() { Text = "刷新设备", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
    private CancellationTokenSource? session;
    private bool closeAfterStop;
    private PitchPreset settings = new("原声", 0, 0);
    private int outputVolume = 85;

    public LocalForm()
    {
        Text = "声线工坊 · 本地实时变声";
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
        toggle.BackColor = Accent;
        toggle.ForeColor = Color.FromArgb(16, 22, 32);
        refresh.BackColor = Color.FromArgb(28, 38, 53);

        preset.Items.AddRange([
            new PitchPreset("原声", 0, 0),
            new PitchPreset("明亮", 4, 0.22),
            new PitchPreset("轻柔高音", 6, 0.12),
            new PitchPreset("童趣高音", 9, 0.20),
            new PitchPreset("沉稳低音", -4, -0.20),
            new PitchPreset("深沉低音", -7, -0.35)
        ]);
        preset.SelectedIndexChanged += (_, _) =>
        {
            if (preset.SelectedItem is PitchPreset selected)
            {
                pitch.Value = selected.Semitones;
                UpdateSettings();
            }
        };
        pitch.ValueChanged += (_, _) => UpdateSettings();
        volume.ValueChanged += (_, _) => UpdateSettings();
        preset.SelectedIndex = 1;
        refresh.Click += (_, _) => RefreshDevices();
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
        var tone = (preset.SelectedItem as PitchPreset)?.Tone ?? 0;
        Volatile.Write(ref settings, new PitchPreset("", pitch.Value, tone));
        Volatile.Write(ref outputVolume, volume.Value);
        pitchLabel.Text = $"音高  {pitch.Value:+0;-0;0} 半音";
        volumeLabel.Text = $"输出音量  {volume.Value}%";
    }

    private async Task StartAsync()
    {
        if (input.SelectedItem is not AudioDevice source || output.SelectedItem is not AudioDevice destination)
            return;
        using var current = new CancellationTokenSource();
        session = current;
        input.Enabled = output.Enabled = refresh.Enabled = false;
        toggle.Text = "结束变声";
        status.Text = "正在打开音频设备…";
        var level = new Progress<int>(peak =>
        {
            if (session == current && !current.IsCancellationRequested)
            {
                meter.Value = Math.Min(100, (int)(Math.Sqrt(peak / 32768.0) * 100));
                status.Text = peak == 0 ? "实时变声中 · 当前输入为静音" : "实时变声中 · 本机处理";
            }
        });
        try { await Task.Run(() => RunAsync(source, destination, level, current.Token)); }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { status.Text = "已结束"; }
        catch (Exception error) { status.Text = error.GetBaseException().Message; }
        finally
        {
            session = null;
            meter.Value = 0;
            input.Enabled = output.Enabled = refresh.Enabled = toggle.Enabled = true;
            toggle.Text = "开始变声";
            if (closeAfterStop)
                Close();
        }
    }

    private async Task RunAsync(AudioDevice source, AudioDevice destination, IProgress<int> level, CancellationToken token)
    {
        const int blockMilliseconds = 20;
        using var player = new WaveAudio.StreamingOutput(destination, blockMilliseconds);
        using var capture = WaveAudio.CreateCapture(source, VoicePreset.SampleRate, blockMilliseconds);
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        capture.DataAvailable += (_, frame) =>
        {
            if (frame.BytesRecorded > 0 && !token.IsCancellationRequested)
                frames.Writer.TryWrite(frame.Buffer.AsSpan(0, frame.BytesRecorded).ToArray());
        };
        capture.RecordingStopped += (_, result) => frames.Writer.TryComplete(
            result.Exception ?? new InvalidOperationException("麦克风已停止。"));

        token.ThrowIfCancellationRequested();
        capture.StartRecording();
        try
        {
            var processor = new PitchProcessor();
            var count = 0;
            var peak = 0;
            await foreach (var frame in frames.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var current = Volatile.Read(ref settings);
                peak = Math.Max(peak, processor.Process(frame, current.Semitones, current.Tone, Volatile.Read(ref outputVolume) / 100.0));
                await player.QueueAsync(frame, token).ConfigureAwait(false);
                if (++count % 6 == 0)
                {
                    level.Report(peak);
                    peak = 0;
                }
            }
        }
        finally { capture.StopRecording(); }
    }
}
