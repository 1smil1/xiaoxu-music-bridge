using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace xiaoxu_music_bridge.Audio;

[Flags]
public enum PcmAudioFrameFlags : byte
{
    None = 0,
    Discontinuity = 1,
    KeepAlive = 2,
}

public readonly record struct AudioClockSnapshot(
    ulong SampleIndex,
    int SampleRate,
    long MonotonicMs,
    long DiscontinuityId);

public sealed class PcmAudioBroadcaster : IDisposable
{
    public const int HeaderSize = 32;
    public const int FrameDurationMilliseconds = 10;

    private readonly object _gate = new();
    private readonly int _maxFrames;
    private readonly Queue<byte[]> _queue = new();

    private PcmAudioSubscription? _subscriber;
    private float[]? _staging;
    private int _stagingSamples;
    private ulong _stagingSampleIndex;
    private long _stagingTimestampUs;
    private int _streamSampleRate;
    private int _streamChannels;
    private long _sampleIndex;
    private int _sampleRate;
    private long _monotonicMs;
    private long _discontinuityId;
    private int _pendingDiscontinuity;
    private bool _markNextPacketDiscontinuous;
    private int _disposed;

    public PcmAudioBroadcaster(int maxBufferedMilliseconds = 250)
    {
        if (maxBufferedMilliseconds < FrameDurationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedMilliseconds));

        _maxFrames = Math.Max(1, maxBufferedMilliseconds / FrameDurationMilliseconds);
    }

    public int BufferedFrameCount
    {
        get
        {
            lock (_gate) return _queue.Count;
        }
    }

    public bool TrySubscribe(out PcmAudioSubscription? subscription)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_subscriber != null)
            {
                subscription = null;
                return false;
            }

            ResetStreamState();
            subscription = new PcmAudioSubscription(this);
            _subscriber = subscription;
            return true;
        }
    }

    public void Publish(
        ReadOnlySpan<float> interleavedSamples,
        int sampleRate,
        int channels,
        long monotonicMicroseconds)
    {
        if (sampleRate <= 0 || channels <= 0 || interleavedSamples.Length == 0)
            return;

        var inputFrameCount = interleavedSamples.Length / channels;
        if (inputFrameCount == 0) return;

        var sampleIndex = (ulong)(Interlocked.Add(ref _sampleIndex, inputFrameCount) - inputFrameCount);
        Volatile.Write(ref _sampleRate, sampleRate);
        Volatile.Write(ref _monotonicMs,
            (monotonicMicroseconds + inputFrameCount * 1_000_000L / sampleRate) / 1000L);

        if (Volatile.Read(ref _subscriber) == null || Volatile.Read(ref _disposed) != 0)
            return;

        if (!Monitor.TryEnter(_gate))
        {
            MarkPendingDiscontinuity();
            return;
        }

        try
        {
            if (_subscriber == null) return;

            var usableSampleCount = inputFrameCount * channels;
            var formatChanged = _streamSampleRate != 0
                && (_streamSampleRate != sampleRate || _streamChannels != channels);
            if (formatChanged)
            {
                _queue.Clear();
                ResetStaging();
                MarkPendingDiscontinuity();
            }

            _streamSampleRate = sampleRate;
            _streamChannels = channels;
            var samplesPerPacket = Math.Max(1, sampleRate / 100) * channels;
            if (_staging == null || _staging.Length != samplesPerPacket)
            {
                _staging = new float[samplesPerPacket];
                _stagingSamples = 0;
            }

            if (Interlocked.Exchange(ref _pendingDiscontinuity, 0) != 0)
            {
                ResetStaging();
                _markNextPacketDiscontinuous = true;
            }

            var sourceOffset = 0;
            var droppedOldFrames = false;
            while (sourceOffset < usableSampleCount)
            {
                if (_stagingSamples == 0)
                {
                    var sourceFrameOffset = sourceOffset / channels;
                    _stagingSampleIndex = sampleIndex + (ulong)sourceFrameOffset;
                    _stagingTimestampUs = monotonicMicroseconds
                        + sourceFrameOffset * 1_000_000L / sampleRate;
                }

                var copyCount = Math.Min(samplesPerPacket - _stagingSamples, usableSampleCount - sourceOffset);
                interleavedSamples.Slice(sourceOffset, copyCount)
                    .CopyTo(_staging.AsSpan(_stagingSamples));
                sourceOffset += copyCount;
                _stagingSamples += copyCount;

                if (_stagingSamples != samplesPerPacket) continue;

                var packet = BuildPacket(
                    _staging,
                    sampleRate,
                    channels,
                    _stagingSampleIndex,
                    _stagingTimestampUs,
                    _markNextPacketDiscontinuous
                        ? PcmAudioFrameFlags.Discontinuity
                        : PcmAudioFrameFlags.None);
                _markNextPacketDiscontinuous = false;

                if (_queue.Count >= _maxFrames)
                {
                    _queue.Dequeue();
                    droppedOldFrames = true;
                }

                _queue.Enqueue(packet);
                _stagingSamples = 0;
            }

            if (droppedOldFrames)
            {
                Interlocked.Increment(ref _discontinuityId);
                if (_queue.TryPeek(out var first))
                    first[5] |= (byte)PcmAudioFrameFlags.Discontinuity;
            }

            SignalDataAvailable();
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public AudioClockSnapshot GetClock() => new(
        (ulong)Math.Max(0, Volatile.Read(ref _sampleIndex)),
        Volatile.Read(ref _sampleRate),
        Volatile.Read(ref _monotonicMs),
        Volatile.Read(ref _discontinuityId));

    internal bool TryRead(PcmAudioSubscription subscription, out byte[] packet)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(subscription, _subscriber) || !_queue.TryDequeue(out packet!))
            {
                packet = Array.Empty<byte>();
                return false;
            }

            if (_queue.Count > 0) SignalDataAvailable();
            return true;
        }
    }

    internal async ValueTask<byte[]> ReadAsync(
        PcmAudioSubscription subscription,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryRead(subscription, out var packet)) return packet;
            if (subscription.IsEnded || !IsActive(subscription))
                throw new EndOfStreamException("PCM audio stream ended");

            await subscription.WaitForDataAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<byte[]?> ReadAsync(
        PcmAudioSubscription subscription,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryRead(subscription, out var packet)) return packet;
            if (subscription.IsEnded || !IsActive(subscription))
                throw new EndOfStreamException("PCM audio stream ended");
            if (!await subscription.WaitForDataAsync(timeout, cancellationToken).ConfigureAwait(false))
                return null;
        }
    }

    public static byte[] BuildKeepAlivePacket(AudioClockSnapshot clock)
    {
        var packet = new byte[HeaderSize];
        "XPCM"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] = (byte)PcmAudioFrameFlags.KeepAlive;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), checked((uint)Math.Max(0, clock.SampleRate)));
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(16, 8), clock.SampleIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24, 8), checked((ulong)Math.Max(0, clock.MonotonicMs * 1000)));
        return packet;
    }

    private bool IsActive(PcmAudioSubscription subscription)
    {
        lock (_gate) return ReferenceEquals(subscription, _subscriber);
    }

    internal int GetBufferedFrameCount(PcmAudioSubscription subscription)
    {
        lock (_gate)
            return ReferenceEquals(subscription, _subscriber) ? _queue.Count : 0;
    }

    internal void Unsubscribe(PcmAudioSubscription subscription)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(subscription, _subscriber)) return;
            _subscriber = null;
            ResetStreamState();
            SignalDataAvailable();
        }
    }

    public void EndStream()
    {
        PcmAudioSubscription? ended;
        lock (_gate)
        {
            ended = _subscriber;
            _subscriber = null;
            ResetStreamState();
        }
        ended?.MarkEnded();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        EndStream();
    }

    private static byte[] BuildPacket(
        ReadOnlySpan<float> samples,
        int sampleRate,
        int channels,
        ulong sampleIndex,
        long monotonicTimestampUs,
        PcmAudioFrameFlags flags)
    {
        var packet = new byte[HeaderSize + samples.Length * sizeof(float)];
        "XPCM"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] = (byte)flags;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6, 2), checked((ushort)channels));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            checked((uint)(samples.Length / channels)));
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(16, 8), sampleIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(
            packet.AsSpan(24, 8),
            checked((ulong)Math.Max(0, monotonicTimestampUs)));
        MemoryMarshal.AsBytes(samples).CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    private void MarkPendingDiscontinuity()
    {
        if (Interlocked.Exchange(ref _pendingDiscontinuity, 1) == 0)
            Interlocked.Increment(ref _discontinuityId);
    }

    private void SignalDataAvailable()
    {
        _subscriber?.SignalDataAvailable();
    }

    private void ResetStreamState()
    {
        _queue.Clear();
        ResetStaging();
        _streamSampleRate = 0;
        _streamChannels = 0;
        _markNextPacketDiscontinuous = false;
        Interlocked.Exchange(ref _pendingDiscontinuity, 0);
    }

    private void ResetStaging()
    {
        _stagingSamples = 0;
        _stagingSampleIndex = 0;
        _stagingTimestampUs = 0;
    }
}

