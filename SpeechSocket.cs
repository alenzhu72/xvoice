using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceChange;

internal static class SpeechSocket
{
    public static async Task ConnectAsync(ClientWebSocket socket, Uri endpoint, string key, string service, CancellationToken token)
    {
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        try
        {
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
        }
        catch (WebSocketException error) when (!token.IsCancellationRequested)
        {
            var code = (int)socket.HttpStatusCode;
            var hint = code is 401 or 403 ? $"（HTTP {code}），请检查 API Key 和访问权限。" : "，请检查网络、代理和服务地址。";
            throw new InvalidOperationException($"无法连接{service}{hint}", error);
        }
    }

    public static Task SendAsync(ClientWebSocket socket, JsonObject message, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message.ToJsonString())), WebSocketMessageType.Text, true, token);

    public static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, string service, CancellationToken token)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException($"{service}连接关闭或返回了无效消息。");
            message.Write(buffer, 0, result.Count);
            if (message.Length > 2 * 1024 * 1024)
                throw new InvalidOperationException($"{service}返回的消息过大。");
        }
        while (!result.EndOfMessage);
        return JsonDocument.Parse(message.ToArray());
    }
}
