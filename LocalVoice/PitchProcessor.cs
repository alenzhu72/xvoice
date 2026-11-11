using System.Buffers.Binary;

namespace VoiceChange;

internal sealed record PitchPreset(string Name, int Semitones, double Tone)
{
    public override string ToString() => Name;
}

internal sealed class PitchProcessor
{
    private const int SampleRate = VoicePreset.SampleRate;
    private const int Sweep = SampleRate * 38 / 1000;
    private const int MinDelay = SampleRate * 3 / 1000;
    private readonly float[] history = new float[2048];
    private int write;
    private double phase;
    private double ratio = 1;
    private double tone;
    private float low;
    private float envelope;

    public int Process(Span<byte> pcm, int semitones, double brightness, double gain)
    {
        var target = Math.Pow(2, semitones / 12.0);
        var peak = 0;
        for (var index = 0; index < pcm.Length; index += 2)
        {
            var source = BinaryPrimitives.ReadInt16LittleEndian(pcm[index..]);
            peak = Math.Max(peak, Math.Abs((int)source));
            history[write] = source / 32768f;
            ratio += (target - ratio) * 0.003;
            tone += (brightness - tone) * 0.003;
            phase += (1 - ratio) / Sweep;
            phase -= Math.Floor(phase);
            var other = phase + 0.5;
            if (other >= 1) other -= 1;
            var weight = 0.5 - 0.5 * Math.Cos(2 * Math.PI * phase);
            var shifted = Read(MinDelay + phase * Sweep) * weight +
                Read(MinDelay + other * Sweep) * (1 - weight);
            var wet = Math.Min(1, Math.Abs(ratio - 1) * 30);
            var value = history[write] * (1 - wet) + shifted * wet;
            low += 0.2f * ((float)value - low);
            value += tone * (value - low);
            envelope = Math.Max(Math.Abs(history[write]), envelope * 0.999f);
            var gate = Math.Min(1, envelope * 900);
            var output = (short)Math.Clamp(value * gate * gain * 32768, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[index..], output);
            write = (write + 1) & (history.Length - 1);
        }
        return peak;
    }

    private float Read(double delay)
    {
        var whole = (int)delay;
        var fraction = (float)(delay - whole);
        var index = (write - whole) & (history.Length - 1);
        var older = (index - 1) & (history.Length - 1);
        return history[index] + (history[older] - history[index]) * fraction;
    }
}
