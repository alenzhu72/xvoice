using Android.Media;
using System.Net.WebSockets;

using System.Threading.Channels;

namespace VoiceChange;

internal static class RvcAudio
{
    private const int Rate = 16000;
    private const int Bytes = GpuConnection.InputBytes;

    public static async Task RunAsync(Uri endpoint, int prebuffer, int capacity, IProgress<(int Peak, string Stats)> meter, CancellationToken token)
    {
        using var connection = await GpuConnection.ConnectAsync(endpoint, token);
        int inputSize = AudioRecord.GetMinBufferSize(Rate, ChannelIn.Mono, Encoding.Pcm16bit);
        int outputSize = AudioTrack.GetMinBufferSize(GpuConnection.OutputRate, ChannelOut.Mono, Encoding.Pcm16bit);
        if (inputSize <= 0 || outputSize <= 0)
            throw new InvalidOperationException("设备不支持 16 kHz 音频。");

        using var record = new AudioRecord(AudioSource.Mic, Rate, ChannelIn.Mono, Encoding.Pcm16bit,
            Math.Max(inputSize, Bytes * 2));
        using var track = new AudioTrack(Android.Media.Stream.Music, GpuConnection.OutputRate, ChannelConfiguration.Mono, Encoding.Pcm16bit,
            Math.Max(outputSize, GpuConnection.OutputBytes * (prebuffer + 1)), AudioTrackMode.Stream);
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(token);
        int dropped = 0;
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true
        });
        Task? reader = null;
        try
        {
            if (record.State != State.Initialized || track.State != AudioTrackState.Initialized)
                throw new InvalidOperationException("麦克风或播放设备初始化失败。");
            record.StartRecording();
            if (record.RecordingState != RecordState.Recording)
                throw new InvalidOperationException("无法启动麦克风。");
            int buffered = 0, blocks = 0;
            double total = 0, maximum = 0;
            reader = Task.Run(() => Capture(record, queue.Writer, capture.Token));
            var playback = new byte[GpuConnection.OutputBytes];
            await foreach (var block in queue.Reader.ReadAllAsync(token))
            {
                double elapsed = await connection.ConvertAsync(block, playback, token);
                total += elapsed; maximum = Math.Max(maximum, elapsed); blocks++;
                for (int played = 0; played < playback.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    var written = track.Write(playback, played, playback.Length - played, WriteMode.NonBlocking);
                    if (written < 0)
                        throw new InvalidOperationException("播放变声音频失败。");
                    played += written;
                    if (written == 0)
                    {
                        if (track.PlayState != PlayState.Playing) track.Play();
                        await Task.Delay(5, token);
                    }
                }
                if (++buffered == prebuffer && track.PlayState != PlayState.Playing) track.Play();
                int peak = 0;
                for (int i = 0; i < Bytes; i += 2)
                    peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(block, i)));
                var underruns = OperatingSystem.IsAndroidVersionAtLeast(24) ? track.UnderrunCount.ToString() : "不可用";
                meter.Report((Math.Min(100, peak * 400 / 32768), $"音频块 {blocks} · 往返 {elapsed:F0} ms\n平均 {total / blocks:F0} ms · 最大 {maximum:F0} ms\n录音丢块 {Volatile.Read(ref dropped)} · 播放欠载 {underruns}"));
            }
        }
        finally
        {
            capture.Cancel();
            try
            {
                if (record.RecordingState == RecordState.Recording)
                    record.Stop();
                if (reader is not null)
                    await reader;
            }
            finally
            {
                record.Release();
                track.Release();
            }
        }
    }

    private static void Capture(AudioRecord record, ChannelWriter<byte[]> writer, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var block = new byte[Bytes];
                for (int offset = 0; offset < Bytes;)
                {
                    if (token.IsCancellationRequested)
                        return;
                    int count = record.Read(block, offset, Bytes - offset);
                    if (count <= 0)
                        throw new InvalidOperationException($"麦克风读取失败（{count}）。");
                    offset += count;
                }
                writer.TryWrite(block);
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested) { failure = error; }
        catch (Exception) when (token.IsCancellationRequested) { }
        finally { writer.TryComplete(failure); }
    }
}

