using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace VoiceChange;

internal sealed partial class AzureSpeech
{
    public async Task RecognizeAsync(AudioDevice input, Action<string> onPhrase, Action readyToListen, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var active = lifetime.Token;
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = CreateRecognitionConfig();
        using var format = AudioStreamFormat.GetWaveFormatPCM(WaveAudio.InputRate, 16, 1);
        using var stream = AudioInputStream.CreatePushStream(format);
        using var audio = AudioConfig.FromStreamInput(stream);
        using var recognizer = new SpeechRecognizer(config, audio);
        using var capture = WaveAudio.CreateCapture(input);
        var captureLock = new object();

        capture.DataAvailable += (_, e) =>
        {
            lock (captureLock)
            {
                if (active.IsCancellationRequested || e.BytesRecorded == 0)
                    return;
                try
                {
                    stream.Write(e.Buffer, e.BytesRecorded);
                }
                catch (Exception error)
                {
                    failed.TrySetResult(error);
                }
            }
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (!active.IsCancellationRequested)
                failed.TrySetResult(new InvalidOperationException("麦克风已停止，请检查设备连接及录音权限。", e.Exception));
        };
        recognizer.Recognized += (_, e) =>
        {
            if (active.IsCancellationRequested || e.Result.Reason != ResultReason.RecognizedSpeech ||
                string.IsNullOrWhiteSpace(e.Result.Text))
                return;
            onPhrase(e.Result.Text);
        };
        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error && !active.IsCancellationRequested)
            {
                var hint = e.ErrorCode == CancellationErrorCode.AuthenticationFailure
                    ? (SpeechSettings.IsCustomerBuild ? "请联系提供方检查语音服务配置。" : "请检查设置中的 Azure 区域和密钥。")
                    : "请检查网络、Azure 配额或资源的访问权限。";
                failed.TrySetResult(new InvalidOperationException($"实时识别中断（{e.ErrorCode}）。{hint}"));
            }
        };
        recognizer.SessionStopped += (_, _) =>
        {
            if (!active.IsCancellationRequested)
                failed.TrySetResult(new InvalidOperationException("Azure 已结束识别会话，请重新点击开始。"));
        };

        var startedCapture = false;
        var startedRecognition = false;
        try
        {
            active.ThrowIfCancellationRequested();
            capture.StartRecording();
            startedCapture = true;

            var starting = recognizer.StartContinuousRecognitionAsync();
            startedRecognition = true;
            var ready = await Task.WhenAny(starting, failed.Task)
                .WaitAsync(TimeSpan.FromSeconds(15), active).ConfigureAwait(false);
            if (ready == failed.Task)
                throw await failed.Task.ConfigureAwait(false);
            await starting.ConfigureAwait(false);
            readyToListen();
            throw await failed.Task.WaitAsync(active).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                if (startedCapture)
                    capture.StopRecording();
            }
            finally
            {
                lock (captureLock)
                    stream.Close();
                if (startedRecognition)
                {
                    try
                    {
                        await recognizer.StopContinuousRecognitionAsync()
                            .WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                    }
                    catch (Exception) { /* Native recognizer is disposed below. */ }
                }
            }
        }
    }
}
