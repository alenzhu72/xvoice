using Android.Media;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceChange;

internal static class LocalAudio
{
    private const int SampleRate = VoicePreset.SampleRate;
    private const int BlockSamples = SampleRate / 50;

    public static void Run(PitchPreset preset, IProgress<int> meter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var inputSize = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        var outputSize = AudioTrack.GetMinBufferSize(SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
        if (inputSize <= 0 || outputSize <= 0)
            throw new InvalidOperationException("当前音频设备不支持 24 kHz 单声道。");

        using var record = new AudioRecord(AudioSource.Mic, SampleRate, ChannelIn.Mono,
            Encoding.Pcm16bit, Math.Max(inputSize, BlockSamples * sizeof(short) * 3));
        using var formatBuilder = new AudioFormat.Builder();
        using var format = formatBuilder.SetSampleRate(SampleRate)!
            .SetEncoding(Encoding.Pcm16bit)!.SetChannelMask(ChannelOut.Mono)!.Build();
        using var attributesBuilder = new AudioAttributes.Builder();
        using var attributes = attributesBuilder.SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Speech)!.Build();
        using var builder = new AudioTrack.Builder();
        using var track = builder.SetAudioFormat(format!)!.SetAudioAttributes(attributes!)!
            .SetBufferSizeInBytes(Math.Max(outputSize, BlockSamples * sizeof(short) * 2))!
            .SetTransferMode(AudioTrackMode.Stream)!.Build()!;
        try
        {
            if (record.State != State.Initialized || track.State != AudioTrackState.Initialized)
                throw new InvalidOperationException("音频设备初始化失败，请检查麦克风是否被占用。");

            var processor = new PitchProcessor();
            var samples = new short[BlockSamples];
            var interval = Stopwatch.StartNew();
            var peak = 0;
            record.StartRecording();
            if (record.RecordingState != RecordState.Recording)
                throw new InvalidOperationException("麦克风启动失败，请检查系统录音权限。");
            track.Play();
            meter.Report(0);

            while (!token.IsCancellationRequested)
            {
                var count = record.Read(samples, 0, samples.Length);
                if (token.IsCancellationRequested)
                    break;
                if (count < 0)
                    throw new InvalidOperationException($"麦克风读取失败（{count}）。");
                if (count == 0)
                    continue;

                var pcm = MemoryMarshal.AsBytes(samples.AsSpan(0, count));
                peak = Math.Max(peak, processor.Process(pcm, preset.Semitones, preset.Tone, 1));
                // Keep playback bounded. Drop an unwritten tail if the output device is behind.
                var written = track.Write(samples, 0, count, WriteMode.NonBlocking);
                if (written < 0)
                    throw new InvalidOperationException($"音频输出失败（{written}）。");
                if (interval.ElapsedMilliseconds >= 100)
                {
                    meter.Report(Math.Clamp(peak * 400 / 32768, 0, 100));
                    peak = 0;
                    interval.Restart();
                }
            }
        }
        finally
        {
            try
            {
                if (track.PlayState == PlayState.Playing)
                {
                    track.Pause();
                    track.Flush();
                }
            }
            finally
            {
                try
                {
                    track.Release();
                }
                finally
                {
                    try
                    {
                        if (record.RecordingState == RecordState.Recording)
                            record.Stop();
                    }
                    finally { record.Release(); }
                }
            }
        }
    }
}
