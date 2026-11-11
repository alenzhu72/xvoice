using Android.Media;
using System.Diagnostics;

namespace VoiceChange;

internal sealed record AudioDevice(IProgress<int>? Level = null);

internal static class WaveAudio
{
    public const int InputRate = 16000;
    public static Capture CreateCapture(AudioDevice device) => new(device.Level);

    internal sealed record Frame(byte[] Buffer, int BytesRecorded);
    internal sealed record CaptureEnd(Exception? Exception);

    internal sealed class Capture : IDisposable
    {
        private const int FrameBytes = InputRate * 2 * 30 / 1000;
        private readonly AudioRecord record;
        private readonly IProgress<int>? level;
        private Thread? worker;
        private volatile bool running;
        public event EventHandler<Frame>? DataAvailable;
        public event EventHandler<CaptureEnd>? RecordingStopped;

        public Capture(IProgress<int>? level)
        {
            this.level = level;
            var size = AudioRecord.GetMinBufferSize(InputRate, ChannelIn.Mono, Encoding.Pcm16bit);
            if (size <= 0)
                throw new InvalidOperationException("设备无法打开 16 kHz 麦克风。");
            record = new AudioRecord(AudioSource.Mic, InputRate, ChannelIn.Mono,
                Encoding.Pcm16bit, Math.Max(size, FrameBytes * 4));
            if (record.State != State.Initialized)
            {
                record.Release();
                record.Dispose();
                throw new InvalidOperationException("无法初始化麦克风，请检查录音权限。");
            }
        }

        public void StartRecording()
        {
            record.StartRecording();
            if (record.RecordingState != RecordState.Recording)
                throw new InvalidOperationException("无法开始录音。");
            running = true;
            worker = new Thread(ReadFrames) { IsBackground = true };
            worker.Start();
        }

        private void ReadFrames()
        {
            Exception? failure = null;
            try
            {
                var bytes = new byte[FrameBytes];
                var samples = new short[FrameBytes / 2];
                var interval = Stopwatch.StartNew();
                var peak = 0;
                while (running)
                {
                    var count = record.Read(samples, 0, samples.Length);
                    if (!running)
                        break;
                    if (count < 0)
                        throw new InvalidOperationException($"麦克风读取失败（{count}）。");
                    if (count == 0)
                        continue;
                    var byteCount = count * sizeof(short);
                    Buffer.BlockCopy(samples, 0, bytes, 0, byteCount);
                    DataAvailable?.Invoke(this, new Frame(bytes, byteCount));
                    for (var i = 0; i < count; i++)
                        peak = Math.Max(peak, Math.Abs((int)samples[i]));
                    if (interval.ElapsedMilliseconds >= 120)
                    {
                        level?.Report(peak);
                        peak = 0;
                        interval.Restart();
                    }
                }
            }
            catch (Exception error) { failure = error; }
            finally { RecordingStopped?.Invoke(this, new CaptureEnd(failure)); }
        }

        public void StopRecording()
        {
            if (worker is null)
                return;
            running = false;
            record.Stop();
            worker.Join();
            worker = null;
        }

        public void Dispose()
        {
            try { StopRecording(); }
            finally
            {
                record.Release();
                record.Dispose();
            }
        }
    }

    internal sealed class StreamingOutput : IDisposable
    {
        private readonly AudioTrack track;

        public StreamingOutput(AudioDevice device)
        {
            var size = AudioTrack.GetMinBufferSize(VoicePreset.SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
            if (size <= 0)
                throw new InvalidOperationException("设备无法打开语音播放。");
            using var formatBuilder = new AudioFormat.Builder();
            using var format = formatBuilder.SetSampleRate(VoicePreset.SampleRate)!
                .SetEncoding(Encoding.Pcm16bit)!.SetChannelMask(ChannelOut.Mono)!.Build();
            using var attributesBuilder = new AudioAttributes.Builder();
            using var attributes = attributesBuilder.SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Speech)!.Build();
            using var builder = new AudioTrack.Builder();
            track = builder.SetAudioFormat(format!)!.SetAudioAttributes(attributes!)!
                .SetBufferSizeInBytes(size)!.SetTransferMode(AudioTrackMode.Stream)!.Build()!;
            if (track.State != AudioTrackState.Initialized)
            {
                track.Release();
                track.Dispose();
                throw new InvalidOperationException("无法初始化语音播放。");
            }
        }

        public async Task WriteAsync(System.IO.Stream pcm, CancellationToken token)
        {
            var buffer = new byte[VoicePreset.SampleRate * 2 / 25];
            while (true)
            {
                var count = await pcm.ReadAtLeastAsync(buffer, buffer.Length, false, token).ConfigureAwait(false);
                if (count == 0)
                    return;
                if (count % 2 != 0)
                    throw new InvalidOperationException("合成音频格式无效。");
                if (track.PlayState != PlayState.Playing)
                    track.Play();
                for (var offset = 0; offset < count;)
                {
                    token.ThrowIfCancellationRequested();
                    var written = track.Write(buffer, offset, count - offset);
                    if (written <= 0)
                        throw new InvalidOperationException($"语音播放失败（{written}）。");
                    offset += written;
                }
            }
        }

        public void Dispose()
        {
            if (track.PlayState == PlayState.Playing)
            {
                track.Pause();
                track.Flush();
            }
            track.Release();
            track.Dispose();
        }
    }
}