public sealed class PcmAudioSubscription : IDisposable
{
    private PcmAudioBroadcaster? _owner;
    private readonly SemaphoreSlim _dataAvailable = new(0, 1);
    private int _ended;

    internal PcmAudioSubscription(PcmAudioBroadcaster owner)
    {
        _owner = owner;
    }

    public int BufferedFrameCount =>
        Volatile.Read(ref _owner)?.GetBufferedFrameCount(this) ?? 0;

    internal bool IsEnded => Volatile.Read(ref _ended) != 0;

    internal Task WaitForDataAsync(CancellationToken cancellationToken) =>
        _dataAvailable.WaitAsync(cancellationToken);

    internal Task<bool> WaitForDataAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _dataAvailable.WaitAsync(timeout, cancellationToken);

    internal void SignalDataAvailable()
    {
        if (_dataAvailable.CurrentCount == 0)
        {
            try { _dataAvailable.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    public bool TryRead(out byte[] packet)
    {
        var owner = Volatile.Read(ref _owner);
        if (owner == null)
        {
            packet = Array.Empty<byte>();
            return false;
        }

        return owner.TryRead(this, out packet);
    }

    public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        var owner = Volatile.Read(ref _owner)
            ?? throw new ObjectDisposedException(nameof(PcmAudioSubscription));
        return owner.ReadAsync(this, cancellationToken);
    }

    public ValueTask<byte[]?> ReadAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var owner = Volatile.Read(ref _owner)
            ?? throw new ObjectDisposedException(nameof(PcmAudioSubscription));
        return owner.ReadAsync(this, timeout, cancellationToken);
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        Interlocked.Exchange(ref _ended, 1);
        SignalDataAvailable();
        owner?.Unsubscribe(this);
    }

    internal void MarkEnded()
    {
        Interlocked.Exchange(ref _owner, null);
        Interlocked.Exchange(ref _ended, 1);
        SignalDataAvailable();
    }
}
