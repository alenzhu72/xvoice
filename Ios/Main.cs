using AVFoundation;
using Foundation;
using UIKit;

namespace VoiceChange;

internal static class Program
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
}

[Register("SceneDelegate")]
public sealed class SceneDelegate : UIWindowSceneDelegate
{
    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is UIWindowScene windowScene)
        {
            Window = new UIWindow(windowScene) { RootViewController = new MainController() };
            Window.MakeKeyAndVisible();
        }
    }

    public override void DidEnterBackground(UIScene scene) =>
        (Window?.RootViewController as MainController)?.Stop();
}

internal sealed class MainController : UIViewController
{
    private readonly UIColor panel = UIColor.FromRGB(28, 38, 53);
    private readonly UIColor accent = UIColor.FromRGB(121, 230, 205);
    private readonly UILabel status = new() { Text = "就绪", Lines = 0 };
    private readonly UILabel transcript = new() { Lines = 0, Text = "说话后，识别片段会显示在这里。" };
    private readonly UIButton voices = new(UIButtonType.System);
    private readonly UIButton toggle = new(UIButtonType.System);
    private VoicePreset selected = DomesticSpeech.Voices[0];
    private CancellationTokenSource? session;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        var view = View!;
        view.BackgroundColor = UIColor.FromRGB(16, 22, 32);
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false };
        var stack = new UIStackView { Axis = UILayoutConstraintAxis.Vertical, Spacing = 16, TranslatesAutoresizingMaskIntoConstraints = false };
        view.AddSubview(scroll);
        scroll.AddSubview(stack);
        var safe = view.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints([
            scroll.TopAnchor.ConstraintEqualTo(safe.TopAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(safe.BottomAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor),
            stack.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor, 24),
            stack.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor, -24),
            stack.LeadingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.LeadingAnchor, 24),
            stack.TrailingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TrailingAnchor, -24),
            stack.WidthAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.WidthAnchor, -48)
        ]);
        var logo = new UIImageView(UIImage.FromBundle("voice_logo.png")) { ContentMode = UIViewContentMode.ScaleAspectFit };
        stack.AddArrangedSubview(logo);
        logo.HeightAnchor.ConstraintEqualTo(76).Active = true;
        stack.AddArrangedSubview(Label("声线工坊", 32));
        stack.AddArrangedSubview(Label("普通话实时变声", 16));
        stack.AddArrangedSubview(Label("音色", 15));
        Configure(voices, selected.Name, panel, UIColor.White);
        voices.TouchUpInside += (_, _) => PickVoice();
        stack.AddArrangedSubview(voices);
        Configure(toggle, "开始变声", accent, UIColor.Black);
        toggle.TouchUpInside += async (_, _) => await ToggleAsync();
        stack.AddArrangedSubview(toggle);
        status.TextColor = accent;
        stack.AddArrangedSubview(status);
        stack.AddArrangedSubview(Label("最近识别", 15));
        transcript.TextColor = UIColor.White;
        stack.AddArrangedSubview(transcript);
    }

    private void PickVoice()
    {
        var sheet = UIAlertController.Create("选择音色", null, UIAlertControllerStyle.ActionSheet);
        foreach (var voice in DomesticSpeech.Voices)
        {
            sheet.AddAction(UIAlertAction.Create(voice.Name, UIAlertActionStyle.Default, _ =>
            {
                selected = voice;
                voices.SetTitle(voice.Name, UIControlState.Normal);
            }));
        }
        sheet.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));
        PresentViewController(sheet, true, null);
    }

    public void Stop() => session?.Cancel();

    private async Task ToggleAsync()
    {
        if (session is not null)
        {
            Stop();
            toggle.Enabled = false;
            return;
        }

        using var current = new CancellationTokenSource();
        session = current;
        toggle.Enabled = voices.Enabled = false;
        var audio = AVAudioSession.SharedInstance();
        var activated = false;
        try
        {
            if (!await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Audio))
            {
                status.Text = "请在系统设置中允许麦克风访问。";
                return;
            }
            current.Token.ThrowIfCancellationRequested();
            var categoryError = audio.SetCategory(AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.DefaultToSpeaker);
            if (categoryError is not null)
                throw new InvalidOperationException(categoryError.LocalizedDescription);
            if (!audio.SetActive(true, out var error))
                throw new InvalidOperationException(error?.LocalizedDescription ?? "无法启用录音和播放。");
            activated = true;
            using var engine = new AVAudioEngine();
            using var reader = new StreamReader(typeof(MainController).Assembly.GetManifestResourceStream("VoiceChange.IosSpeechConfig")!);
            var preview = new Progress<string>(value =>
            {
                if (session == current && !current.IsCancellationRequested)
                    transcript.Text = value;
            });
            var state = new Progress<string>(value =>
            {
                if (session == current && !current.IsCancellationRequested)
                    status.Text = value;
            });
            var service = new DomesticSpeech(reader.ReadLine()!, reader.ReadLine()!, reader.ReadLine()!, preview);
            var device = new AudioDevice(engine);
            var voice = selected;
            toggle.SetTitle("结束变声", UIControlState.Normal);
            toggle.Enabled = true;
            transcript.Text = "";
            await Task.Run(() => new LiveVoice(service).RunAsync(device, device, voice, new Progress<string>(), state, current.Token));
            status.Text = "已结束";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { status.Text = "已结束"; }
        catch (OperationCanceledException) { status.Text = "连接超时，请检查网络后重试。"; }
        catch (Exception error) { status.Text = error.Message; }
        finally
        {
            if (activated)
                audio.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
            session = null;
            toggle.SetTitle("开始变声", UIControlState.Normal);
            toggle.Enabled = voices.Enabled = true;
        }
    }

    private static UILabel Label(string text, int size) =>
        new() { Text = text, Font = UIFont.SystemFontOfSize(size), TextColor = UIColor.White, Lines = 0 };

    private static void Configure(UIButton button, string title, UIColor background, UIColor text)
    {
        button.SetTitle(title, UIControlState.Normal);
        button.SetTitleColor(text, UIControlState.Normal);
        button.BackgroundColor = background;
        button.Layer.CornerRadius = 12;
        button.HeightAnchor.ConstraintEqualTo(52).Active = true;
    }
}
