using System;

namespace xiaoxu_music_bridge.Audio;

public sealed class BeatState
{
    public float Bpm;
    public float BpmConfidence;
    public float BeatPhase;     // 0-1 within current beat
    public bool IsDownbeat;
    public long BeatCount;
    public bool TempoChanged;
}

public sealed class BeatTracker
{
    // 4 seconds of onset envelope at 30Hz = 120 samples
    private const int EnvelopeSize = 120;
    private const int MinLag = 15;  // 30Hz hop → 0.5s lag (max 120 BPM)
    private const int MaxLag = 60;  // 2s lag (min 30 BPM)

    private readonly float[] _envelope = new float[EnvelopeSize];
    private int _envelopeIdx;
    private int _envelopeCount;

    private float _bpm;
    private float _confidence;
    private float _phase;        // 0-1 within current beat
    private double _lastBeatTime; // seconds since Start
    private long _beatCount;
    private float _lastReportedBpm;
    private bool _tempoChanged;

    public BeatState State { get; } = new();

    /// <summary>
    /// Feed one onset envelope value (0-1, sum of bass + flux) and elapsed time.
    /// </summary>
    public void Update(float onsetEnv, double elapsedSec, float dt)
    {
        // Store into circular buffer
        _envelope[_envelopeIdx] = onsetEnv;
        _envelopeIdx = (_envelopeIdx + 1) % EnvelopeSize;
        if (_envelopeCount < EnvelopeSize) _envelopeCount++;

        if (_envelopeCount < MaxLag + 1) return;

        // ---- Autocorrelation over lag range ----
        float bestLag = 0f;
        float bestVal = float.NegativeInfinity;
        float mean = 0f;
        for (int i = 0; i < EnvelopeSize; i++) mean += _envelope[i];
        mean /= EnvelopeSize;

        for (int lag = MinLag; lag <= MaxLag; lag++)
        {
            float sum = 0f;
            for (int i = 0; i < EnvelopeSize; i++)
            {
                int j = (i + lag) % EnvelopeSize;
                sum += (_envelope[i] - mean) * (_envelope[j] - mean);
            }
            if (sum > bestVal) { bestVal = sum; bestLag = lag; }
        }

        // Compute mean absolute to normalize
        float varSum = 0f;
        for (int i = 0; i < EnvelopeSize; i++) varSum += MathF.Abs(_envelope[i] - mean);
        float meanAbs = varSum / EnvelopeSize;
        float confidence = meanAbs > 1e-6f ? MathF.Min(1f, bestVal / (meanAbs * EnvelopeSize * 4f)) : 0f;

        // Only update BPM if confidence is decent
        if (confidence > 0.3f && bestLag > 0f)
        {
            float bpm = 60f / (bestLag * (1f / 30f));
            if (bpm < 30f) bpm = 30f;
            if (bpm > 240f) bpm = 240f;

            // Smooth BPM (heavy smoothing for stability)
            if (_bpm <= 0f) _bpm = bpm;
            else
            {
                // If difference is large, snap faster (tempo change); else smooth
                if (MathF.Abs(bpm - _bpm) > 15f) _bpm = _bpm * 0.7f + bpm * 0.3f;
                else _bpm = _bpm * 0.95f + bpm * 0.05f;
            }
            _confidence = _confidence * 0.92f + confidence * 0.08f;

            if (MathF.Abs(_bpm - _lastReportedBpm) > 5f && _lastReportedBpm > 0f)
                _tempoChanged = true;
            _lastReportedBpm = _bpm;
        }
        else
        {
            _confidence *= 0.95f;
        }

        // ---- Beat phase tracking ----
        if (_bpm > 30f)
        {
            float ibi = 60f / _bpm;
            _phase = (float)((elapsedSec - _lastBeatTime) / ibi);
            while (_phase >= 1f)
            {
                _phase -= 1f;
                _lastBeatTime += ibi;
                _beatCount++;
            }
        }

        State.Bpm = _bpm;
        State.BpmConfidence = _confidence;
        State.BeatPhase = _phase;
        State.BeatCount = _beatCount;
        State.IsDownbeat = (_beatCount % 4) == 0 && _phase < 0.05f;
        State.TempoChanged = _tempoChanged;
        _tempoChanged = false;
    }

    public void Reset()
    {
        _bpm = 0f;
        _confidence = 0f;
        _phase = 0f;
        _lastBeatTime = 0;
        _beatCount = 0;
        _envelopeIdx = 0;
        _envelopeCount = 0;
        for (int i = 0; i < EnvelopeSize; i++) _envelope[i] = 0f;
    }
}
