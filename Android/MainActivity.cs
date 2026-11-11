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
    private static readonly Color Background = Color.ParseColor("#101620");
    private static readonly Color Panel = Color.ParseColor("#1C2635");
    private static readonly Color Accent = Color.ParseColor("#79E6CD");
    private static readonly Color Secondary = Color.ParseColor("#B0BDD7");
    private Button toggle = null!;
    private Spinner voices = null!;
    private TextView status = null!;
    private TextView transcript = null!;
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
        Add(content, Label("普通话实时变声", 15, Secondary), 4);
        Add(content, Label("音色", 14, Secondary), 28);
        voices = new Spinner(this);
        var names = DomesticSpeech.Voices.Select(voice => voice.Name).ToArray();
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
        Add(content, Label("最近识别", 14, Secondary), 26);
        transcript = Label("说话后，识别片段会显示在这里。", 17, Color.White);
        transcript.SetPadding(Dp(16), Dp(14), Dp(16), Dp(14));
        transcript.Background = Card(Panel, 12);
        transcript.SetMinHeight(Dp(82));
        Add(content, transcript, 8);
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
        voices.Enabled = false;
        status.Text = "正在连接…";
        inputStatus.Text = "正在等待麦克风数据…";
        transcript.Text = "";
        var selected = DomesticSpeech.Voices[voices.SelectedItemPosition];
        try
        {
            using var reader = new StreamReader(typeof(MainActivity).Assembly
                .GetManifestResourceStream("VoiceChange.AndroidSpeechConfig")!);
            var text = new Progress<string>(value =>
            {
                if (session == current && !current.IsCancellationRequested)
                    transcript.Text = value;
            });
            var service = new DomesticSpeech(reader.ReadLine()!, reader.ReadLine()!, reader.ReadLine()!, text);
            var level = new Progress<int>(peak =>
            {
                if (session != current || current.IsCancellationRequested)
                    return;
                inputLevel.Progress = Math.Clamp((int)(Math.Sqrt(peak / 32768.0) * 100), 0, 100);
                inputStatus.Text = peak == 0 ? "麦克风正在采集 · 当前输入为静音" : "麦克风正在采集";
            });
            var state = new Progress<string>(value =>
            {
                if (session == current && !current.IsCancellationRequested)
                    status.Text = value;
            });
            await Task.Run(() => new LiveVoice(service).RunAsync(new(level), new(), selected,
                new Progress<string>(), state, current.Token));
            status.Text = "已结束";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { status.Text = "已结束"; }
        catch (OperationCanceledException) { status.Text = "连接超时，请检查网络后重试。"; }
        catch (Exception error) { status.Text = error.Message; }
        finally
        {
            session = null;
            if (!IsDestroyed)
            {
                toggle.Text = "开始变声";
                toggle.Enabled = voices.Enabled = true;
                inputStatus.Text = "麦克风已停止";
                inputLevel.Progress = 0;
            }
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


