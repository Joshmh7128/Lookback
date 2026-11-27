using System.Runtime.InteropServices;
using Lookback.Core.Time;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Lookback.Encode;

/// <summary>What the AAC track is. Bitrate in bits per second: 128, 160 or 192 kbps (what the Microsoft encoder offers).</summary>
public sealed record AacEncoderSettings(int BitrateBps = 160_000)
{
    /// <summary>The track's role in the session manifest: "game" or "mic".</summary>
    public string Role { get; init; } = "game";

    /// <summary>
    /// How far the input's QPC may drift from the sample count before the
    /// encoder inserts silence (input late) or drops samples (input early).
    /// </summary>
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromMilliseconds(20);
}

/// <summary>
/// The Microsoft AAC encoder MFT, synchronous, AAC-LC 48 kHz stereo. Takes
/// 48 kHz stereo float (what <c>WasapiSource</c> delivers), converts to
/// PCM16, and emits one raw access unit per 1024 samples to an
/// <see cref="IPacketSink"/>, announcing <see cref="TrackInfo.AacAudio"/> with
/// the encoder's AudioSpecificConfig before the first packet.
///
/// Timing: the first call's QPC anchors the track, and packet times come from
/// the sample count, so AAC frames are contiguous as MP4 needs. Input that
/// arrives later than the count says (a capture gap, a device switch) is
/// preceded by silence; input that runs ahead is trimmed. Decoded audio lines
/// up with the QPC it was captured at (see <see cref="EncoderDelay"/>).
/// Allocation-free per call after the first packet; one thread.
/// </summary>
public sealed unsafe class MfAacEncoder : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int FrameSamples = AacConfig.FrameSamples;

    /// <summary>
    /// Priming samples the encoder's output carries before the signal. The
    /// Microsoft encoder emits none: its first access unit decodes to the
    /// first input samples (<c>AacEncoderTests</c> checks the onset to within
    /// a millisecond), so packet k starts at sample k * 1024. Kept as a
    /// constant so another encoder's delay has one place to go.
    /// </summary>
    public const int EncoderDelay = 0;

    private const long HnsPerSecond = 10_000_000;
    private const int PoolSize = 4;
    private const int NotAccepting = unchecked((int)0xC00D36B5);
    private static readonly Guid UserData = new("B6BC765F-4C3B-40A4-BD51-2535B66FE09D");
    private static readonly Guid AacProfileLevel = new("7632F0E6-9538-4D61-ACDA-EA29C8C14456");

    private readonly IPacketSink _sink;
    private readonly int _trackId;
    private readonly long _freq;
    private readonly long _tolerance;
    private readonly IMFTransform _mft;
    private readonly InputSlot[] _pool = new InputSlot[PoolSize];
    private readonly IMFSample _outSample;
    private readonly IMFMediaBuffer _outBuffer;
    private readonly short[] _pcm = new short[FrameSamples * Channels];
    private byte[] _scratch = new byte[8192];
    private TrackInfo? _track;
    private int _fill;
    private int _next;
    private long _t0 = long.MinValue;
    private long _timeline;     // samples placed on the timeline (input plus inserted silence)
    private long _submitted;    // samples handed to the MFT
    private long _packetsOut;
    private bool _disposed;

    private MfAacEncoder(AacEncoderSettings settings, IPacketSink sink, int trackId, long qpcFrequency, IMFTransform mft, string name)
    {
        Settings = settings;
        Name = name;
        _sink = sink;
        _trackId = trackId;
        _freq = qpcFrequency;
        _tolerance = QpcTime.FromTimeSpan(settings.Tolerance, SampleRate);
        _mft = mft;

        using (IMFMediaType output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MfGuids.MajorType, MfGuids.Audio);
            output.Set(MfGuids.Subtype, MfGuids.Aac);
            output.Set(MfGuids.AudioBitsPerSample, 16u);
            output.Set(MfGuids.AudioSampleRate, (uint)SampleRate);
            output.Set(MfGuids.AudioChannels, (uint)Channels);
            output.Set(MfGuids.AudioAvgBytesPerSecond, (uint)(settings.BitrateBps / 8));
            output.Set(MfGuids.AacPayloadType, 0u);       // raw access units, no ADTS
            output.Set(AacProfileLevel, 0x29u);           // AAC-LC, level 2
            mft.SetOutputType(0, output, 0);
        }

        using (IMFMediaType input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MfGuids.MajorType, MfGuids.Audio);
            input.Set(MfGuids.Subtype, MfGuids.Pcm);
            input.Set(MfGuids.AudioBitsPerSample, 16u);
            input.Set(MfGuids.AudioSampleRate, (uint)SampleRate);
            input.Set(MfGuids.AudioChannels, (uint)Channels);
            input.Set(MfGuids.AudioBlockAlignment, (uint)(Channels * 2));
            input.Set(MfGuids.AudioAvgBytesPerSecond, (uint)(SampleRate * Channels * 2));
            mft.SetInputType(0, input, 0);
        }

        AudioSpecificConfig = ReadAudioSpecificConfig() ?? AacConfig.AudioSpecificConfig(SampleRate, Channels);

        OutputStreamInfo info = mft.GetOutputStreamInfo(0);
        if ((info.Flags & MfGuids.ProvidesSamples) != 0)
        {
            throw new NotSupportedException($"{name} allocates its own output samples; expected the Microsoft AAC encoder.");
        }

        _outSample = MediaFactory.MFCreateSample();
        _outBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 8192));
        _outSample.AddBuffer(_outBuffer);
        for (int i = 0; i < PoolSize; i++)
        {
            _pool[i] = new InputSlot(FrameSamples * Channels * 2);
        }

        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    public AacEncoderSettings Settings { get; }

    /// <summary>The MFT's friendly name, e.g. "Microsoft AAC Audio Encoder MFT".</summary>
    public string Name { get; }

    /// <summary>The track's AudioSpecificConfig (from <c>MF_MT_USER_DATA</c>, else built).</summary>
    public byte[] AudioSpecificConfig { get; }

    /// <summary>Samples per channel placed on the timeline, inserted silence included.</summary>
    public long SamplesIn => _timeline;

    public long PacketsOut => Interlocked.Read(ref _packetsOut);

    /// <summary>Samples of silence inserted where the input arrived late.</summary>
    public long SilenceInserted { get; private set; }

    /// <summary>Samples dropped where the input ran ahead of its clock.</summary>
    public long SamplesDropped { get; private set; }

    /// <summary>Creates the encoder. Starts Media Foundation if needed.</summary>
    public static MfAacEncoder Create(AacEncoderSettings settings, IPacketSink sink, int trackId, long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        if (settings.BitrateBps is not (128_000 or 160_000 or 192_000))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.BitrateBps, "AAC bitrate must be 128, 160 or 192 kbps.");
        }

        MediaFoundation.Startup();
        var input = new RegisterTypeInfo { GuidMajorType = MfGuids.Audio, GuidSubtype = MfGuids.Pcm };
        var output = new RegisterTypeInfo { GuidMajorType = MfGuids.Audio, GuidSubtype = MfGuids.Aac };
        MediaFactory.MFTEnumEx(MfGuids.CategoryAudioEncoder, MfGuids.EnumSync | MfGuids.EnumSortAndFilter, input, output, out nint array, out uint count);
        if (array == 0 || count == 0)
        {
            throw new InvalidOperationException("No AAC encoder MFT is registered (Media Foundation missing, e.g. a Windows N edition without the Media Feature Pack).");
        }

        List<Exception> failures = [];
        try
        {
            for (int i = 0; i < count; i++)
            {
                using var activate = new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                string name = "AAC encoder";
                try
                {
                    name = activate.GetAllocatedString(MfGuids.FriendlyName) ?? name;
                }
                catch (SharpGenException)
                {
                }

                IMFTransform? mft = null;
                try
                {
                    mft = activate.ActivateObject<IMFTransform>(typeof(IMFTransform).GUID);
                    return new MfAacEncoder(settings, sink, trackId, qpcFrequency, mft, name);
                }
                catch (SharpGenException ex)
                {
                    mft?.Dispose();
                    failures.Add(new InvalidOperationException($"{name}: {ex.Message}", ex));
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(array);
        }

        throw new AggregateException("No AAC encoder accepted 48 kHz stereo PCM16.", failures);
    }

    /// <summary>
    /// Adds 48 kHz interleaved stereo float whose first frame was at
    /// <paramref name="qpc"/>. Encodes every full 1024-sample frame.
    /// </summary>
    public void Encode(ReadOnlySpan<float> stereo, long qpc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int frames = stereo.Length / Channels;
        if (frames == 0)
        {
            return;
        }

        if (_t0 == long.MinValue)
        {
            _t0 = qpc;
        }

        // Keep the sample count on the clock: silence for a late start, trim an early one.
        long expected = _t0 + QpcTime.Rescale(_timeline, SampleRate, _freq);
        long drift = QpcTime.Rescale(qpc - expected, _freq, SampleRate);
        if (drift > _tolerance)
        {
            SilenceInserted += drift;
            for (long i = 0; i < drift; i++)
            {
                Push(0, 0);
            }
        }
        else if (drift < -_tolerance)
        {
            int skip = (int)Math.Min(-drift, frames);
            SamplesDropped += skip;
            stereo = stereo[(skip * Channels)..];
            frames -= skip;
        }

        for (int f = 0; f < frames; f++)
        {
            Push(stereo[2 * f], stereo[(2 * f) + 1]);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_fill > 0)
            {
                Array.Clear(_pcm, _fill * Channels, _pcm.Length - (_fill * Channels));
                _fill = FrameSamples;
                Submit();
            }

            _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
            _mft.ProcessMessage(TMessageType.MessageCommandDrain, 0);
            Drain();
            _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        finally
        {
            foreach (InputSlot slot in _pool)
            {
                slot?.Dispose();
            }

            _outBuffer.Dispose();
            _outSample.Dispose();
            _mft.Dispose();
        }
    }

    private void Push(float l, float r)
    {
        _pcm[2 * _fill] = ToPcm16(l);
        _pcm[(2 * _fill) + 1] = ToPcm16(r);
        _timeline++;
        if (++_fill == FrameSamples)
        {
            Submit();
        }
    }

    private static short ToPcm16(float s) => (short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f);

    private void Submit()
    {
        InputSlot slot = _pool[_next];
        _next = (_next + 1) % PoolSize;
        slot.Buffer.Lock(out nint ptr, out _, out _);
        MemoryMarshal.AsBytes(_pcm.AsSpan()).CopyTo(new Span<byte>((void*)ptr, _pcm.Length * 2));
        slot.Buffer.Unlock();
        slot.Buffer.CurrentLength = _pcm.Length * 2;
        slot.Sample.SampleTime = QpcTime.Rescale(_submitted, SampleRate, HnsPerSecond);
        slot.Sample.SampleDuration = QpcTime.Rescale(FrameSamples, SampleRate, HnsPerSecond);
        _submitted += FrameSamples;
        _fill = 0;

        int hr = ProcessInput(slot.Sample);
        if (hr == NotAccepting)
        {
            Drain();
            hr = ProcessInput(slot.Sample);
        }

        if (hr < 0)
        {
            throw new InvalidOperationException($"{Name}: ProcessInput failed 0x{hr:X8}.");
        }

        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            _outBuffer.CurrentLength = 0;
            var data = new OutputBuffer { StreamId = 0, Sample = _outSample.NativePointer };
            int status;
            int hr = ProcessOutput(&data, &status);
            if (data.Events != 0)
            {
                Marshal.Release(data.Events);
            }

            if (hr == MfGuids.NeedMoreInput)
            {
                return;
            }

            if (hr < 0)
            {
                throw new InvalidOperationException($"{Name}: ProcessOutput failed 0x{hr:X8}.");
            }

            Emit();
        }
    }

    private void Emit()
    {
        _outBuffer.Lock(out nint ptr, out _, out int length);
        try
        {
            if (length == 0)
            {
                return;
            }

            if (_track is null)
            {
                _track = TrackInfo.AacAudio(_trackId, SampleRate, Channels, Settings.Role) with { CodecConfig = AudioSpecificConfig };
                _sink.OnTrack(_track);
            }

            if (_scratch.Length < length)
            {
                _scratch = new byte[length * 2];
            }

            new ReadOnlySpan<byte>((void*)ptr, length).CopyTo(_scratch);

            // Frame k decodes to timeline samples [k*1024 - delay, (k+1)*1024 - delay).
            long k = _packetsOut;
            long start = (k * FrameSamples) - EncoderDelay;
            long pts = _t0 + QpcTime.Rescale(start, SampleRate, _freq);
            long end = _t0 + QpcTime.Rescale(start + FrameSamples, SampleRate, _freq);
            _sink.OnPacket(new PacketInfo(_trackId, pts, pts, end - pts, IsKeyframe: true), _scratch.AsSpan(0, length));
        }
        finally
        {
            _outBuffer.Unlock();
            Interlocked.Increment(ref _packetsOut);
        }
    }

    private byte[]? ReadAudioSpecificConfig()
    {
        // MF_MT_USER_DATA is the tail of HEAACWAVEINFO (12 bytes) followed by the AudioSpecificConfig.
        try
        {
            using IMFMediaType current = _mft.GetOutputCurrentType(0);
            int size = (int)current.GetBlobSize(UserData);
            if (size <= 12 + 1)
            {
                return null;
            }

            byte[] blob = new byte[size];
            current.GetBlob(UserData, blob);
            return blob[12..];
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    // IMFTransform::ProcessInput (slot 24) and ProcessOutput (slot 25), called raw so the hot path allocates nothing.
    private int ProcessInput(IMFSample sample)
    {
        nint p = _mft.NativePointer;
        return ((delegate* unmanaged[Stdcall]<nint, int, nint, int, int>)(*(void***)p)[24])(p, 0, sample.NativePointer, 0);
    }

    private int ProcessOutput(OutputBuffer* data, int* status)
    {
        nint p = _mft.NativePointer;
        return ((delegate* unmanaged[Stdcall]<nint, int, int, OutputBuffer*, int*, int>)(*(void***)p)[25])(p, 0, 1, data, status);
    }

    /// <summary><c>MFT_OUTPUT_DATA_BUFFER</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct OutputBuffer
    {
        public int StreamId;
        public nint Sample;
        public int Status;
        public nint Events;
    }

    /// <summary>One pooled PCM16 input: a memory buffer in a sample, reused every lap.</summary>
    private sealed class InputSlot : IDisposable
    {
        public InputSlot(int bytes)
        {
            Buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
            Sample = MediaFactory.MFCreateSample();
            Sample.AddBuffer(Buffer);
        }

        public IMFMediaBuffer Buffer { get; }

        public IMFSample Sample { get; }

        public void Dispose()
        {
            Sample.Dispose();
            Buffer.Dispose();
        }
    }
}
