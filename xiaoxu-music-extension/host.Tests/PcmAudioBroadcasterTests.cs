using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using xiaoxu_music_bridge.Audio;
using xiaoxu_music_bridge.Common;

internal static class PcmAudioBroadcasterTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        EmitsFixedTenMillisecondFrames();
        PermitsOneSubscriberAndReconnect();
        DropsOldFramesAndMarksDiscontinuity();
        FormatChangeMarksNextFrameAsDiscontinuous();
        DoesNotBufferWithoutSubscriber();
        ValidatesStreamOrigins();
        RejectsUnavailableOrNonFloatCapture();
        SilentBeatResponseIncludesAudioClock();
    }

    internal static async Task ReadAsyncEndsWhenStreamEnds()
    {
        using var broadcaster = new PcmAudioBroadcaster();
        Equal(true, broadcaster.TrySubscribe(out var subscription));
        var pendingRead = subscription!.ReadAsync().AsTask();

        broadcaster.EndStream();

        try
        {
            await pendingRead.WaitAsync(TimeSpan.FromSeconds(1));
            throw new InvalidOperationException("PCM read should end when capture stops");
        }
        catch (EndOfStreamException)
        {
        }

        Equal(true, broadcaster.TrySubscribe(out var reconnected));
        reconnected!.Dispose();
        subscription.Dispose();
    }

    private static void RejectsUnavailableOrNonFloatCapture()
    {
        Equal(false, AudioCaptureFormatPolicy.IsSupported(false, 32, 8, 2));
        Equal(false, AudioCaptureFormatPolicy.IsSupported(true, 16, 4, 2));
        Equal(false, AudioCaptureFormatPolicy.IsSupported(true, 32, 6, 2));
        Equal(true, AudioCaptureFormatPolicy.IsSupported(true, 32, 8, 2));
        Equal(503, AudioStreamAvailabilityPolicy.StatusCode(false, false));
        Equal(503, AudioStreamAvailabilityPolicy.StatusCode(true, false));
        Equal(200, AudioStreamAvailabilityPolicy.StatusCode(true, true));
    }

    private static void EmitsFixedTenMillisecondFrames()
    {
        using var broadcaster = new PcmAudioBroadcaster();
        Equal(true, broadcaster.TrySubscribe(out var subscription));
        using (subscription!)
        {
            var active = subscription!;
            var samples = Enumerable.Range(0, 20).Select(i => i + 0.25f).ToArray();
            broadcaster.Publish(samples.AsSpan(0, 8), 1000, 2, 1_000_000);
            broadcaster.Publish(samples.AsSpan(8), 1000, 2, 1_004_000);

            Equal(true, active.TryRead(out var packet));
            Equal(32 + samples.Length * sizeof(float), packet.Length);
            Equal("XPCM", System.Text.Encoding.ASCII.GetString(packet, 0, 4));
            Equal((byte)1, packet[4]);
            Equal((byte)0, packet[5]);
            Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6, 2)));
            Equal((uint)1000, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4)));
            Equal((uint)10, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4)));
            Equal((ulong)0, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(16, 8)));
            Equal((ulong)1_000_000, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(24, 8)));
            SequenceEqual(samples, MemoryMarshal.Cast<byte, float>(packet.AsSpan(32)).ToArray());
        }
    }

    private static void PermitsOneSubscriberAndReconnect()
    {
        using var broadcaster = new PcmAudioBroadcaster();
        Equal(true, broadcaster.TrySubscribe(out var first));
        Equal(false, broadcaster.TrySubscribe(out var rejected));
        Equal<PcmAudioSubscription?>(null, rejected);
        first!.Dispose();
        Equal(true, broadcaster.TrySubscribe(out var reconnected));
        reconnected!.Dispose();
    }

    private static void DropsOldFramesAndMarksDiscontinuity()
    {
        using var broadcaster = new PcmAudioBroadcaster(250);
        Equal(true, broadcaster.TrySubscribe(out var subscription));
        using (subscription!)
        {
            var active = subscription!;
            broadcaster.Publish(new float[300], 1000, 1, 2_000_000);
            Equal(25, active.BufferedFrameCount);
            Equal(true, active.TryRead(out var packet));
            Equal((byte)PcmAudioFrameFlags.Discontinuity, packet[5]);
            Equal((ulong)50, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(16, 8)));

            var clock = broadcaster.GetClock();
            Equal((ulong)300, clock.SampleIndex);
            Equal(1000, clock.SampleRate);
            Equal(1L, clock.DiscontinuityId);
        }
    }

    private static void DoesNotBufferWithoutSubscriber()
    {
        using var broadcaster = new PcmAudioBroadcaster();
        broadcaster.Publish(new float[200], 1000, 2, 3_000_000);
        Equal(0, broadcaster.BufferedFrameCount);
        Equal((ulong)100, broadcaster.GetClock().SampleIndex);
    }

    private static void FormatChangeMarksNextFrameAsDiscontinuous()
    {
        using var broadcaster = new PcmAudioBroadcaster();
        Equal(true, broadcaster.TrySubscribe(out var subscription));
        using (subscription!)
        {
            var active = subscription!;
            broadcaster.Publish(new float[5], 1000, 1, 4_000_000);
            broadcaster.Publish(new float[20], 2000, 1, 4_005_000);
            Equal(true, active.TryRead(out var packet));
            Equal((byte)PcmAudioFrameFlags.Discontinuity, packet[5]);
            Equal(1L, broadcaster.GetClock().DiscontinuityId);
        }
    }

    private static void ValidatesStreamOrigins()
    {
        Equal(true, AudioStreamOriginPolicy.IsAllowed(null));
        Equal(true, AudioStreamOriginPolicy.IsAllowed("https://xiaoxu.xin"));
        Equal(false, AudioStreamOriginPolicy.IsAllowed("https://music.xiaoxu.xin"));
        Equal(false, AudioStreamOriginPolicy.IsAllowed("http://localhost:4173"));
        Equal(false, AudioStreamOriginPolicy.IsAllowed("http://127.0.0.1:9000"));
        Equal(true, AudioStreamOriginPolicy.IsAllowed("http://localhost:5173"));
        Equal(true, AudioStreamOriginPolicy.IsAllowed("http://127.0.0.1:3000"));
        Equal(false, AudioStreamOriginPolicy.IsAllowed("https://xiaoxu.xin.example"));
        Equal(false, AudioStreamOriginPolicy.IsAllowed("https://evilxiaoxu.xin"));
        Equal(false, AudioStreamOriginPolicy.IsAllowed("https://localhost.evil.example"));
        Equal(false, AudioStreamOriginPolicy.IsAllowed("file://xiaoxu.xin"));
    }

    private static void SilentBeatResponseIncludesAudioClock()
    {
        using var document = JsonDocument.Parse(BeatHttpResponsePolicy.SelectJson(null));
        var clock = document.RootElement.GetProperty("audioClock");
        Equal((ulong)0, clock.GetProperty("sampleIndex").GetUInt64());
        Equal(0, clock.GetProperty("sampleRate").GetInt32());
        Equal(0L, clock.GetProperty("monotonicMs").GetInt64());
        Equal(0L, clock.GetProperty("discontinuityId").GetInt64());
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"PCM test expected {expected}, got {actual}");
    }

    private static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("PCM test sequences differ");
    }
}
