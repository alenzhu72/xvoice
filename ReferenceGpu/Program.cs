using System.Diagnostics;
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
    private readonly TrackBar pitch = new() { Minimum = -24, Maximum = 24, Value = 13, TickFrequency = 3, Dock = DockStyle.Fill };
    private readonly TrackBar volume = new() { Minimum = 0, Maximum = 150, Value = 100, TickFrequency = 25, Dock = DockStyle.Fill };
    private readonly Label pitchLabel = new() { Dock = DockStyle.Fill };
    private readonly Label volumeLabel = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Text = "就绪 · 建议戴耳机试听，避免扬声器声音回到麦克风。", Dock = DockStyle.Fill };
    private readonly ProgressBar meter = new() { Dock = DockStyle.Fill };
    private readonly Button toggle = new() { Text = "开始变声", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
    private readonly Button refresh = new() { Text = "刷新设备和音色", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
    private readonly Button importVoice = new() { Text = "导入新音色", Dock = DockStyle.Fill };
    private CancellationTokenSource? session;
    private bool closeAfterStop;
    private bool importing;
    private int semitones;
    private int outputVolume = 100;

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
        Text = "声线工坊 · RVC GPU · 原版参数";
        ClientSize = new Size(610, 610);
        layout.RowCount = 13;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.Controls.Add(Caption("推理设备（停止后切换）"), 0, 12);
        layout.Controls.Add(backend, 1, 12);
        backend.Items.AddRange(["GPU 0 · DirectML", "GPU 1 · DirectML", "GPU 2 · DirectML", "CPU"]);
        backend.SelectedIndex = 0;
#endif
        ClientSize = new Size(610, 710);
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        AddWide(importVoice, layout.RowCount - 1);
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        AddWide(new Label { Text = "130 ms 音频块 · 80 ms 淡化 · 2.00 s 上下文 · 索引 0.35\nRMVPE · 响度混合 0.50 · 门限 -60 dB · 输出 44100 Hz",
            Dock = DockStyle.Fill }, layout.RowCount - 1);
        importVoice.BackColor = Color.FromArgb(28, 38, 53);
        importVoice.Click += async (_, _) => { try { await ImportVoiceAsync(); } catch (Exception error) { MessageBox.Show(this, error.Message); } };
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
            if (importing)
            {
                e.Cancel = true;
                MessageBox.Show(this, "正在转换或导入音色，请完成后关闭。");
                return;
            }
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
                .Where(name => !name.StartsWith("contentvec", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "rmvpe", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            preset.Items.AddRange(voices);
        }
        if (preset.Items.Count > 0)
            preset.SelectedIndex = Math.Max(0, preset.Items.IndexOf(previous));
    }

    private async Task ImportVoiceAsync()
    {
        using var file = new OpenFileDialog
        {
            Title = "选择以前训练的 PTH 或导出的 ONNX",
            Filter = "音色模型 (*.pth;*.onnx)|*.pth;*.onnx"
        };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        string voice = Path.GetFileNameWithoutExtension(file.FileName);
        if (voice.StartsWith("contentvec", StringComparison.OrdinalIgnoreCase) ||
            voice.Equals("rmvpe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "请选择音色模型，例如 linzhilin1.onnx。");
            return;
        }
        string sourceModel = file.FileName;
        string index = Path.ChangeExtension(sourceModel, ".ivf");
        string? checkpoint = null;
        if (Path.GetExtension(sourceModel).Equals(".pth", StringComparison.OrdinalIgnoreCase))
            checkpoint = sourceModel;
        else
        {
            using var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
            using var model = new Microsoft.ML.OnnxRuntime.InferenceSession(sourceModel, options);
            if (!model.InputMetadata.TryGetValue("features", out var shape) || shape.Dimensions.Length != 3)
                throw new InvalidDataException("所选 ONNX 不是本程序支持的音色模型。");
            if (shape.Dimensions[1] != 222)
            {
                checkpoint = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sourceModel))!, voice + ".pth");
                if (!File.Exists(checkpoint))
                {
                    using var select = new OpenFileDialog { Title = "旧版 ONNX 需要重导出，请选择原始 PTH", Filter = "RVC v1 (*.pth)|*.pth" };
                    if (select.ShowDialog(this) != DialogResult.OK) return;
                    checkpoint = select.FileName;
                }
            }
        }
        if (checkpoint is not null)
        {
            index = Path.ChangeExtension(checkpoint, ".index");
            if (!File.Exists(index))
            {
                using var select = new OpenFileDialog { Title = "选择该音色配套的原始索引", Filter = "RVC index (*.index)|*.index" };
                if (select.ShowDialog(this) != DialogResult.OK) return;
                index = select.FileName;
            }
        }
        if (!File.Exists(index))
        {
            MessageBox.Show(this, $"同一文件夹中缺少 {voice}.ivf，请先完成训练导出。");
            return;
        }
        Directory.CreateDirectory(ModelDirectory);
        string target = Path.Combine(ModelDirectory, voice + ".onnx");
        string targetIndex = Path.Combine(ModelDirectory, voice + ".ivf");
        if (File.Exists(target) || File.Exists(targetIndex))
        {
            MessageBox.Show(this, "同名音色已经存在。可点击刷新设备和音色，或给新模型及索引使用新的同名文件名。");
            return;
        }
        importing = true;
        importVoice.Enabled = toggle.Enabled = refresh.Enabled = false;
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        string? staging = null;
        try
        {
            status.Text = "正在导入音色…";
            if (checkpoint is not null)
            {
                var environment = LocateTrainingEnvironment();
                if (environment is null) return;
                staging = Path.Combine(Path.GetTempPath(), "XVoice-model-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                status.Text = "正在一次性转换训练模型，请等待…";
                var info = new ProcessStartInfo(Path.Combine(environment.Value.Root, "runtime", "python.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    WorkingDirectory = environment.Value.Root
                };
                foreach (var arg in new[] { "-X", "utf8", "-u", Path.Combine(AppContext.BaseDirectory, "import_training.py"),
                    environment.Value.Root, checkpoint, index, staging, environment.Value.Deps })
                    info.ArgumentList.Add(arg);
                info.Environment["PYTHONIOENCODING"] = "utf-8";
                info.Environment["PATH"] = Path.Combine(environment.Value.Root, "runtime") + ";" + info.Environment["PATH"];
                using var conversion = Process.Start(info)!;
                var stdout = conversion.StandardOutput.ReadToEndAsync();
                var stderr = conversion.StandardError.ReadToEndAsync();
                await conversion.WaitForExitAsync();
                var diagnostic = await stdout + Environment.NewLine + await stderr;
                File.WriteAllText(Path.Combine(staging, "conversion.log"), diagnostic);
                if (conversion.ExitCode != 0)
                    throw new IOException("转换失败。日志：" + Path.Combine(staging, "conversion.log"));
                sourceModel = Path.Combine(staging, "voice.onnx");
                index = Path.Combine(staging, "voice.ivf");
            }
            await Task.Run(() =>
            {
                File.Copy(sourceModel, temporary);
                File.Copy(index, temporary + ".ivf");
                File.Move(temporary, target);

                try { File.Move(temporary + ".ivf", targetIndex); }
                catch { File.Delete(target); throw; }
            });
            RefreshVoices();
            preset.SelectedItem = voice;
            status.Text = $"已导入 {voice}，请选择 GPU · DirectML 试听。";
        }
        catch (Exception error) { status.Text = error.Message; }
        finally
        {
            try { File.Delete(temporary); File.Delete(temporary + ".ivf"); } catch (IOException) { }
            importing = false;
            importVoice.Enabled = toggle.Enabled = refresh.Enabled = true;
        }
    }

    private (string Root, string Deps)? LocateTrainingEnvironment()
    {
        foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(origin); directory is not null; directory = directory.Parent)
            {
                string root = Path.Combine(directory.FullName, "bin", "rvc-runtime", "RVC20260718Nvidia");
                string deps = Path.Combine(directory.FullName, "obj", "rvc-export-deps");
                if (File.Exists(Path.Combine(root, "runtime", "python.exe")) && Directory.Exists(deps))
                    return (root, deps);
            }
        }
        MessageBox.Show(this, "旧训练模型首次转换需要 RVC 开发环境。请选择 RVC 根目录和导出依赖目录，转换完成后播放只用 .NET。");
        using var rootPicker = new FolderBrowserDialog { Description = "选择 RVC 根目录" };
        if (rootPicker.ShowDialog(this) != DialogResult.OK) return null;
        using var depsPicker = new FolderBrowserDialog { Description = "选择 export-deps 或 obj/rvc-export-deps" };
        if (depsPicker.ShowDialog(this) != DialogResult.OK) return null;
        return (rootPicker.SelectedPath, depsPicker.SelectedPath);
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
        importVoice.Enabled = preset.Enabled = input.Enabled = output.Enabled = refresh.Enabled = false;
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
            importVoice.Enabled = preset.Enabled = input.Enabled = output.Enabled = refresh.Enabled = toggle.Enabled = true;
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





