using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Text.RegularExpressions;

namespace VoiceChange;

internal sealed class DomesticSpeech : ISpeechService
{
    private readonly Uri recognitionEndpoint;
    private readonly string aliyunKey;
    private readonly MiniMaxSpeech miniMax;
    private readonly IProgress<string>? transcript;

    public static VoicePreset[] Voices { get; } =
    [
        new("温柔女声", "Chinese (Mandarin)_Soft_Girl"),
        new("甜美女声", "Chinese (Mandarin)_Sweet_Lady"),
        new("俏皮童趣", "Chinese (Mandarin)_Cute_Spirit"),
        new("青年男声", "male-qn-qingse"),
        new("暖心闺蜜", "Chinese (Mandarin)_Warm_Bestie"),
        new("清脆少女", "Chinese (Mandarin)_Crisp_Girl"),
        new("温润青年", "Chinese (Mandarin)_Gentle_Youth"),
        new("直爽少年", "Chinese (Mandarin)_Straightforward_Boy"),
        new("电台主播", "Chinese (Mandarin)_Radio_Host"),
        new("慈祥长辈", "Chinese (Mandarin)_Kind-hearted_Elder")
    ];

    public DomesticSpeech(string workspaceId, string aliyunKey, string miniMaxKey, IProgress<string>? transcript = null)
    {
        if (workspaceId.Length != 0 && !Regex.IsMatch(workspaceId, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("请输入百炼北京的业务空间 ID，例如 ws-…，也可以留空。");
        if (string.IsNullOrWhiteSpace(aliyunKey) || string.IsNullOrWhiteSpace(miniMaxKey))
            throw new ArgumentException("请填写阿里云百炼和 MiniMax 两个 API Key。");

        var host = workspaceId.Length == 0 ? "dashscope.aliyuncs.com" : $"{workspaceId}.cn-beijing.maas.aliyuncs.com";
        recognitionEndpoint = new($"wss://{host}/api-ws/v1/inference");
        this.aliyunKey = aliyunKey;
        miniMax = new(miniMaxKey);
        this.transcript = transcript;
    }

    public async Task CheckConnectionAsync(CancellationToken token)
    {
        using (var socket = new ClientWebSocket())
        {
            var id = await StartRecognitionAsync(socket, token).ConfigureAwait(false);
            await SendFinishAsync(socket, id, token).ConfigureAwait(false);
        }
        await SpeakAsync("你好。", Voices[0], (stream, ct) => stream.CopyToAsync(Stream.Null, ct), token).ConfigureAwait(false);
    }

    public Task SpeakAsync(string text, VoicePreset preset, Func<Stream, CancellationToken, Task> play, CancellationToken token) =>
        miniMax.SpeakAsync(text, preset, play, token);

    public async Task RecognizeAsync(AudioDevice input, Action<string> onPhrase, Action ready, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var active = lifetime.Token;
        using var socket = new ClientWebSocket();
        using var capture = WaveAudio.CreateCapture(input);
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        capture.DataAvailable += (_, e) =>
        {
            if (!active.IsCancellationRequested && e.BytesRecorded > 0)
                frames.Writer.TryWrite(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
        };
        capture.RecordingStopped += (_, e) => frames.Writer.TryComplete(
            new InvalidOperationException("麦克风已停止，请检查连接和录音权限。", e.Exception));

        active.ThrowIfCancellationRequested();
        capture.StartRecording();
        try
        {
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(active))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(15));
                await StartRecognitionAsync(socket, startup.Token).ConfigureAwait(false);
            }
            ready();
            var send = SendAudioAsync(socket, frames.Reader, active);
            var receive = ReceiveResultsAsync(socket, onPhrase, active);
            var completed = await Task.WhenAny(send, receive).ConfigureAwait(false);
            try
            {
                await completed.ConfigureAwait(false);
            }
            finally
            {
                lifetime.Cancel();
                socket.Abort();
                try { await (completed == send ? receive : send).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
        finally
        {
            lifetime.Cancel();
            capture.StopRecording();
        }
    }

    private async Task<string> StartRecognitionAsync(ClientWebSocket socket, CancellationToken token)
    {
        await SpeechSocket.ConnectAsync(socket, recognitionEndpoint, aliyunKey, "阿里云百炼", token).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        await SpeechSocket.SendAsync(socket, new JsonObject
        {
            ["header"] = TaskHeader("run-task", id),
            ["payload"] = new JsonObject
            {
                ["task_group"] = "audio",
                ["task"] = "asr",
                ["function"] = "recognition",
                ["model"] = "paraformer-realtime-v2",
                ["parameters"] = new JsonObject
                {
                    ["format"] = "pcm",
                    ["sample_rate"] = WaveAudio.InputRate,
                    ["language_hints"] = new JsonArray("zh"),
                    ["max_sentence_silence"] = 250,
                    ["multi_threshold_mode_enabled"] = true,
                    ["heartbeat"] = true
                },
                ["input"] = new JsonObject()
            }
        }, token).ConfigureAwait(false);
        using var response = await SpeechSocket.ReceiveAsync(socket, "阿里云百炼", token).ConfigureAwait(false);
        if (ReadEvent(response.RootElement) != "task-started")
            throw new InvalidOperationException("阿里云百炼未确认识别任务，请核对北京地域和业务空间。");
        return id;
    }

    private static Task SendFinishAsync(ClientWebSocket socket, string id, CancellationToken token) =>
        SpeechSocket.SendAsync(socket, new JsonObject
        {
            ["header"] = TaskHeader("finish-task", id),
            ["payload"] = new JsonObject { ["input"] = new JsonObject() }
        }, token);

    private static JsonObject TaskHeader(string action, string id) =>
        new() { ["action"] = action, ["task_id"] = id, ["streaming"] = "duplex" };

    private static async Task SendAudioAsync(ClientWebSocket socket, ChannelReader<byte[]> frames, CancellationToken token)
    {
        await foreach (var frame in frames.ReadAllAsync(token).ConfigureAwait(false))
            await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
    }

    private async Task ReceiveResultsAsync(ClientWebSocket socket, Action<string> onPhrase, CancellationToken token)
    {
        var pending = new StreamingPhrase();
        while (true)
        {
            using var message = await SpeechSocket.ReceiveAsync(socket, "阿里云百炼", token).ConfigureAwait(false);
            var root = message.RootElement;
            var kind = ReadEvent(root);
            if (kind == "task-finished")
                throw new InvalidOperationException("阿里云已结束识别会话，请重新点击开始。");
            if (kind != "result-generated")
                continue;

            var sentence = root.GetProperty("payload").GetProperty("output").GetProperty("sentence");
            if (sentence.TryGetProperty("heartbeat", out var heartbeat) && heartbeat.ValueKind == JsonValueKind.True)
                continue;

            var final = sentence.TryGetProperty("sentence_end", out var end) && end.ValueKind == JsonValueKind.True;
            var text = sentence.TryGetProperty("text", out var result) ? result.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(text))
                transcript?.Report(text);
            if (pending.Update(text, final) is { } fragment)
                onPhrase(fragment);
        }
    }

    private static string? ReadEvent(JsonElement root)
    {
        var header = root.GetProperty("header");
        var kind = header.GetProperty("event").GetString();
        if (kind == "task-failed")
        {
            var code = header.TryGetProperty("error_code", out var error) ? error.ToString() : "未知";
            throw new InvalidOperationException($"阿里云识别失败（{code}）。请检查北京地域、模型权限和配额。");
        }
        return kind;
    }
}
