using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceChange;

internal sealed class MiniMaxSpeech(string key)
{
    private static readonly Uri Endpoint = new("wss://api.minimax.cn/ws/v1/t2a_v2");

    public async Task SpeakAsync(string text, VoicePreset preset, Func<Stream, CancellationToken, Task> play, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        await SpeechSocket.ConnectAsync(socket, Endpoint, key, "MiniMax", token).ConfigureAwait(false);
        await ExpectAsync(socket, "connected_success", token).ConfigureAwait(false);
        await SpeechSocket.SendAsync(socket, new JsonObject
        {
            ["event"] = "task_start",
            ["model"] = "speech-2.8-turbo",
            ["language_boost"] = "Chinese",
            ["voice_setting"] = new JsonObject { ["voice_id"] = preset.Voice },
            ["audio_setting"] = new JsonObject { ["sample_rate"] = VoicePreset.SampleRate, ["format"] = "pcm", ["channel"] = 1 }
        }, token).ConfigureAwait(false);
        await ExpectAsync(socket, "task_started", token).ConfigureAwait(false);
        await SpeechSocket.SendAsync(socket, new JsonObject { ["event"] = "task_continue", ["text"] = text }, token).ConfigureAwait(false);
        await using var chunks = ReadAudioAsync(socket, token).GetAsyncEnumerator();
        using var pcm = new PcmStream(chunks);
        await play(pcm, token).ConfigureAwait(false);
        await SpeechSocket.SendAsync(socket, new JsonObject { ["event"] = "task_finish" }, token).ConfigureAwait(false);
    }

    private static async Task ExpectAsync(ClientWebSocket socket, string expected, CancellationToken token)
    {
        using var message = await SpeechSocket.ReceiveAsync(socket, "MiniMax", token).ConfigureAwait(false);
        if (ReadEvent(message.RootElement) != expected)
            throw new InvalidOperationException("MiniMax 未确认合成任务，请检查密钥和账户权限。");
    }

    private static async IAsyncEnumerable<byte[]> ReadAudioAsync(ClientWebSocket socket, [EnumeratorCancellation] CancellationToken token)
    {
        var received = false;
        while (true)
        {
            using var message = await SpeechSocket.ReceiveAsync(socket, "MiniMax", token).ConfigureAwait(false);
            var root = message.RootElement;
            ReadEvent(root);
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("audio", out var audio) && audio.GetString() is { Length: > 0 } hex)
            {
                received = true;
                yield return Convert.FromHexString(hex);
            }
            if (root.TryGetProperty("is_final", out var final) && final.ValueKind == JsonValueKind.True)
                break;
        }
        if (!received)
            throw new InvalidOperationException("MiniMax 未返回音频，请检查音色和账户余额。");
    }

    private static string? ReadEvent(JsonElement root)
    {
        var kind = root.TryGetProperty("event", out var name) ? name.GetString() : null;
        var code = root.TryGetProperty("base_resp", out var status) && status.TryGetProperty("status_code", out var value)
            ? value.GetInt32() : 0;
        if (kind == "task_failed" || code != 0)
            throw new InvalidOperationException($"MiniMax 合成失败（{code}）。请检查 API Key、账户余额和音色权限。");
        return kind;
    }

    private sealed class PcmStream(IAsyncEnumerator<byte[]> chunks) : Stream
    {
        private ReadOnlyMemory<byte> available;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
                return 0;
            cancellationToken.ThrowIfCancellationRequested();
            if (available.IsEmpty)
            {
                if (!await chunks.MoveNextAsync().ConfigureAwait(false))
                    return 0;
                available = chunks.Current;
            }
            var length = Math.Min(buffer.Length, available.Length);
            available[..length].CopyTo(buffer);
            available = available[length..];
            return length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
