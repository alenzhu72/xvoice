using System.Net.WebSockets;
using System.Diagnostics;
using VoiceChange;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ServerForm());
    }

    internal static WebApplication CreateServer(string url, int adapter, string models, int pitch, float gain, int maxClients)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        
        var app = builder.Build();
        var slot = new SemaphoreSlim(maxClients, maxClients);
        app.UseWebSockets();
        app.MapGet("/health", () => Results.Ok(new { service = "XVoice GPU", adapter, blockMs = 160, maxClients, activeClients = maxClients - slot.CurrentCount }));
        app.Map("/gpu/{voice}", async (HttpContext context, string voice) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();
            if (voice is not ("guanguanV1" or "kikiV1" or "keruanV1"))
                return Results.NotFound();
            int sessionPitch = pitch;
            float sessionGain = gain;
            if (context.Request.Query.TryGetValue("pitch", out var shift))
            {
                if (!int.TryParse(shift, out sessionPitch) || sessionPitch is < -12 or > 12)
                    return Results.BadRequest("Pitch must be between -12 and 12.");
            }
            if (context.Request.Query.TryGetValue("volume", out var volume))
            {
                if (!int.TryParse(volume, out int percent) || percent is < 0 or > 150)
                    return Results.BadRequest("Volume must be between 0 and 150.");
                sessionGain = percent / 100f;
            }

            if (!slot.Wait(0))
                return Results.StatusCode(503);
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var token = context.RequestAborted;
                var input = new byte[5120];
                try
                {
                    using var converter = new CpuConverter(models, voice + ".onnx", adapter);
                    converter.Convert(input, sessionPitch, sessionGain);
                    await socket.SendAsync("ready"u8.ToArray(), WebSocketMessageType.Text, true, token);
                    int blocks = 0;
                    while (!token.IsCancellationRequested)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                        idle.CancelAfter(TimeSpan.FromSeconds(20));
                        int offset = 0;
                        while (true)
                        {
                            var part = await socket.ReceiveAsync(input.AsMemory(offset), idle.Token);
                            if (part.MessageType == WebSocketMessageType.Close)
                            {
                                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", idle.Token);
                                return Results.Empty;
                            }
                            offset += part.Count;
                            if (part.MessageType != WebSocketMessageType.Binary ||
                                (part.EndOfMessage ? offset != input.Length : offset == input.Length))
                            {
                                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Expected 5120 bytes of PCM", idle.Token);
                                return Results.Empty;
                            }
                            if (part.EndOfMessage) break;
                        }
                        long started = Stopwatch.GetTimestamp();
                        var output = converter.Convert(input, sessionPitch, sessionGain);
                        await socket.SendAsync(output.AsMemory(), WebSocketMessageType.Binary, true, idle.Token);
                        if (blocks++ % 50 == 0)
                            app.Logger.LogInformation("{Voice} GPU {Adapter}: {Ms:F1} ms", voice, adapter, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException)
                {
                    app.Logger.LogDebug("Client disconnected: {Reason}", error.Message);
                }
                catch (Exception error)
                {
                    app.Logger.LogError(error, "RVC failed on GPU {Adapter}", adapter);
                    if (socket.State == WebSocketState.Open)
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "GPU inference failed; see server console", deadline.Token);
                    }
                }
            }
            finally { slot.Release(); }
            return Results.Empty;
        });
        
        return app;
    }
}


