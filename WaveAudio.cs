using System.Runtime.InteropServices;
using System.Diagnostics;
using NAudio.Wave;

namespace VoiceChange;

internal sealed record AudioDevice(int Id, string Name)
{
    public override string ToString() => Name;
}

internal static class WaveAudio
{
    public const int InputRate = 16000;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags)).ToInt32();
    private static readonly int LengthOffset = Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Length)).ToInt32();

    public static List<AudioDevice> Devices(bool input)
    {
        var devices = new List<AudioDevice> { new(-1, "Windows 默认设备") };
        var count = input ? WaveInEvent.DeviceCount : (int)waveOutGetNumDevs();
        for (var id = 0; id < count; id++)
        {
            if (input)
                devices.Add(new(id, WaveInEvent.GetCapabilities(id).ProductName));
            else if (waveOutGetDevCaps((nuint)id, out var caps, (uint)Marshal.SizeOf<OutputCaps>()) == 0)
                devices.Add(new(id, caps.Name));
        }
        return devices;
    }

    public static WaveInEvent CreateCapture(AudioDevice device, int sampleRate = InputRate, int bufferMilliseconds = 30) => new()
    {
        DeviceNumber = device.Id,
        WaveFormat = new WaveFormat(sampleRate, 16, 1),
        BufferMilliseconds = bufferMilliseconds,
        NumberOfBuffers = 4
    };

    public static async Task PlayAsync(AudioDevice device, byte[] pcm, CancellationToken token)
    {
        using var source = new MemoryStream(pcm, writable: false);
        using var player = new StreamingOutput(device);
        await player.WriteAsync(source, token).ConfigureAwait(false);
        await player.DrainAsync(token).ConfigureAwait(false);
    }

    internal sealed class StreamingOutput : IDisposable
    {
        private readonly int blockBytes;
        private readonly nint handle;
        private readonly List<OutputBuffer> buffers = new(3);
        private int next;

        public StreamingOutput(AudioDevice device, int blockMilliseconds = 40)
        {
            blockBytes = VoicePreset.SampleRate * 2 * blockMilliseconds / 1000;
            var format = new PcmFormat
            {
                Tag = 1,
                Channels = 1,
                SamplesPerSecond = VoicePreset.SampleRate,
                BytesPerSecond = VoicePreset.SampleRate * 2,
                BlockAlign = 2,
                BitsPerSample = 16
            };
            Check(waveOutOpen(out handle, unchecked((uint)device.Id), ref format, 0, 0, 0), "打开");
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    var buffer = new OutputBuffer(blockBytes);
                    buffers.Add(buffer);
                    Check(waveOutPrepareHeader(handle, buffer.Header, HeaderSize), "准备");
                    buffer.Prepared = true;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public async Task WriteAsync(Stream pcm, CancellationToken token)
        {
            var bytes = new byte[blockBytes];
            var received = false;
            while (true)
            {
                var count = await pcm.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
                if (count == 0)
                {
                    if (!received)
                        throw new InvalidOperationException("语音合成返回了无效音频。");
                    return;
                }

                if (count % 2 != 0)
                    throw new InvalidOperationException("语音合成返回了不完整的音频样本。");

                await QueueAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false);
                received = true;
            }
        }

        public async Task QueueAsync(ReadOnlyMemory<byte> pcm, CancellationToken token)
        {
            if (pcm.IsEmpty || pcm.Length > blockBytes || pcm.Length % 2 != 0)
                throw new ArgumentException("播放缓冲区大小无效。", nameof(pcm));
            var buffer = buffers[next];
            await WaitForBufferAsync(buffer, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            pcm.Span.CopyTo(buffer.Data);
            Marshal.WriteInt32(buffer.Header, LengthOffset, pcm.Length);
            Check(waveOutWrite(handle, buffer.Header, HeaderSize), "写入");
            buffer.Queued = true;
            next = (next + 1) % buffers.Count;
        }

        public async Task DrainAsync(CancellationToken token)
        {
            foreach (var buffer in buffers)
                await WaitForBufferAsync(buffer, token).ConfigureAwait(false);
        }

        private static async Task WaitForBufferAsync(OutputBuffer buffer, CancellationToken token)
        {
            var time = Stopwatch.StartNew();
            while (buffer.Queued && (Marshal.ReadInt32(buffer.Header, FlagsOffset) & 1) == 0)
            {
                if (time.Elapsed > TimeSpan.FromSeconds(4))
                    throw new TimeoutException("播放设备响应超时。");
                await Task.Delay(8, token).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            waveOutReset(handle);
            foreach (var buffer in buffers)
            {
                if (buffer.Prepared && waveOutUnprepareHeader(handle, buffer.Header, HeaderSize) == 0)
                    buffer.Prepared = false;
            }
            var closed = waveOutClose(handle) == 0;
            foreach (var buffer in buffers)
            {
                if (closed || !buffer.Prepared)
                    buffer.Dispose();
            }
        }
    }

    private static void Check(uint result, string action)
    {
        if (result != 0)
            throw new InvalidOperationException($"{action}播放设备失败（{result}），请检查设备连接。");
    }

    private sealed class OutputBuffer : IDisposable
    {
        private readonly GCHandle pin;
        public nint Header { get; }
        public byte[] Data { get; }
        public bool Prepared { get; set; }
        public bool Queued { get; set; }

        public OutputBuffer(int size)
        {
            Data = new byte[size];
            pin = GCHandle.Alloc(Data, GCHandleType.Pinned);
            try
            {
                Header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { Data = pin.AddrOfPinnedObject(), Length = size }, Header, false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Header);
            pin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PcmFormat
    {
        public ushort Tag, Channels;
        public int SamplesPerSecond, BytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public int Length, BytesRecorded;
        public nuint User;
        public uint Flags, Loops;
        public nint Next;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputCaps
    {
        public ushort Manufacturer, Product;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
        public uint Support;
    }

    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", ExactSpelling = true)]
    private static extern uint waveOutGetDevCaps(nuint id, out OutputCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint handle, nuint id, ref PcmFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint handle);
}
