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
    private static readonly PitchPreset[] Presets =
    [
        new("原声", 0, 0),
        new("明亮", 3, 0.22),
        new("轻柔高音", 6, 0.27),
        new("童趣高音", 9, 0.32),
        new("沉稳低音", -4, -0.18),
        new("深沉低音", -7, -0.24)
    ];
    private static readonly Color Background = Color.ParseColor("#101620");
    private static readonly Color Panel = Color.ParseColor("#1C2635");
    private static readonly Color Accent = Color.ParseColor("#79E6CD");
    private static readonly Color Secondary = Color.ParseColor("#B0BDD7");
    private Button toggle = null!;
    private Spinner voices = null!;
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
        Add(content, Label("离线实时变声", 15, Secondary), 4);
        Add(content, Label("音色", 14, Secondary), 28);
        voices = new Spinner(this);
        var names = Presets.Select(voice => voice.Name).ToArray();
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
        Add(content, Label("建议戴耳机试听，避免扬声器声音回到麦克风。", 14, Secondary), 24);
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
        status.Text = "正在启动麦克风…";
        var preset = Presets[Math.Max(0, voices.SelectedItemPosition)];
        var progress = new Progress<int>(peak =>
        {
            if (session != current || current.IsCancellationRequested)
                return;
            status.Text = "实时变声中";
            inputStatus.Text = "麦克风输入";
            inputLevel.Progress = peak;
        });
        try
        {
            await Task.Run(() => LocalAudio.Run(preset, progress, current.Token));
            status.Text = "已结束";
        }
        catch (Exception error)
        {
            status.Text = current.IsCancellationRequested ? "已结束" : error.Message;
        }
        finally
        {
            session = null;
            toggle.Text = "开始变声";
            toggle.Enabled = voices.Enabled = true;
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



