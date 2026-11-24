using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoiceChange;

internal sealed class QualityModels : IDisposable
{
    private readonly InferenceSession rmvpe;
    private readonly float[] mel;
    private readonly float[] centers, vectors;
    private readonly int[] offsets;
    private readonly string inputName;
    private readonly float[] window = Enumerable.Range(0, 1024).Select(i => .5f - .5f * MathF.Cos(2 * MathF.PI * i / 1024)).ToArray();
    private readonly Complex[] spectrum = new Complex[1024];
    private readonly float[] magnitude = new float[513];
    private readonly float[] bands = new float[128 * 32];

    internal QualityModels(string directory, string voiceModel, SessionOptions options)
    {
        mel = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(directory, "mel.bin"))).ToArray();
        // This compact file stores the vectors and IVF buckets exported from the original FAISS index.
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, Path.GetFileNameWithoutExtension(voiceModel) + ".ivf")));
        int lists = reader.ReadInt32(), count = reader.ReadInt32();
        centers = ReadFloats(lists * 256);
        offsets = new int[lists + 1];
        for (int i = 0; i < lists; i++)
            offsets[i + 1] = offsets[i] + reader.ReadInt32();
        if (offsets[^1] != count) throw new InvalidDataException("音色索引无效。");
        vectors = ReadFloats(count * 256);
        rmvpe = new InferenceSession(Path.Combine(directory, "rmvpe.onnx"), options);
        inputName = rmvpe.InputMetadata.Keys.Single();

        float[] ReadFloats(int size) => MemoryMarshal.Cast<byte, float>(reader.ReadBytes(size * sizeof(float))).ToArray();
    }

    internal void Retrieve(float[] features, int width)
    {
        // Earlier frames provide encoder context. Retrieve for the final 250 ms used for synthesis.
        for (int offset = Math.Max(0, features.Length - 22 * width); offset < features.Length; offset += width)
        {
            var query = features.AsSpan(offset, width);
            int bestList = 0;
            float best = float.PositiveInfinity;
            for (int i = 0; i < offsets.Length - 1; i++)
            {
                float distance = Distance(query, centers.AsSpan(i * width, width));
                if (distance < best) { best = distance; bestList = i; }
            }
            Mix(query, bestList, width);
        }
    }

    private void Mix(Span<float> query, int list, int width)
    {
        Span<float> distances = stackalloc float[8];
        distances.Fill(float.PositiveInfinity);
        Span<int> ids = stackalloc int[8];
        int count = 0;
        for (int i = offsets[list]; i < offsets[list + 1]; i++)
        {
            float distance = Distance(query, vectors.AsSpan(i * width, width));
            if (distance >= distances[^1]) continue;
            int position = distances.Length - 1;
            while (position > 0 && distance < distances[position - 1])
            {
                distances[position] = distances[position - 1];
                ids[position] = ids[position - 1];
                position--;
            }
            distances[position] = distance;
            ids[position] = i;
            count = Math.Min(count + 1, 8);
        }
        if (count == 0) return;
        float total = 0;
        for (int i = 0; i < count; i++)
        {
            float ratio = Math.Max(distances[0], 1e-6f) / Math.Max(distances[i], 1e-6f);
            distances[i] = ratio * ratio;
            total += distances[i];
        }
        for (int j = 0; j < width; j++)
        {
            float mixed = 0;
            for (int i = 0; i < count; i++) mixed += vectors[ids[i] * width + j] * distances[i];
            query[j] = .65f * query[j] + .35f * mixed / total;
        }
    }

    private static float Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        for (int j = 0; j < a.Length; j += Vector<float>.Count)
        {
            var delta = new Vector<float>(a[j..]) - new Vector<float>(b[j..]);
            sum += delta * delta;
        }
        return Vector.Sum(sum);
    }

    internal void Pitch(float[] history, float[] frequency, float shift)
    {
        const int frames = 32;
        for (int frame = 0; frame < frames; frame++)
        {
            int start = history.Length - frames * 160 + frame * 160 - 512;
            for (int i = 0; i < spectrum.Length; i++)
            {
                int index = start + i;
                if (index >= history.Length) index = history.Length * 2 - 2 - index;
                spectrum[i] = history[index] * window[i];
            }
            Fft(spectrum);
            for (int i = 0; i < magnitude.Length; i++)
                magnitude[i] = (float)spectrum[i].Magnitude;
            for (int band = 0; band < 128; band++)
                bands[band * frames + frame] = MathF.Log(Math.Max(1e-5f,
                    VectorDot(magnitude, mel.AsSpan(band * 513, 513))));
        }
        using var values = rmvpe.Run([NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<float>(bands, [1, 128, frames]))]);
        var scores = values.Single().AsTensor<float>().ToArray();
        if (scores.Length != frames * 360) throw new InvalidDataException("RMVPE 输出尺寸不匹配。");
        Array.Copy(frequency, 13, frequency, 0, frequency.Length - 13);
        for (int f = 0; f < frames; f++)
        {
            var row = scores.AsSpan(f * 360, 360);
            int bin = 0;
            for (int i = 1; i < row.Length; i++) if (row[i] > row[bin]) bin = i;
            float sum = 0, weighted = 0;
            for (int i = Math.Max(0, bin - 4); i <= Math.Min(359, bin + 4); i++)
            {
                sum += row[i];
                weighted += row[i] * (1997.3794f + 20 * i);
            }
            frequency[frequency.Length - frames + f] = row[bin] > .03f && sum > 0
                ? 10 * MathF.Pow(2, weighted / sum / 1200) * shift : 0;
        }
    }

    private static float VectorDot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        int step = Vector<float>.Count;
        for (int i = 0; i < 512; i += step) sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        return Vector.Sum(sum) + a[512] * b[512];
    }

    private static void Fft(Complex[] values)
    {
        for (int i = 1, j = 0; i < values.Length; i++)
        {
            int bit = values.Length >> 1;
            while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= values.Length; length *= 2)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < values.Length; start += length)
            {
                var phase = Complex.One;
                for (int i = 0; i < length / 2; i++)
                {
                    int left = start + i, right = left + length / 2;
                    var t = phase * values[right];
                    values[right] = values[left] - t;
                    values[left] += t;
                    phase *= step;
                }
            }
        }
    }

    public void Dispose() => rmvpe.Dispose();
}

