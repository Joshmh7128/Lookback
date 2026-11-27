using System.Runtime.InteropServices;
using Lookback.Capture.Gpu;
using Lookback.Core.Time;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using DxgiFormat = Vortice.DXGI.Format;

namespace Lookback.Encode;

/// <summary>What to encode. Bitrate in bits per second.</summary>
public sealed record VideoEncoderSettings(CodecKind Codec, int Width, int Height, int Fps, long BitrateBps)
{
    /// <summary>Keyframe interval. Segments and clips cut on keyframes, so this bounds clip-start precision.</summary>
    public TimeSpan Gop { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Try hardware MFTs on the capture adapter first. False forces the software encoder.</summary>
    public bool PreferHardware { get; init; } = true;

    /// <summary>
    /// Experimental, off by default: run the encoder on its own D3D11 device
    /// on the same adapter and hand frames over through a shared keyed-mutex
    /// texture. In live WGC capture on AETHER this wedged the capture device
    /// on both NVIDIA and AMD at the first handoff (the unit tests, without
    /// WGC, pass), so the shared capture device plus the encode watchdog is
    /// the default until the handoff is reworked.
    /// </summary>
    public bool IsolateDevice { get; init; }

    /// <summary>When the requested codec has no working encoder, try hardware H.264, then software H.264.</summary>
    public bool AllowCodecFallback { get; init; } = true;
}

/// <summary>
/// Media Foundation video encoder for frames from the capture device. Picks
/// the hardware MFT (NVENC, AMF, QSV) that belongs to the capture adapter's
/// LUID and runs it asynchronously with a DXGI device manager, so NV12
/// textures go in without leaving the GPU. Falls back to the synchronous
/// software H.264 MFT. By default the encoder gets its own device
/// (<see cref="VideoEncoderSettings.IsolateDevice"/>); frames cross through a
/// shared texture whose keyed-mutex waits time out instead of blocking.
/// No B-frames, so output order is input order and DTS = PTS.
/// Output is length-prefixed and goes to an <see cref="IPacketSink"/>;
/// the track (with <c>avcC</c>/<c>hvcC</c> built from the in-band
/// parameter sets) is announced on the first keyframe.
/// </summary>
public sealed class MfVideoEncoder : IVideoEncoder
{
    private const int PoolSize = 8;
    private const int MaxInFlight = PoolSize - 2;
    private static readonly long HnsPerSecond = 10_000_000;

    private readonly GpuDevice _gpu;
    private readonly bool _ownsDevice;
    private readonly Handoff? _handoff;
    private readonly IPacketSink _sink;
    private readonly int _trackId;
    private readonly long _qpcFrequency;
    private readonly IMFTransform _mft;
    private readonly IMFDXGIDeviceManager _manager;
    private readonly CodecApi? _codecApi;
    private readonly IMFMediaEventGenerator? _events;
    private readonly Thread? _eventThread;
    private readonly Slot[] _pool = new Slot[PoolSize];
    private readonly SemaphoreSlim _needInput = new(0, int.MaxValue);
    private readonly ManualResetEventSlim _drained = new(false);
    private readonly Lock _outputLock = new();
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private readonly long _frameHns;
    private byte[] _scratch = new byte[1 << 20];
    private byte[]? _sequenceHeader;
    private TrackInfo? _track;
    private int _next;
    private long _submitted;
    private long _emitted;
    private long _dropped;
    private long _handoffTimeouts;
    private int _inCall;
    private int _closed;
    private volatile bool _disposed;
    private volatile EncodePhase _phase;
    private volatile EncodePhase _outputPhase;

