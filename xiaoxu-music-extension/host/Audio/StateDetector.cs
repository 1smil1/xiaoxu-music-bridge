using System;

namespace xiaoxu_music_bridge.Audio;

public sealed class AudioState
{
    public bool IsPlaying;
    public bool IsSilence;
    public float SilenceDuration; // seconds
}

public sealed class StateDetector
{
    private const float RmsThreshold = 0.005f;
    private const float SilenceEnterSec = 1.5f;
    private const float SilenceExitSec = 0.3f;

    private float _silenceTimer;
    private bool _isSilence;
    private double _silenceStartTime;
    private double _lastElapsed = double.NegativeInfinity;

    public AudioState State { get; } = new();

    /// <summary>
    /// Update state with current RMS and elapsed time.
    /// </summary>
    public void Update(float rms, double elapsedSec)
    {
        if (_lastElapsed < 0) _lastElapsed = elapsedSec;
        float dt = (float)Math.Max(0, elapsedSec - _lastElapsed);
        _lastElapsed = elapsedSec;

        bool playingNow = rms > RmsThreshold;
        State.IsPlaying = playingNow;

        if (!playingNow) _silenceTimer += dt;
        else _silenceTimer = 0f;

        if (!_isSilence && _silenceTimer > SilenceEnterSec)
        {
            _isSilence = true;
            _silenceStartTime = elapsedSec;
        }
        else if (_isSilence && _silenceTimer == 0f)
        {
            // Need playing for at least 0.3s before clearing silence
            if (elapsedSec - _silenceStartTime > SilenceExitSec)
            {
                _isSilence = false;
            }
        }

        State.IsSilence = _isSilence;
        State.SilenceDuration = _isSilence ? (float)(elapsedSec - _silenceStartTime) : 0f;
    }

    public void Reset()
    {
        _silenceTimer = 0f;
        _isSilence = false;
        _silenceStartTime = 0f;
        _lastElapsed = double.NegativeInfinity;
    }
}
