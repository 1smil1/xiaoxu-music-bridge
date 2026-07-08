using System;

namespace xiaoxu_music_bridge.Audio;

/// <summary>
/// Multi-band onset detector: spectral flux + per-band fast/slow EMA thresholding.
/// Output is a per-band onset pulse (1.0 on hit, decays 0.8 per frame).
/// </summary>
public sealed class OnsetSet
{
    public float SpectralFlux;  // 0-1 (normalized)
    public float BassOnset;
    public float MidOnset;
    public float TrebleOnset;
    public float FluxOnset;
}

public sealed class OnsetDetector
{
    private readonly int _n;

    // Previous magnitude frame (length N/2)
    private readonly float[] _prevMag;

    // Per-band fast/slow EMA
    private float _bassFast, _bassSlow;
    private float _midFast, _midSlow;
    private float _trebleFast, _trebleSlow;

    // Per-band onset output (decaying)
    private float _bassOnset, _midOnset, _trebleOnset;
    private float _fluxOnset;

    // Per-band cooldown timers (seconds)
    private float _bassCooldown, _midCooldown, _trebleCooldown, _fluxCooldown;

    // Spectral flux EMA (for normalization)
    private float _fluxAvg;

    public OnsetDetector(int n)
    {
        _n = n;
        _prevMag = new float[n / 2];
    }

    /// <summary>
    /// Process a new magnitude frame and current features. Decay onset values
    /// regardless of input (so single-frame pulses fade over ~3 frames).
    /// </summary>
    public void Update(
        ReadOnlySpan<float> magnitude,
        AudioFeatures f,
        float dt,
        OnsetSet dest)
    {
        int halfN = _n / 2;
        if (magnitude.Length < halfN) throw new ArgumentException("magnitude too short.");

        // ---- Spectral flux ----
        float flux = 0f;
        for (int i = 0; i < halfN; i++)
        {
            float diff = magnitude[i] - _prevMag[i];
            if (diff > 0) flux += diff;
        }
        // Normalize: keep moving average of flux, divide by it.
        _fluxAvg = _fluxAvg * 0.92f + flux * 0.08f;
        float fluxNorm = _fluxAvg > 1e-6f ? MathF.Min(1f, flux / (_fluxAvg * 4f)) : 0f;
        dest.SpectralFlux = fluxNorm;

        // Save current as previous for next frame
        for (int i = 0; i < halfN; i++) _prevMag[i] = magnitude[i];

        // ---- Per-band EMA + threshold ----
        UpdateBand(ref _bassFast, ref _bassSlow, f.Bass, 0.50f, 0.02f);
        UpdateBand(ref _midFast, ref _midSlow, f.Mid, 0.50f, 0.02f);
        UpdateBand(ref _trebleFast, ref _trebleSlow, f.Treble, 0.50f, 0.02f);

        // ---- Cooldown decay ----
        _bassCooldown -= dt;
        _midCooldown -= dt;
        _trebleCooldown -= dt;
        _fluxCooldown -= dt;

        // ---- Trigger onsets ----
        if (f.Bass > 0.20f && f.Bass > _bassSlow * 1.45f && _bassCooldown <= 0f)
        {
            _bassOnset = 1f;
            _bassCooldown = 0.20f;
        }
        else
        {
            _bassOnset *= 0.80f;
        }

        if (f.Mid > 0.20f && f.Mid > _midSlow * 1.45f && _midCooldown <= 0f)
        {
            _midOnset = 1f;
            _midCooldown = 0.15f;
        }
        else
        {
            _midOnset *= 0.80f;
        }

        if (f.Treble > 0.20f && f.Treble > _trebleSlow * 1.45f && _trebleCooldown <= 0f)
        {
            _trebleOnset = 1f;
            _trebleCooldown = 0.10f;
        }
        else
        {
            _trebleOnset *= 0.80f;
        }

        if (fluxNorm > 0.50f && _fluxCooldown <= 0f)
        {
            _fluxOnset = 1f;
            _fluxCooldown = 0.18f;
        }
        else
        {
            _fluxOnset *= 0.80f;
        }

        dest.BassOnset = _bassOnset;
        dest.MidOnset = _midOnset;
        dest.TrebleOnset = _trebleOnset;
        dest.FluxOnset = _fluxOnset;
    }

    private static void UpdateBand(ref float fast, ref float slow, float v, float fastA, float slowA)
    {
        fast += fastA * (v - fast);
        slow += slowA * (v - slow);
    }
}
