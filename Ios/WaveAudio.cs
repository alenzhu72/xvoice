using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AVFoundation;

namespace VoiceChange;

internal sealed record AudioDevice(AVAudioEngine Engine);

internal static class WaveAudio
{
    public const int InputRate = 16000;
    public static Capture CreateCapture(AudioDevice device) => new(device.Engine);
    internal sealed record Frame(byte[] Buffer, int BytesRecorded);
    internal sealed record CaptureEnd(Exception? Exception);

    internal sealed class Capture(AVAudioEngine engine) : IDisposable
    {
        private AVAudioInputNode? input;
        private float[] samples = [];
        private readonly byte[] pcm = new byte[InputRate * 2 * 30 / 1000];
        private int offset;
        private double phase;
        private double sum;
        private int count;
        private bool failed;
        public event EventHandler<Frame>? DataAvailable;
        public event EventHandler<CaptureEnd>? RecordingStopped;

        public void StartRecording()
        {
            var node = engine.InputNode;
            var format = node.GetBusOutputFormat(0);
            if (format.SampleRate < InputRate || format.ChannelCount == 0)
                throw new InvalidOperationException("当前麦克风格式无法用于实时识别。");
            node.InstallTapOnBus(0, 1024, null, OnBuffer);
            input = node;
            if (!engine.StartAndReturnError(out var error))
                throw new InvalidOperationException(error?.LocalizedDescription ?? "无法启动麦克风。");
        }

        private void OnBuffer(AVAudioPcmBuffer buffer, AVAudioTime time)
        {
            if (failed)
                return;
            try
            {
                var frames = (int)buffer.FrameLength;
                if (frames == 0)
                    return;
                if (samples.Length < frames)
                    samples = new float[frames];
                if (buffer.FloatChannelData == IntPtr.Zero)
                    throw new InvalidOperationException("麦克风返回了不支持的音频格式。");
                Marshal.Copy(Marshal.ReadIntPtr(buffer.FloatChannelData), samples, 0, frames);
                var rate = buffer.Format.SampleRate;
                for (var index = 0; index < frames; index++)
                {
                    sum += samples[index];
                    count++;
                    phase += InputRate;
                    if (phase < rate)
                        continue;
                    phase -= rate;
                    var value = (short)Math.Clamp(sum / count * short.MaxValue, short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), value);
                    sum = 0;
                    count = 0;
                    offset += sizeof(short);
                    if (offset == pcm.Length)
                    {
                        DataAvailable?.Invoke(this, new Frame(pcm, offset));
                        offset = 0;
                    }
                }
            }
            catch (Exception error)
            {
                failed = true;
                RecordingStopped?.Invoke(this, new CaptureEnd(error));
            }
        }

        public void StopRecording()
        {
            input?.RemoveTapOnBus(0);
            input = null;
        }

        public void Dispose() => StopRecording();
    }

    internal sealed class StreamingOutput : IDisposable
    {
        private readonly AVAudioEngine engine;
        private readonly AVAudioPlayerNode player = new();
        private readonly AVAudioFormat format = new(VoicePreset.SampleRate, 1);
        private readonly Queue<Task> pending = new();

        public StreamingOutput(AudioDevice device)
        {
            engine = device.Engine;
            engine.AttachNode(player);
            engine.Connect(player, engine.MainMixerNode, format);
        }

        public async Task WriteAsync(Stream pcm, CancellationToken token)
        {
            var bytes = new byte[VoicePreset.SampleRate * 2 / 25];
            while (true)
            {
                var size = await pcm.ReadAtLeastAsync(bytes, bytes.Length, false, token).ConfigureAwait(false);
                if (size == 0)
                    return;
                if (size % 2 != 0)
                    throw new InvalidOperationException("合成音频格式无效。");
                if (pending.Count >= 3)
                    await pending.Dequeue().WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var floats = new float[size / 2];
                for (var index = 0; index < floats.Length; index++)
                    floats[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * 2)) / 32768f;
                var buffer = new AVAudioPcmBuffer(format, (uint)floats.Length) { FrameLength = (uint)floats.Length };
                Marshal.Copy(floats, 0, Marshal.ReadIntPtr(buffer.FloatChannelData), floats.Length);
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.ScheduleBuffer(buffer, AVAudioPlayerNodeCompletionCallbackType.PlayedBack, _ =>
                {
                    buffer.Dispose();
                    done.TrySetResult();
                });
                pending.Enqueue(done.Task);
                if (!player.Playing)
                    player.Play();
            }
        }

        public void Dispose()
        {
            player.Stop();
            engine.Stop();
            engine.DetachNode(player);
            player.Dispose();
            format.Dispose();
        }
    }
}
