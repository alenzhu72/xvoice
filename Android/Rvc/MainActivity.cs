using Android;
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace VoiceChange;

[Activity(MainLauncher = true, Exported = true, ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class MainActivity : Activity
{
    private const int MicrophoneRequest = 1;
    private static readonly string[] Presets = ["kikiV1", "guanguanV1", "keruanV1"];
    private static readonly Color Background = Color.ParseColor("#101620");
    private static readonly Color Panel = Color.ParseColor("#1C2635");
    private static readonly Color Accent = Color.ParseColor("#79E6CD");
    private static readonly Color Secondary = Color.ParseColor("#B0BDD7");
    private Button toggle = null!;
    private EditText address = null!;
    private Spinner voices = null!;
    private TextView debug = null!;
    private readonly Queue<string> logs = new();
    private int prebuffer = 2, capacity = 2;
    private TextView status = null!;
    private TextView inputStatus = null!;
    private ProgressBar inputLevel = null!;
    private CancellationTokenSource? session;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        VolumeControlStream = Android.Media.Stream.Music;
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(24), Dp(32), Dp(24), Dp(24));
        content.SetBackgroundColor(Background);
        var scroll = new ScrollView(this) { FillViewport = true };
        scroll.SetFitsSystemWindows(true);
        scroll.AddView(content);
        SetContentView(scroll);

        var logo = new ImageView(this);
        logo.SetImageResource(Resource.Drawable.voice_logo);
        logo.Background = Card(Color.White, 18);
        logo.ClipToOutline = true;
        content.AddView(logo, new LinearLayout.LayoutParams(Dp(76), Dp(76)));
        Add(content, Label("声线工坊", 32, Color.White), 14);
        Add(content, Label("局域网 GPU 变声", 15, Secondary), 4);
        Add(content, Label("电脑地址", 14, Secondary), 22);
        address = new EditText(this) { Text = GetPreferences(Android.Content.FileCreationMode.Private)?.GetString("server", "192.168.10.163:8767") };
        address.SetSingleLine(true);
        Add(content, address, 4, 52);
        Add(content, Label("音色", 14, Secondary), 12);
        voices = new Spinner(this);
        var names = Presets;
        var adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, names);
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        voices.Adapter = adapter;
        voices.Background = Card(Panel, 12);
        voices.SetPadding(Dp(14), 0, Dp(12), 0);
        Add(content, voices, 8, 52);

        toggle = new Button(this) { Text = "开始变声", TextSize = 17 };
        toggle.SetTextColor(Background);
        toggle.Background = Card(Accent, 14);
        toggle.Click += (_, _) => Toggle();
        Add(content, toggle, 22, 56);
        status = Label("就绪", 14, Accent);
        Add(content, status, 12);
        inputStatus = Label("麦克风待启动", 14, Secondary);
        Add(content, inputStatus, 14);
        inputLevel = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100 };
        Add(content, inputLevel, 4, 12);
        var prefs = GetPreferences(Android.Content.FileCreationMode.Private);
        prebuffer = Math.Clamp(prefs?.GetInt("prebuffer", 2) ?? 2, 1, 5);
        capacity = Math.Clamp(prefs?.GetInt("queue", 2) ?? 2, 1, 5);
        debug = Label("等待调试数据", 14, Secondary);
        Add(content, debug, 16);
        var config = new Button(this) { Text = "配置缓冲" };
        config.Click += (_, _) => Configure();
        Add(content, config, 8);
        var logButton = new Button(this) { Text = "查看 / 复制日志" };
        logButton.Click += (_, _) => ShowLogs();
        Add(content, logButton, 4);

        Add(content, Label("建议戴耳机试听，避免扬声器声音回到麦克风。", 14, Secondary), 24);
    }

    private void Log(string line)
    {
        if (logs.Count >= 120) logs.Dequeue();
        logs.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
    }

    private void ShowLogs()
    {
        var data = logs.Count == 0 ? "暂无日志" : string.Join("\n", logs);
        using var dialog = new AlertDialog.Builder(this);
        dialog.SetTitle("调试日志")!.SetMessage(data)!
            .SetPositiveButton("复制", (_, _) =>
            {
                if (GetSystemService(ClipboardService) is Android.Content.ClipboardManager clipboard)
                    clipboard.PrimaryClip = Android.Content.ClipData.NewPlainText("XVoice log", data);
            })!.SetNegativeButton("关闭", (_, _) => { })!.Show();
    }

    private void Configure()
    {
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetPadding(Dp(20), Dp(12), Dp(20), Dp(8));
        var playback = AddOption("播放预缓冲", prebuffer);
        var capture = AddOption("录音队列上限", capacity);
        using var dialog = new AlertDialog.Builder(this);
        dialog.SetTitle("缓冲配置 · 下次开始生效")!.SetView(panel)!
            .SetPositiveButton("保存", (_, _) =>
            {
                prebuffer = playback.Progress + 1;
                capacity = capture.Progress + 1;
                GetPreferences(Android.Content.FileCreationMode.Private)?.Edit()?
                    .PutInt("prebuffer", prebuffer)?.PutInt("queue", capacity)?.Apply();
            })!.SetNegativeButton("取消", (_, _) => { })!.Show();

        SeekBar AddOption(string name, int blocks)
        {
            var label = Label($"{name} · {blocks * 160} ms", 14, Color.White);
            Add(panel, label, 8);
            var bar = new SeekBar(this) { Max = 4, Progress = blocks - 1 };
            bar.ProgressChanged += (_, e) => label.Text = $"{name} · {(e.Progress + 1) * 160} ms";
            Add(panel, bar, 4, 45);
            return bar;
        }
    }

    private void Toggle()
    {
        if (session is not null)
        {
            session.Cancel();
            toggle.Enabled = false;
            status.Text = "正在结束…";
            return;
        }
        if (CheckSelfPermission(Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.RecordAudio], MicrophoneRequest);
            return;
        }
        _ = StartAsync();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != MicrophoneRequest)
            return;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted && session is null)
            _ = StartAsync();
        else
            status.Text = "请在系统设置中允许声线工坊使用麦克风。";
    }

    private async Task StartAsync()
    {
        using var current = new CancellationTokenSource();
        session = current;
        toggle.Text = "结束变声";
        address.Enabled = voices.Enabled = false;
        status.Text = "连接电脑并预热模型…";
        var preset = Presets[Math.Max(0, voices.SelectedItemPosition)];
        int reports = 0;
        Log($"开始 {preset} · 预缓冲 {prebuffer} 块 · 队列 {capacity} 块");
        var progress = new Progress<(int Peak, string Stats)>(sample =>
        {
            if (session != current || current.IsCancellationRequested)
                return;
            status.Text = "实时变声中";
            inputStatus.Text = "麦克风输入";
            inputLevel.Progress = sample.Peak;
            debug.Text = sample.Stats;
            if (reports++ % 6 == 0) Log(sample.Stats.Replace("\n", " · "));
        });
        try
        {
            if (!Uri.TryCreate("ws://" + address.Text?.Trim() + "/gpu/" + preset, UriKind.Absolute, out var endpoint)
                || endpoint.Port == 80 || endpoint.AbsolutePath != "/gpu/" + preset)
                throw new InvalidOperationException("请输入电脑的 IP 和端口，例如 192.168.10.163:8767。");
            GetPreferences(Android.Content.FileCreationMode.Private)?.Edit()?.PutString("server", address.Text?.Trim())?.Apply();
            int playbackBlocks = prebuffer, captureBlocks = capacity;
            await Task.Run(() => RvcAudio.RunAsync(endpoint, playbackBlocks, captureBlocks, progress, current.Token));
            status.Text = "已结束";
        }
        catch (Exception error)
        {
            if (!current.IsCancellationRequested) Log(error.ToString());
            status.Text = current.IsCancellationRequested ? "已结束" : $"连接或音频失败：{error.Message}";
        }
        finally
        {
            session = null;
            toggle.Text = "开始变声";
            toggle.Enabled = address.Enabled = voices.Enabled = true;
            inputStatus.Text = "麦克风已停止";
            inputLevel.Progress = 0;
        }
    }
    protected override void OnStop()
    {
        session?.Cancel();
        base.OnStop();
    }

    private TextView Label(string text, int size, Color color)
    {
        var label = new TextView(this) { Text = text, TextSize = size };
        label.SetTextColor(color);
        return label;
    }

    private GradientDrawable Card(Color color, int radius)
    {
        var card = new GradientDrawable();
        card.SetColor(color);
        card.SetCornerRadius(Dp(radius));
        return card;
    }

    private void Add(LinearLayout parent, View child, int gap, int height = -2)
    {
        var layout = new LinearLayout.LayoutParams(-1, height < 0 ? height : Dp(height));
        layout.TopMargin = Dp(gap);
        parent.AddView(child, layout);
    }

    private int Dp(int value) => (int)Math.Round(value * Resources!.DisplayMetrics!.Density);
}





