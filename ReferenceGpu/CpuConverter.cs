using System.Buffers.Binary;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoiceChange;

internal sealed class CpuConverter : IDisposable
{
    public const int BlockMs = 130;
    public const int InputSamples = 2080;
    private const int ContextSamples = 35520;
    private const int FeatureWidth = 256;
    private const int Frames = 222;
    private const int OutputSamples = VoicePreset.SampleRate * BlockMs / 1000;
    private const int Overlap = VoicePreset.SampleRate * 8 / 100;

    private readonly InferenceSession encoder;
    private readonly InferenceSession generator;
    private readonly float[] history = new float[ContextSamples];
    private readonly float[] features = new float[Frames * FeatureWidth];
    private readonly long[] pitch = new long[Frames];
    private readonly float[] frequency = new float[Frames];
    private readonly float[] previous = new float[Overlap];
    private readonly float[] reduced = new float[ContextSamples / 4];
    private bool hasPrevious;
#if RVC_QUALITY
    private readonly QualityModels? quality;
#endif

    public CpuConverter(string directory, string voiceModel = "guanguanV1.onnx", int? graphicsAdapter = null)
    {
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        if (graphicsAdapter is int adapter)
        {
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(adapter);
        }
        var encoderFile = graphicsAdapter.HasValue ? "contentvec-fp32.onnx" : "contentvec.onnx";
        encoder = new InferenceSession(Path.Combine(directory, encoderFile), options);
        try
        {
            generator = new InferenceSession(Path.Combine(directory, voiceModel), options);
            if (encoder.InputMetadata["audio"].Dimensions[1] != ContextSamples ||
                generator.InputMetadata["features"].Dimensions[1] != Frames)
            {
                generator.Dispose();
                throw new InvalidDataException("此音色为旧处理块规格，请用随包 export_reference.py 重新导出到本版。");
            }
#if RVC_QUALITY
            try
            {
                if (graphicsAdapter.HasValue)
                    quality = new QualityModels(directory, voiceModel, options);
            }
            catch { generator.Dispose(); throw; }
#endif
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    public byte[] Convert(ReadOnlySpan<byte> input, int semitones, float gain)
    {
        if (input.Length != InputSamples * sizeof(short))
            throw new ArgumentException("需要一块 130 毫秒的 PCM 音频。", nameof(input));
        history.AsSpan(InputSamples).CopyTo(history);
        for (int i = 0; i < InputSamples; i++)
            history[ContextSamples - InputSamples + i] = BinaryPrimitives.ReadInt16LittleEndian(input[(i * 2)..]) / 32768f;

        using var encoded = encoder.Run([
            NamedOnnxValue.CreateFromTensor("audio", new DenseTensor<float>(history, [1, ContextSamples]))
        ]);
        var content = encoded.Single().AsTensor<float>().ToArray();
#if RVC_QUALITY
        quality?.Retrieve(content, FeatureWidth);
#endif
        var sourceFrames = content.Length / FeatureWidth;
        for (int frame = 0; frame < Frames; frame++)
        {
            var source = Math.Min(frame / 2, sourceFrames - 1) * FeatureWidth;
            content.AsSpan(source, FeatureWidth).CopyTo(features.AsSpan(frame * FeatureWidth));
        }

#if RVC_QUALITY
        if (quality is not null)
        {
            quality.Pitch(history, frequency, MathF.Pow(2, semitones / 12f));
            for (int frame = 0; frame < Frames; frame++)
            {
                float mel = 1127 * MathF.Log(1 + frequency[frame] / 700);
                pitch[frame] = Math.Clamp((int)MathF.Round((mel - 77.75f) * 254 / 729.2f + 1), 1, 255);
            }
        }
        else
#endif
        EstimatePitch(semitones);
        using var converted = generator.Run([
            NamedOnnxValue.CreateFromTensor("features", new DenseTensor<float>(features, [1, Frames, FeatureWidth])),
            NamedOnnxValue.CreateFromTensor("pitch", new DenseTensor<long>(pitch, [1, Frames])),
            NamedOnnxValue.CreateFromTensor("frequency", new DenseTensor<float>(frequency, [1, Frames]))
        ]);
        var result = converted.Single().AsTensor<float>().ToArray();
        if (result.Length == 0 || result.Any(sample => !float.IsFinite(sample)))
            throw new InvalidOperationException("模型返回了无效音频。");
        int search = VoicePreset.SampleRate / 100;
        var resampled = new float[OutputSamples + Overlap + search];
        for (int i = 0; i < resampled.Length; i++)
        {
            double position = i * (double)result.Length / resampled.Length;
            int center = (int)position;
            double sum = 0, norm = 0;
            for (int tap = -16; tap <= 16; tap++)
            {
                int source = center + tap;
                if (source < 0 || source >= result.Length) continue;
                double distance = position - source;
                if (Math.Abs(distance) >= 16) continue;
                double sinc = Math.Abs(distance) < 1e-10 ? 1 : Math.Sin(Math.PI * distance) / (Math.PI * distance);
                double weight = sinc * (.5 + .5 * Math.Cos(Math.PI * distance / 16));
                sum += result[source] * weight;
                norm += weight;
            }
            resampled[i] = norm == 0 ? 0 : (float)(sum / norm);
        }

        // Apply a 40 ms, 10 ms-hop RMS envelope before SOLA, and keep the scaled overlap.
        var envelope = new float[24];
        for (int frame = 0; frame < envelope.Length; frame++)
        {
            int center = Math.Min(resampled.Length - 1, frame * VoicePreset.SampleRate / 100);
            double sourceEnergy = 0, convertedEnergy = 0;
            int count = 0;
            for (int j = -VoicePreset.SampleRate / 50; j < VoicePreset.SampleRate / 50; j += 4)
            {
                int target = center + j;
                if (target < 0 || target >= resampled.Length) continue;
                int source = ContextSamples - 3520 + (int)(target * 16000L / VoicePreset.SampleRate);
                source = Math.Clamp(source, 0, history.Length - 1);
                sourceEnergy += history[source] * history[source];
                convertedEnergy += resampled[target] * resampled[target];
                count++;
            }
            double inputRms = Math.Sqrt(sourceEnergy / Math.Max(1, count));
            double outputRms = Math.Sqrt(convertedEnergy / Math.Max(1, count));
            envelope[frame] = inputRms < .001 ? 0 :
                (float)Math.Clamp(Math.Sqrt(inputRms / Math.Max(.001, outputRms)), 0, 5);
        }
        for (int i = 0; i < resampled.Length; i++)
        {
            double time = i * 100.0 / VoicePreset.SampleRate;
            int frame = Math.Min((int)time, envelope.Length - 2);
            float ratio = (float)Math.Clamp(time - frame, 0, 1);
            resampled[i] *= envelope[frame] * (1 - ratio) + envelope[frame + 1] * ratio;
        }
        int alignment = 0;
        if (hasPrevious)
        {
            double best = double.NegativeInfinity;
            for (int offset = 0; offset <= search; offset++)
            {
                double correlation = 0, energy = 1e-8;
                for (int i = 0; i < Overlap; i++)
                {
                    float value = resampled[offset + i];
                    correlation += previous[i] * value;
                    energy += value * value;
                }
                double score = correlation / Math.Sqrt(energy);
                if (score > best) { best = score; alignment = offset; }
            }
        }
        var output = new byte[OutputSamples * 2];
        for (int i = 0; i < OutputSamples; i++)
        {
            float value = resampled[alignment + i];
            if (i < Overlap && hasPrevious)
            {
                float fade = MathF.Pow(MathF.Sin(MathF.PI * .5f * i / Overlap), 2);
                value = previous[i] * (1 - fade) + value * fade;
            }
            var pcm = (short)Math.Clamp(value * gain * 32768, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), pcm);
        }
        resampled.AsSpan(alignment + OutputSamples, Overlap).CopyTo(previous);
        hasPrevious = true;
        return output;
    }

    private void EstimatePitch(int semitones)
    {
        for (int i = 0; i < reduced.Length; i++)
        {
            int sample = i * 4;
            reduced[i] = (history[sample] + history[sample + 1] + history[sample + 2] + history[sample + 3]) * 0.25f;
        }

        float shift = MathF.Pow(2, semitones / 12f);
        for (int frame = 0; frame < Frames; frame++)
        {
            float f0 = DetectPitch(Math.Clamp(frame * 40 - 45, 0, reduced.Length - 160)) * shift;
            frequency[frame] = f0;
            float mel = 1127 * MathF.Log(1 + f0 / 700);
            pitch[frame] = Math.Clamp((int)MathF.Round((mel - 77.75f) * 254 / 729.2f + 1), 1, 255);
        }
    }

    private float DetectPitch(int offset)
    {
        const int window = 80;
        const int maximumLag = 80;
        float energy = 0;
        for (int i = 0; i < window; i++)
            energy += reduced[offset + i] * reduced[offset + i];
        if (energy < 0.0004f)
            return 0;

        Span<float> difference = stackalloc float[maximumLag + 1];
        difference[0] = 1;
        float total = 0;
        for (int lag = 1; lag <= maximumLag; lag++)
        {
            float sum = 0;
            for (int i = 0; i < window; i++)
            {
                float delta = reduced[offset + i] - reduced[offset + i + lag];
                sum += delta * delta;
            }
            total += sum;
            difference[lag] = total > 0 ? sum * lag / total : 1;
        }

        for (int lag = 8; lag < maximumLag; lag++)
        {
            if (difference[lag] >= 0.18f)
                continue;
            while (lag < maximumLag && difference[lag + 1] < difference[lag])
                lag++;
            if (lag == maximumLag)
                return 4000f / lag;
            float left = difference[lag - 1], middle = difference[lag], right = difference[lag + 1];
            float curvature = left - 2 * middle + right;
            float correction = curvature > 0 ? (left - right) / (2 * curvature) : 0;
            return 4000f / (lag + correction);
        }
        return 0;
    }

    public void Dispose()
    {
        generator.Dispose();
        encoder.Dispose();
    }
}


