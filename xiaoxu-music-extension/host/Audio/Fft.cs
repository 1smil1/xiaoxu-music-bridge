using System;

namespace xiaoxu_music_bridge.Audio;

/// <summary>
/// Cooley-Tukey radix-2 in-place FFT with pre-computed bit-reversal table.
/// N must be a power of two. Real input, complex output (or vice versa).
/// </summary>
public sealed class Fft
{
    public int Size { get; }
    private readonly float[] _cosTable;
    private readonly float[] _sinTable;
    private readonly int[] _bitReverse;

    public Fft(int size)
    {
        if ((size & (size - 1)) != 0)
            throw new ArgumentException($"FFT size must be a power of two (got {size}).");
        Size = size;

        int logN = (int)Math.Log2(size);
        _cosTable = new float[size / 2];
        _sinTable = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            float angle = -2f * MathF.PI * i / size;
            _cosTable[i] = MathF.Cos(angle);
            _sinTable[i] = MathF.Sin(angle);
        }

        _bitReverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int x = i;
            int r = 0;
            for (int j = 0; j < logN; j++)
            {
                r = (r << 1) | (x & 1);
                x >>= 1;
            }
            _bitReverse[i] = r;
        }
    }

    /// <summary>
    /// Forward real FFT. Reads <paramref name="re"/> length N (real input),
    /// writes magnitude spectrum into <paramref name="mag"/> length N/2.
    /// </summary>
    public void ForwardMagnitude(ReadOnlySpan<float> re, Span<float> mag)
    {
        int n = Size;
        if (re.Length < n) throw new ArgumentException($"re must be >= {n} (got {re.Length}).");
        if (mag.Length < n / 2) throw new ArgumentException($"mag must be >= {n / 2} (got {mag.Length}).");

        // Local mutable copy for in-place transform
        Span<float> reBuf = stackalloc float[n];
        Span<float> imBuf = stackalloc float[n];
        for (int i = 0; i < n; i++) reBuf[i] = re[i];

        // Bit-reverse permutation
        for (int i = 0; i < n; i++)
        {
            int j = _bitReverse[i];
            if (j > i)
            {
                (reBuf[i], reBuf[j]) = (reBuf[j], reBuf[i]);
            }
        }

        // Cooley-Tukey iterative butterfly
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1;
            int tableStep = n / size;
            for (int i = 0; i < n; i += size)
            {
                for (int k = 0, idx = 0; k < half; k++, idx += tableStep)
                {
                    float cos = _cosTable[idx];
                    float sin = _sinTable[idx];
                    float tpre = reBuf[i + k + half] * cos - imBuf[i + k + half] * sin;
                    float tpim = reBuf[i + k + half] * sin + imBuf[i + k + half] * cos;
                    reBuf[i + k + half] = reBuf[i + k] - tpre;
                    imBuf[i + k + half] = imBuf[i + k] - tpim;
                    reBuf[i + k] += tpre;
                    imBuf[i + k] += tpim;
                }
            }
        }

        // Magnitude (first half, excluding DC and Nyquist)
        for (int i = 0; i < n / 2; i++)
        {
            float r = reBuf[i];
            float im = imBuf[i];
            mag[i] = MathF.Sqrt(r * r + im * im);
        }
    }

    /// <summary>
    /// Apply Hann window in-place.
    /// </summary>
    public static void ApplyHannWindow(Span<float> buf)
    {
        int n = buf.Length;
        for (int i = 0; i < n; i++)
        {
            float w = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (n - 1)));
            buf[i] *= w;
        }
    }
}