    private MfVideoEncoder(GpuDevice gpu, bool ownsDevice, Handoff? handoff, VideoEncoderSettings settings, EncoderCandidate candidate, IPacketSink sink, int trackId, long qpcFrequency, IMFTransform mft)
    {
        _gpu = gpu;
        _ownsDevice = ownsDevice;
        _handoff = handoff;
        _sink = sink;
        _trackId = trackId;
        _qpcFrequency = qpcFrequency;
        _mft = mft;
        Settings = settings;
        Candidate = candidate;
        _frameHns = HnsPerSecond / settings.Fps;

        _manager = MediaFactory.MFCreateDXGIDeviceManager();
        _manager.ResetDevice(gpu.Device).CheckError();

        if (candidate.Hardware)
        {
            using IMFAttributes attrs = mft.Attributes;
            attrs.Set(MfGuids.TransformAsyncUnlock, 1u);
            mft.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)_manager.NativePointer);
        }

        _codecApi = CodecApi.From(mft);
        Configure(settings);

        OutputStreamInfo info = mft.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & MfGuids.ProvidesSamples) != 0;
        _outputSize = Math.Max(info.Size, settings.Width * settings.Height);

        for (int i = 0; i < PoolSize; i++)
        {
            _pool[i] = new Slot(gpu.Device, settings.Width, settings.Height);
        }

        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);

        if (candidate.Hardware)
        {
            _events = mft.QueryInterface<IMFMediaEventGenerator>();
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "lookback-encode-events", Priority = ThreadPriority.AboveNormal };
            _eventThread.Start();
        }
    }

    public VideoEncoderSettings Settings { get; }

    public EncoderCandidate Candidate { get; }

    /// <summary>The MFT's friendly name, e.g. "NVIDIA H.264 Encoder MFT".</summary>
    public string Name => Candidate.Name;

    public bool IsHardware => Candidate.Hardware;

    /// <summary>True when the encoder runs on its own device (see <see cref="VideoEncoderSettings.IsolateDevice"/>).</summary>
    public bool IsIsolated => _handoff is not null;

    /// <summary>Where the encoding thread and the encoder's event thread are, for stall reports.</summary>
    public string Phase => $"encode {_phase}, events {_outputPhase}";

    /// <summary>Frames the encoder had no room for (it was behind, or the handoff timed out).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Handoff waits that timed out (the encoder side was not done with the shared texture).</summary>
    public long HandoffTimeouts => Interlocked.Read(ref _handoffTimeouts);

    public long FramesIn => Interlocked.Read(ref _submitted);

    public long PacketsOut => Interlocked.Read(ref _emitted);

    /// <summary>Frames submitted but not yet out, over the pool size: the governor's encoder load.</summary>
    public double Load => (double)(FramesIn - PacketsOut) / MaxInFlight;

    /// <summary>
    /// The fallback chain for <paramref name="settings"/> on <paramref name="gpu"/>'s
    /// adapter, best first: hardware MFTs for the codec, then (if allowed and
    /// the codec is not H.264) hardware H.264, then software H.264. Entries in
    /// <paramref name="blocklist"/> for this adapter are left out.
    /// MFStartup must have been called (see <see cref="MediaFoundation.Startup"/>).
    /// </summary>
    public static IReadOnlyList<EncoderCandidate> Candidates(GpuDevice gpu, VideoEncoderSettings settings, EncoderBlocklist? blocklist = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(settings);
        List<EncoderCandidate> list = [];
        void AddAll(CodecKind codec, long? luid, bool hardware)
        {
            foreach (string name in List(codec, luid, hardware))
            {
                var c = new EncoderCandidate(codec, name, hardware);
                if (!list.Contains(c) && blocklist?.IsBlocked(gpu.Identity, c) != true)
                {
                    list.Add(c);
                }
            }
        }

        if (settings.PreferHardware && !gpu.IsWarp)
        {
            AddAll(settings.Codec, gpu.Luid, hardware: true);
            if (settings.AllowCodecFallback && settings.Codec != CodecKind.H264)
            {
                AddAll(CodecKind.H264, gpu.Luid, hardware: true);
            }
        }

        if (settings.Codec == CodecKind.H264 || settings.AllowCodecFallback)
        {
            AddAll(CodecKind.H264, luid: null, hardware: false);
        }

        return list;
    }

    /// <summary>Creates the first candidate in <see cref="Candidates"/> that accepts the settings.</summary>
    public static MfVideoEncoder Create(GpuDevice gpu, VideoEncoderSettings settings, IPacketSink sink, int trackId, long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sink);
        List<Exception> failures = [];
        foreach (EncoderCandidate c in Candidates(gpu, settings))
        {
            try
            {
                return Create(gpu, settings, c, sink, trackId, qpcFrequency);
            }
            catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
            {
                failures.Add(new InvalidOperationException($"{c}: {ex.Message}", ex));
            }
        }

        throw new AggregateException($"No encoder accepted {settings.Codec} {settings.Width}x{settings.Height}@{settings.Fps}.", failures);
    }

    /// <summary>
    /// Creates <paramref name="candidate"/> for frames from <paramref name="capture"/>.
    /// The codec is the candidate's (a fallback candidate may differ from <paramref name="settings"/>).
    /// </summary>
    public static MfVideoEncoder Create(GpuDevice capture, VideoEncoderSettings settings, EncoderCandidate candidate, IPacketSink sink, int trackId, long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sink);
        VideoEncoderSettings s = settings with { Codec = candidate.Codec };

        GpuDevice gpu = capture;
        bool owns = false;
        Handoff? handoff = null;
        if (s.IsolateDevice)
        {
            try
            {
                gpu = capture.CreateSibling();
                owns = true;
                handoff = new Handoff(capture, gpu, s.Width, s.Height);
            }
            catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
            {
                // The adapter cannot share NV12 between devices: encode on the capture device.
                handoff?.Dispose();
                handoff = null;
                if (owns)
                {
                    gpu.Dispose();
                }

                gpu = capture;
                owns = false;
            }
        }

        try
        {
            IMFTransform mft = ActivateByName(candidate, candidate.Hardware ? capture.Luid : null);
            try
            {
                return new MfVideoEncoder(gpu, owns, handoff, s, candidate, sink, trackId, qpcFrequency, mft);
            }
            catch
            {
                mft.Dispose();
                throw;
            }
        }
        catch
        {
            handoff?.Dispose();
            if (owns)
            {
                gpu.Dispose();
            }

            throw;
        }
    }

    /// <summary>Lists the encoder MFTs for <paramref name="codec"/> (for the governor probe and the bench).</summary>
    public static IReadOnlyList<string> List(CodecKind codec, long? luid, bool hardware)
    {
        List<string> names = [];
        foreach ((IMFActivate activate, string name) in Enumerate(codec, luid, hardware))
        {
            names.Add(name);
            activate.Dispose();
        }

        return names;
    }

    /// <summary>Asks for an IDR on the next frame (clip start, tier change).</summary>
    public void ForceKeyframe() => _codecApi?.SetUInt32(MfGuids.ForceKeyFrame, 1);

    /// <summary>
    /// Encodes one NV12 frame from the capture device at <paramref name="qpc"/>.
    /// Copies it into the encoder's own pool on the GPU (through the handoff
    /// when isolated), so <paramref name="nv12"/> can be reused right away.
    /// Returns false and counts a drop when the encoder is behind or the
    /// handoff timed out. Call from one thread (the constant-rate ticker).
    /// </summary>
    public bool Encode(ID3D11Texture2D nv12, long qpc)
    {
        ArgumentNullException.ThrowIfNull(nv12);
        if (_disposed)
        {
            return false;
        }

        Interlocked.Increment(ref _inCall);
        try
        {
            if (FramesIn - PacketsOut >= MaxInFlight || (IsHardware && !_needInput.Wait(0)))
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            Slot slot = _pool[_next];
            _next = (_next + 1) % PoolSize;
            if (_handoff is not null)
            {
                _phase = EncodePhase.Handoff;
                if (!_handoff.Transfer(nv12, slot.Texture))
                {
                    Interlocked.Increment(ref _handoffTimeouts);
                    Interlocked.Increment(ref _dropped);
                    if (IsHardware)
                    {
                        _needInput.Release(); // the input request is still open
                    }

                    return false;
                }
            }
            else
            {
                _phase = EncodePhase.WaitingForGpuLock;
                lock (_gpu.Lock)
                {
                    _phase = EncodePhase.Copying;
                    _gpu.Context.CopyResource(slot.Texture, nv12);
                }
            }

            slot.Sample.SampleTime = QpcTime.Rescale(qpc, _qpcFrequency, HnsPerSecond);
            slot.Sample.SampleDuration = _frameHns;
            Interlocked.Increment(ref _submitted);

            try
            {
                _phase = EncodePhase.ProcessInput;
                _mft.ProcessInput(0, slot.Sample, 0);
            }
            catch (SharpGenException)
            {
                Interlocked.Decrement(ref _submitted);
                Interlocked.Increment(ref _dropped);
                return false;
            }

            if (!IsHardware)
            {
                DrainOutput();
            }

            return true;
        }
        finally
        {
            _phase = EncodePhase.Idle;
            Interlocked.Decrement(ref _inCall);
        }
    }

    /// <summary>Drains and releases the encoder. Waits at most a few seconds for the MFT; use <see cref="Abandon"/> when it may be stuck.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _disposed = true;
        Release(drain: true);
    }

    public void Abandon()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _disposed = true;
        var reaper = new Thread(() =>
        {
            // Release only once no call is inside the encoder; if one stays stuck, leak rather than free objects it is using.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref _inCall) != 0)
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(30))
                {
                    return;
                }

                Thread.Sleep(50);
            }

            try
            {
                Release(drain: false);
            }
            catch (SharpGenException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        })
        { IsBackground = true, Name = "lookback-encoder-reaper" };
        reaper.Start();
    }

    private void Release(bool drain)
    {
        try
        {
            if (drain)
            {
                _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
                _mft.ProcessMessage(TMessageType.MessageCommandDrain, 0);
                if (IsHardware)
                {
                    _drained.Wait(TimeSpan.FromSeconds(2));
                }
                else
                {
                    DrainOutput();
                }
            }

            _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        catch (SharpGenException)
        {
            // Tearing down anyway.
        }

        if (_events is not null)
        {
            // Unblocks GetEvent on the event thread.
            using IMFShutdown? shutdown = _mft.QueryInterfaceOrNull<IMFShutdown>();
            shutdown?.Shutdown();
            _eventThread!.Join(TimeSpan.FromSeconds(2));
            _events.Dispose();
        }

        foreach (Slot slot in _pool)
        {
            slot?.Dispose();
        }

        _codecApi?.Dispose();
        _mft.Dispose();
        _manager.Dispose();
        _handoff?.Dispose();
        if (_ownsDevice)
        {
            _gpu.Dispose();
        }

        _needInput.Dispose();
        _drained.Dispose();
    }

    private void Configure(VideoEncoderSettings s)
    {
        if (_codecApi is not null)
        {
            _codecApi.SetUInt32(MfGuids.RateControlMode, 0); // eAVEncCommonRateControlMode_CBR
            _codecApi.SetUInt32(MfGuids.MeanBitRate, (uint)s.BitrateBps);
            _codecApi.SetUInt32(MfGuids.GopSize, (uint)Math.Max(1, s.Gop.TotalSeconds * s.Fps));
            _codecApi.SetUInt32(MfGuids.BPictureCount, 0);
            _codecApi.SetBool(MfGuids.LowLatency, true);
        }

        using IMFMediaType output = MediaFactory.MFCreateMediaType();
        output.Set(MfGuids.MajorType, MfGuids.Video);
        output.Set(MfGuids.Subtype, s.Codec == CodecKind.Hevc ? MfGuids.Hevc : MfGuids.H264);
        output.Set(MfGuids.AvgBitrate, (uint)s.BitrateBps);
        output.Set(MfGuids.FrameSize, MfGuids.Pack((uint)s.Width, (uint)s.Height));
        output.Set(MfGuids.FrameRate, MfGuids.Pack((uint)s.Fps, 1));
        output.Set(MfGuids.PixelAspectRatio, MfGuids.Pack(1, 1));
        output.Set(MfGuids.InterlaceMode, 2u); // progressive
        output.Set(MfGuids.Mpeg2Profile, s.Codec == CodecKind.Hevc ? 1u : 100u); // HEVC Main, H.264 High
        _mft.SetOutputType(0, output, 0);

        using IMFMediaType input = MediaFactory.MFCreateMediaType();
        input.Set(MfGuids.MajorType, MfGuids.Video);
        input.Set(MfGuids.Subtype, MfGuids.Nv12);
        input.Set(MfGuids.FrameSize, MfGuids.Pack((uint)s.Width, (uint)s.Height));
        input.Set(MfGuids.FrameRate, MfGuids.Pack((uint)s.Fps, 1));
        input.Set(MfGuids.PixelAspectRatio, MfGuids.Pack(1, 1));
        input.Set(MfGuids.InterlaceMode, 2u);
        _mft.SetInputType(0, input, 0);

        ReadSequenceHeader();
    }

    private void ReadSequenceHeader()
    {
        using IMFMediaType current = _mft.GetOutputCurrentType(0);
        try
        {
            int size = (int)current.GetBlobSize(MfGuids.MpegSequenceHeader);
            if (size > 0)
            {
                byte[] blob = new byte[size];
                current.GetBlob(MfGuids.MpegSequenceHeader, blob);
                _sequenceHeader = blob;
            }
        }
        catch (SharpGenException)
        {
            // Not every encoder publishes it; most put the parameter sets in-band.
        }
    }

    private void EventLoop()
    {
        try
        {
            while (true)
            {
                _outputPhase = EncodePhase.WaitingForEvent;
                using IMFMediaEvent e = _events!.GetEvent(0);
                switch (e.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _needInput.Release();
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        _outputPhase = EncodePhase.ProcessOutput;
                        DrainOutput();
                        break;
                    case MediaEventTypes.TransformDrainComplete:
                        _drained.Set();
                        return;
                }
            }
        }
        catch (SharpGenException)
        {
            // Shut down.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Pulls every available output sample. Async: one per HaveOutput event; sync: until NeedMoreInput.</summary>
    private void DrainOutput()
    {
        lock (_outputLock)
        {
            while (true)
            {
                IMFSample? provided = null;
                if (!_providesSamples)
                {
                    provided = MediaFactory.MFCreateSample();
                    using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(_outputSize);
                    provided.AddBuffer(buffer);
                }

                OutputDataBuffer data = new() { StreamID = 0, Sample = provided };
                Result hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
                if (hr.Code == MfGuids.StreamChange)
                {
                    provided?.Dispose();
                    data.Events?.Dispose();
                    RenegotiateOutput();
                    continue;
                }

                if (hr.Failure)
                {
                    provided?.Dispose();
                    data.Events?.Dispose();
                    return;
                }

                using (IMFSample sample = data.Sample!)
                {
                    Emit(sample);
                }

                data.Events?.Dispose();
                if (IsHardware)
                {
                    return;
                }
            }
        }
    }

    private void RenegotiateOutput()
    {
        using IMFMediaType type = _mft.GetOutputAvailableType(0, 0);
        _mft.SetOutputType(0, type, 0);
        ReadSequenceHeader();
    }

    private unsafe void Emit(IMFSample sample)
    {
        long pts = QpcTime.Rescale(sample.SampleTime, HnsPerSecond, _qpcFrequency);
        long duration = QpcTime.Rescale(_frameHns, HnsPerSecond, _qpcFrequency);
        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out nint ptr, out _, out int length);
        try
        {
            var annexB = new ReadOnlySpan<byte>((void*)ptr, length);
            bool key = CodecConfig.IsKeyframe(Settings.Codec, annexB);
            if (_track is null)
            {
                if (!key)
                {
                    return;
                }

                byte[]? config = CodecConfig.FromKeyframe(Settings.Codec, annexB);
                if (config is null && _sequenceHeader is not null)
                {
                    config = CodecConfig.FromKeyframe(Settings.Codec, _sequenceHeader);
                }

                if (config is null)
                {
                    return;
                }

                _track = TrackInfo.Video(_trackId, Settings.Codec, Settings.Width, Settings.Height) with { CodecConfig = config };
                _sink.OnTrack(_track);
            }

            int max = AnnexB.MaxLengthPrefixedSize(length);
            if (_scratch.Length < max)
            {
                _scratch = new byte[max * 2];
            }

            int n = AnnexB.HasStartCode(annexB) ? AnnexB.ToLengthPrefixed(annexB, _scratch) : Copy(annexB, _scratch);
            _sink.OnPacket(new PacketInfo(_trackId, pts, pts, duration, key), _scratch.AsSpan(0, n));
        }
        finally
        {
            buffer.Unlock();
            Interlocked.Increment(ref _emitted);
        }
    }

    private static int Copy(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        src.CopyTo(dst);
        return src.Length;
    }

    private static IMFTransform ActivateByName(EncoderCandidate candidate, long? luid)
    {
        IMFTransform? found = null;
        foreach ((IMFActivate activate, string name) in Enumerate(candidate.Codec, luid, candidate.Hardware))
        {
            try
            {
                if (found is null && name == candidate.Name)
                {
                    found = activate.ActivateObject<IMFTransform>(typeof(IMFTransform).GUID);
                }
            }
            finally
            {
                activate.Dispose();
            }
        }

        return found ?? throw new InvalidOperationException($"{candidate} is no longer registered.");
    }

    private static List<(IMFActivate Activate, string Name)> Enumerate(CodecKind codec, long? luid, bool hardware)
    {
        var output = new RegisterTypeInfo { GuidMajorType = MfGuids.Video, GuidSubtype = codec == CodecKind.Hevc ? MfGuids.Hevc : MfGuids.H264 };
        var input = new RegisterTypeInfo { GuidMajorType = MfGuids.Video, GuidSubtype = MfGuids.Nv12 };
        uint flags = (hardware ? MfGuids.EnumHardware | MfGuids.EnumAsync : MfGuids.EnumSync) | MfGuids.EnumSortAndFilter;

        using IMFAttributes? attrs = luid is null ? null : MediaFactory.MFCreateAttributes(1);
        if (attrs is not null)
        {
            Span<byte> blob = stackalloc byte[8];
            BitConverter.TryWriteBytes(blob[..4], (uint)(luid!.Value & 0xFFFFFFFF));
            BitConverter.TryWriteBytes(blob[4..], (int)(luid.Value >> 32));
            attrs.SetBlob(MfGuids.EnumAdapterLuid, blob);
        }

        List<(IMFActivate, string)> found = [];
        nint array;
        uint count;
        try
        {
            MediaFactory.MFTEnum2(MfGuids.CategoryVideoEncoder, flags, input, output, attrs!, out array, out count);
        }
        catch (SharpGenException)
        {
            return found;
        }

        if (array == 0)
        {
            return found;
        }

        try
        {
            for (int i = 0; i < count; i++)
            {
                var activate = new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                string name;
                try
                {
                    name = activate.GetAllocatedString(MfGuids.FriendlyName) ?? "encoder";
                }
                catch (SharpGenException)
                {
                    name = hardware ? "hardware encoder" : "software encoder";
                }

                found.Add((activate, name));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(array);
        }

        return found;
    }

    /// <summary>One pooled input: an NV12 texture wrapped once in a DXGI surface buffer and a sample, reused every lap.</summary>
    private sealed class Slot : IDisposable
    {
        public Slot(ID3D11Device device, int width, int height)
        {
            Texture = device.CreateTexture2D(new Texture2DDescription(DxgiFormat.NV12, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            Buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, Texture, 0, false);
            Sample = MediaFactory.MFCreateSample();
            Sample.AddBuffer(Buffer);
        }

        public ID3D11Texture2D Texture { get; }

        public IMFMediaBuffer Buffer { get; }

        public IMFSample Sample { get; }

        public void Dispose()
        {
            Sample.Dispose();
            Buffer.Dispose();
            Texture.Dispose();
        }
    }

    /// <summary>
    /// The frame handoff between the capture device and the encoder's own
    /// device: one NV12 texture created shared with a keyed mutex on the
    /// capture device and opened on the encoder device. Key 0 = capture side
    /// may write, key 1 = encoder side may read. Every wait times out, so a
    /// stuck encoder device costs dropped frames, never a blocked capture thread.
    /// </summary>
    private sealed class Handoff : IDisposable
    {
        private const uint WaitMs = 50;
        private readonly GpuDevice _capture;
        private readonly GpuDevice _encoder;
        private readonly ID3D11Texture2D _onCapture;
        private readonly ID3D11Texture2D _onEncoder;
        private readonly IDXGIKeyedMutex _captureKey;
        private readonly IDXGIKeyedMutex _encoderKey;
        private volatile bool _broken;

        public Handoff(GpuDevice capture, GpuDevice encoder, int width, int height)
        {
            _capture = capture;
            _encoder = encoder;
            _onCapture = capture.Device.CreateTexture2D(new Texture2DDescription(DxgiFormat.NV12, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.SharedKeyedMutex));
            try
            {
                using IDXGIResource resource = _onCapture.QueryInterface<IDXGIResource>();
                _onEncoder = encoder.Device.OpenSharedResource<ID3D11Texture2D>(resource.SharedHandle);
                _captureKey = _onCapture.QueryInterface<IDXGIKeyedMutex>();
                _encoderKey = _onEncoder.QueryInterface<IDXGIKeyedMutex>();
            }
            catch
            {
                _onEncoder?.Dispose();
                _onCapture.Dispose();
                throw;
            }
        }

        /// <summary>Copies <paramref name="src"/> (capture device) into <paramref name="dst"/> (encoder device). False on a timeout.</summary>
        public bool Transfer(ID3D11Texture2D src, ID3D11Texture2D dst)
        {
            if (_broken)
            {
                return false;
            }

            lock (_capture.Lock)
            {
                if (Acquire(_captureKey, 0, WaitMs) != 0)
                {
                    return false;
                }

                _capture.Context.CopyResource(_onCapture, src);
                _captureKey.ReleaseSync(1);
            }

            lock (_encoder.Lock)
            {
                if (Acquire(_encoderKey, 1, WaitMs) != 0)
                {
                    // The key is stuck at 1: the capture side can never write again. Report every later frame as dropped.
                    _broken = true;
                    return false;
                }

                _encoder.Context.CopyResource(dst, _onEncoder);
                _encoderKey.ReleaseSync(0);
            }

            return true;
        }

        /// <summary>
        /// IDXGIKeyedMutex::AcquireSync with its raw HRESULT: WAIT_TIMEOUT (0x102)
        /// and WAIT_ABANDONED (0x80) are success codes, which Vortice's wrapper
        /// cannot tell apart from S_OK. Slot 8: IUnknown (3), IDXGIObject (4),
        /// IDXGIDeviceSubObject (1), then AcquireSync.
        /// </summary>
        private static unsafe int Acquire(IDXGIKeyedMutex mutex, ulong key, uint milliseconds)
        {
            var acquire = (delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)(*(nint**)mutex.NativePointer)[8];
            return acquire(mutex.NativePointer, key, milliseconds);
        }

        public void Dispose()
        {
            _encoderKey.Dispose();
            _captureKey.Dispose();
            _onEncoder.Dispose();
            _onCapture.Dispose();
        }
    }
}

/// <summary>What an encoder thread is doing right now; a phase that never changes while frames are due is a stall.</summary>
public enum EncodePhase
{
    Idle,
    WaitingForGpuLock,
    Converting,
    Copying,
    Handoff,
    ProcessInput,
    WaitingForEvent,
    ProcessOutput,
}

/// <summary>Process-wide Media Foundation startup. Call once before creating encoders.</summary>
public static class MediaFoundation
{
    private static int _started;

    public static void Startup()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            MediaFactory.MFStartup(useLightVersion: false).CheckError();
        }
    }
}
