using System.Diagnostics;
using System.Net.WebSockets;

namespace VoiceChange;

internal sealed class GpuConnection : IDisposable
{
    internal const int BlockMs = 160, InputBytes = 5120, OutputBytes = 7680, OutputRate = 24000;
    private readonly ClientWebSocket socket = new();

    internal static async Task<GpuConnection> ConnectAsync(Uri uri, CancellationToken token)
    {
        var connection = new GpuConnection();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            await connection.socket.ConnectAsync(uri, deadline.Token);
            var ready = new byte[5];
            await connection.ReceiveAsync(ready, WebSocketMessageType.Text, deadline.Token);
            if (!ready.AsSpan().SequenceEqual("ready"u8))
                throw new IOException("服务器未就绪。");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal async Task<double> ConvertAsync(byte[] input, byte[] output, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        long start = Stopwatch.GetTimestamp();
        await socket.SendAsync(input.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
        await ReceiveAsync(output, WebSocketMessageType.Binary, deadline.Token);
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private async Task ReceiveAsync(byte[] buffer, WebSocketMessageType expected, CancellationToken token)
    {
        int offset = 0;
        while (true)
        {
            var message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, buffer.Length - offset), token);
            if (message.MessageType != expected)
                throw new IOException(message.CloseStatusDescription ?? "服务器返回格式不匹配。");
            offset += message.Count;
            if (message.EndOfMessage && offset == buffer.Length)
                return;
            if (message.EndOfMessage || offset == buffer.Length)
                throw new IOException("服务器返回的音频长度不匹配。");
        }
    }

    public void Dispose() => socket.Dispose();
}
