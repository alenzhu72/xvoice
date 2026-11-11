using System.Threading.Channels;

namespace VoiceChange;

internal interface ISpeechService
{
    Task CheckConnectionAsync(CancellationToken token);
    Task RecognizeAsync(AudioDevice input, Action<string> onPhrase, Action ready, CancellationToken token);
    Task SpeakAsync(string text, VoicePreset preset, Func<Stream, CancellationToken, Task> play, CancellationToken token);
}

internal sealed class LiveVoice(ISpeechService speech)
{
    public async Task RunAsync(AudioDevice input, AudioDevice output, VoicePreset voice,
        IProgress<string> recognizedText, IProgress<string> status, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var active = lifetime.Token;
        var phrases = Channel.CreateBounded<string>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        }, _ => status.Report("实时变声中 · 语音积压，已跳过较早的片段。"));

        using var player = new WaveAudio.StreamingOutput(output);
        status.Report("正在采集 · 正在连接实时识别…");
        var recognition = speech.RecognizeAsync(input, text =>
        {
            if (active.IsCancellationRequested)
                return;
            recognizedText.Report(text);
            phrases.Writer.TryWrite(text);
        }, () => status.Report($"实时变声中 · {voice.Name} · {(
            speech is DomesticSpeech ? "边说边分段播放" : "说完短句后自动播放")}"), active);
        var speaking = ForwardSpeechAsync(phrases.Reader, player, voice, active);
        var completed = await Task.WhenAny(recognition, speaking).ConfigureAwait(false);
        try
        {
            await completed.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            // Observe the other worker during shutdown without replacing the initial error.
            try { await (completed == recognition ? speaking : recognition).ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private async Task ForwardSpeechAsync(ChannelReader<string> phrases, WaveAudio.StreamingOutput player,
        VoicePreset voice, CancellationToken token)
    {
        await foreach (var text in phrases.ReadAllAsync(token).ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await speech.SpeakAsync(text, voice, player.WriteAsync, timeout.Token).ConfigureAwait(false);
        }
    }
}
