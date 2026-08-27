using System;

namespace xiaoxu_music_bridge.Audio;

/// <summary>
/// Audio features extracted from a single FFT frame.
/// 7 bands + RMS + loudness + spectral centroid + ZCR.
/// </summary>
public sealed class AudioFeatures
{
    // 7 bands (normalized 0-1)
    public float Sub;       // 20-60Hz
    public float Bass;      // 60-250Hz
    public float LowMid;    // 250-500Hz
    public float Mid;       // 500-2kHz
    public float HighMid;   // 2k-4kHz
    public float Treble;    // 4k-8kHz
    public float Air;       // 8k-16kHz

    public float Rms;        // time-domain RMS (0-1+)
    public float Loudness;   // approximate dBFS
    public float Centroid;   // spectral centroid (Hz)
    public float Zcr;        // zero-crossing rate (0-1)
}

/// <summary>
/// Extract 7 bands + RMS + centroid + ZCR from a single FFT magnitude frame
/// (or directly from a time-domain window for RMS/ZCR).
///
/// Sample rate is fixed at construction; bin width = sr / N.
/// </summary>
public sealed class FeatureExtractor
{
    public int N { get; }
    public int SampleRate { get; }
    private readonly float _binWidth;

    // Band definitions (Hz ranges)
    private static readonly (float Lo, float Hi)[] Bands =
    {
        (20f, 60f),       // Sub
        (60f, 250f),      // Bass
        (250f, 500f),     // LowMid
        (500f, 2000f),    // Mid
        (2000f, 4000f),   // HighMid
        (4000f, 8000f),   // Treble
        (8000f, 16000f),  // Air
    };

    public FeatureExtractor(int n, int sampleRate)
    {
        N = n;
        SampleRate = sampleRate;
        _binWidth = (float)sampleRate / n;
    }

    /// <summary>
    /// Compute all features from one frame.
    /// <paramref name="magnitude"/> is FFT magnitude (length N/2).
    /// <paramref name="timeDomain"/> is the windowed time-domain samples (length N).
    /// </summary>
    public void Extract(ReadOnlySpan<float> magnitude, ReadOnlySpan<float> timeDomain, AudioFeatures dest)
    {
        int halfN = N / 2;

        // 7-band energy (sum of squared magnitudes in band)
        Span<float> energies = stackalloc float[Bands.Length];
        Span<float> maxMag = stackalloc float[Bands.Length];

        float totalEnergyForCentroid = 0f;
        float weightedFreqForCentroid = 0f;

        for (int i = 1; i < halfN; i++) // skip DC (i=0)
        {
            float mag = magnitude[i];
            float freq = i * _binWidth;
            float energy = mag * mag;

            for (int b = 0; b < Bands.Length; b++)
            {
                var (lo, hi) = Bands[b];
                if (freq >= lo && freq < hi)
                {
                    energies[b] += energy;
                    if (mag > maxMag[b]) maxMag[b] = mag;
                }
            }

            totalEnergyForCentroid += energy;
            weightedFreqForCentroid += freq * energy;
        }

        // Normalize each band: divide by max-mag-squared within band (peak energy reference).
        // This gives a perceptually meaningful 0-1 (loud sounds → near 1, silent → 0).
        for (int b = 0; b < Bands.Length; b++)
        {
            float refE = maxMag[b] * maxMag[b] * 16f + 1e-7f; // 16 = arbitrary peak bins
            float norm = energies[b] / refE;
            if (norm > 1f) norm = 1f;
            SetBand(dest, b, norm);
        }

        // Centroid (Hz)
        dest.Centroid = totalEnergyForCentroid > 1e-12f
            ? weightedFreqForCentroid / totalEnergyForCentroid
            : 0f;

        // RMS (time-domain)
        float sumSq = 0f;
        int zc = 0;
        float prev = 0f;
        for (int i = 0; i < timeDomain.Length; i++)
        {
            float s = timeDomain[i];
            if (!float.IsFinite(s)) s = 0f;
            sumSq += s * s;
            if (i > 0 && MathF.Sign(s) != MathF.Sign(prev)) zc++;
            prev = s;
        }
        dest.Rms = MathF.Sqrt(sumSq / timeDomain.Length);
        dest.Zcr = (float)zc / Math.Max(1, timeDomain.Length - 1);
        dest.Loudness = 20f * MathF.Log10(dest.Rms + 1e-7f);
    }

    private static void SetBand(AudioFeatures f, int idx, float v)
    {
        switch (idx)
        {
            case 0: f.Sub = v; break;
            case 1: f.Bass = v; break;
            case 2: f.LowMid = v; break;
            case 3: f.Mid = v; break;
            case 4: f.HighMid = v; break;
            case 5: f.Treble = v; break;
            case 6: f.Air = v; break;
        }
    }
}
