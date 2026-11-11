using System.Buffers.Binary;

namespace VoiceChange;

internal sealed class MainForm : Form
{
    internal static readonly Color Background = Color.FromArgb(17, 22, 35);
    private static readonly Color Surface = Color.FromArgb(31, 39, 57);
    private static readonly Color Ink = Color.FromArgb(240, 244, 255);
    private static readonly Color Muted = Color.FromArgb(176, 189, 215);
    private static readonly Color Accent = Color.FromArgb(111, 225, 201);
    private const string Sample = "你好，这里是声线工坊。很高兴和你一起聊天！";

    private readonly ComboBox microphone = new();
    private readonly ComboBox output = new();
    private readonly ComboBox voice = new();
    private readonly TextBox transcript = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 300, Text = Sample };
    private readonly Button record = MakeButton("开始", primary: true);
    private readonly Button speak = MakeButton("朗读文字");
    private readonly Button test = MakeButton("测试输出");
    private readonly Button settingsButton = MakeButton("设置");
    private readonly Button refresh = MakeButton("刷新设备");
    private readonly Button cancel = MakeButton("取消");
    private readonly Label status = Caption("");

    private SpeechSettings settings;
    private CancellationTokenSource? operation;
    private bool live;
    private bool closeRequested;

    public MainForm()
    {
        Text = SpeechSettings.IsDomesticBuild ? "声线工坊 · 国内版" : "声线工坊 · 中文语音变声";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(716, 508);
        MinimumSize = new Size(690, 510);
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 10F);

        try
        {
            settings = SpeechSettings.Load();
            status.Text = HasSettings ? "就绪 · 选择音色后点击「开始」。" : "首次使用请在「设置」填写语音服务配置。";
        }
        catch (Exception)
        {
            settings = new("", "");
            status.Text = SpeechSettings.IsCustomerBuild
                ? "语音服务配置无法读取，请联系提供方。"
                : "无法读取保存的语音设置。请打开「设置」重新保存。";
        }
        voice.Items.AddRange(SpeechSettings.IsDomesticBuild ? DomesticSpeech.Voices : [
            new VoicePreset("温柔女声 · 晓晓", "zh-CN-XiaoxiaoNeural"),
            new VoicePreset("活泼女声 · 晓伊", "zh-CN-XiaoyiNeural"),
            new VoicePreset("清亮童声 · 晓双", "zh-CN-XiaoshuangNeural"),
            new VoicePreset("青年男声 · 云希", "zh-CN-YunxiNeural")
        ]);
        voice.SelectedIndex = 0;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 16, 24, 16),
            ColumnCount = 2,
            RowCount = 7
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var height in new[] { 78, 60, 60, 32 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(layout);

        var header = new Panel { Dock = DockStyle.Fill };
        var title = Caption("声线工坊", Ink);
        title.Dock = DockStyle.None;
        title.Font = new Font(Font.FontFamily, 22F, FontStyle.Bold);
        title.SetBounds(0, 0, 340, 43);
        var subtitle = Caption("普通话实时变声   /   开始后持续听取并播放", Accent);
        subtitle.Dock = DockStyle.None;
        subtitle.SetBounds(2, 45, 540, 24);
        header.Controls.AddRange([title, subtitle]);
        AddWide(header, 0);

        layout.Controls.Add(Field("输入麦克风", microphone), 0, 1);
        layout.Controls.Add(Field("播放设备 / 虚拟声卡", output), 1, 1);
        layout.Controls.Add(Field("音色模板", voice), 0, 2);
        var devices = ButtonRow(test, refresh);
        devices.Padding = new Padding(0, 18, 0, 0);
        layout.Controls.Add(devices, 1, 2);

        AddWide(Caption("最近识别结果 / 试听文字"), 3);
        StyleInput(transcript);
        transcript.Margin = new Padding(0, 0, 6, 4);
        AddWide(transcript, 4);
        AddWide(SpeechSettings.IsCustomerBuild
            ? ButtonRow(record, speak, cancel)
            : ButtonRow(record, speak, settingsButton, cancel), 5);
        status.Padding = new Padding(0, 3, 0, 0);
        AddWide(status, 6);

        refresh.Click += (_, _) => LoadDevices();
        settingsButton.Click += (_, _) => OpenSettings();
        record.Click += async (_, _) =>
        {
            if (live)
            {
                status.Text = "正在结束实时变声…";
                operation!.Cancel();
                UpdateButtons();
                return;
            }

            if (!HasSettings && (SpeechSettings.IsCustomerBuild || !OpenSettings()))
                return;
            await StartLiveAsync();
        };
        speak.Click += async (_, _) => await RunAsync(async token =>
        {
            var text = transcript.Text.Trim();
            if (text.Length == 0)
                throw new ArgumentException("请输入要朗读的文字。");
            var preset = (VoicePreset)voice.SelectedItem!;
            var device = (AudioDevice)output.SelectedItem!;
            status.Text = $"正在合成 · {preset.Name}";
            using var pcm = new MemoryStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await CreateSpeech().SpeakAsync(text, preset, (stream, ct) => stream.CopyToAsync(pcm, ct), timeout.Token);
            if (pcm.Length == 0 || pcm.Length % 2 != 0)
                throw new InvalidOperationException("语音合成返回了无效音频。");
            status.Text = $"正在播放 · {preset.Name}";
            await WaveAudio.PlayAsync(device, pcm.ToArray(), token);
            status.Text = $"已播放 · {preset.Name}";
        });
        test.Click += async (_, _) => await RunAsync(async token =>
        {
            status.Text = "正在向所选设备播放测试音…";
            await WaveAudio.PlayAsync((AudioDevice)output.SelectedItem!, TestTone(), token);
            status.Text = "测试音已发送到所选设备。";
        });
        cancel.Click += (_, _) => operation?.Cancel();
        LoadDevices();
        UpdateButtons();

        void AddWide(Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }
    }

    private void LoadDevices()
    {
        FillDevices(microphone, WaveAudio.Devices(input: true));
        FillDevices(output, WaveAudio.Devices(input: false));
    }

    private static void FillDevices(ComboBox combo, List<AudioDevice> devices)
    {
        var previous = combo.SelectedItem as AudioDevice;
        combo.Items.Clear();
        combo.Items.AddRange(devices.ToArray());
        var index = previous is null ? 0 : devices.IndexOf(previous);
        combo.SelectedIndex = Math.Max(index, 0);
    }

    private bool HasSettings => settings.IsConfigured;

    private bool OpenSettings()
    {
        using var dialog = new SettingsForm(settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;
        settings = dialog.Settings;
        status.Text = "语音服务设置已保存。";
        return true;
    }

    private Task StartLiveAsync() => RunAsync(async token =>
    {
        var engine = new LiveVoice(CreateSpeech());
        var input = (AudioDevice)microphone.SelectedItem!;
        var destination = (AudioDevice)output.SelectedItem!;
        var preset = (VoicePreset)voice.SelectedItem!;
        var textProgress = new Progress<string>(text =>
        {
            if (!token.IsCancellationRequested && operation?.Token == token)
                transcript.Text = text;
        });
        var statusProgress = new Progress<string>(message =>
        {
            if (!token.IsCancellationRequested && operation?.Token == token)
                status.Text = message;
        });
        transcript.Clear();
        status.Text = "正在启动实时变声…";
        await Task.Run(() => engine.RunAsync(input, destination, preset, textProgress, statusProgress, token), token);
    }, isLive: true);

    private ISpeechService CreateSpeech() => settings.CreateService();

    private async Task RunAsync(Func<CancellationToken, Task> action, bool isLive = false)
    {
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        live = isLive;
        UpdateButtons();
        try
        {
            await action(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            status.Text = cancellation.IsCancellationRequested
                ? (isLive ? "实时变声已结束。" : "已取消。")
                : "请求超时，请检查网络后重试。";
        }
        catch (Exception error)
        {
            status.Text = AzureSpeech.DescribeError(error);
        }
        finally
        {
            operation = null;
            live = false;
            if (closeRequested)
                Close();
            else
                UpdateButtons();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (operation is not null)
        {
            closeRequested = true;
            operation.Cancel();
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    private void UpdateButtons()
    {
        var idle = operation is null;
        microphone.Enabled = output.Enabled = voice.Enabled = idle;
        transcript.ReadOnly = !idle;
        speak.Enabled = idle && HasSettings;
        test.Enabled = settingsButton.Enabled = refresh.Enabled = idle;
        record.Enabled = idle ? !SpeechSettings.IsCustomerBuild || HasSettings : live && !operation!.IsCancellationRequested;
        record.Text = live ? "结束" : "开始";
        cancel.Enabled = !idle && !live;
    }

    private static byte[] TestTone()
    {
        const int samples = AzureSpeech.OutputRate / 3;
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var envelope = Math.Min(1, Math.Min(i, samples - i - 1) / 480.0);
            var sample = (short)(4200 * envelope * Math.Sin(2 * Math.PI * 660 * i / AzureSpeech.OutputRate));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample);
        }
        return pcm;
    }

    internal static Control Field(string label, Control input)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 0) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caption = Caption(label);
        caption.Margin = Padding.Empty;
        panel.Controls.Add(caption, 0, 0);
        StyleInput(input);
        input.Margin = Padding.Empty;
        panel.Controls.Add(input, 0, 1);
        return panel;
    }

    private static void StyleInput(Control input)
    {
        input.Dock = DockStyle.Fill;
        input.BackColor = Surface;
        input.ForeColor = Ink;
        if (input is TextBox box)
            box.BorderStyle = BorderStyle.FixedSingle;
        if (input is ComboBox combo)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
        }
    }

    internal static Label Caption(string text, Color? color = null) => new()
    {
        Dock = DockStyle.Fill,
        Text = text,
        ForeColor = color ?? Muted,
        UseMnemonic = false
    };

    internal static FlowLayoutPanel ButtonRow(params Button[] buttons)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        row.Controls.AddRange(buttons);
        return row;
    }

    internal static Button MakeButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Surface,
            ForeColor = primary ? Background : Ink,
            Size = new Size(primary ? 144 : 110, 34),
            Margin = new Padding(0, 2, 8, 0)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
