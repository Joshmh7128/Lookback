using System.Diagnostics;
using System.Runtime.InteropServices;
using Lookback.Audio.Interop;
using Lookback.Core.Time;
using Lookback.Media.Frames;
using Lookback.Media.Live;

namespace Lookback.Audio;

/// <summary>
/// 48 kHz interleaved stereo float, stamped with the raw QPC of its first
/// frame. The span is borrowed for the call. Called on the capture thread:
/// copy or encode, never block.
/// </summary>
public delegate void PcmHandler(ReadOnlySpan<float> stereo, long qpc);

/// <summary>What to capture. Build it from an <see cref="AudioPlan"/>.</summary>
public sealed record AudioSourceOptions
{
    public AudioSourceKind Kind { get; init; } = AudioSourceKind.SystemLoopback;

    /// <summary>The process to isolate for <see cref="AudioSourceKind.ProcessLoopback"/>.</summary>
    public uint ProcessId { get; init; }

    /// <summary>Include the process's children (most games render audio from the main process anyway).</summary>
    public bool IncludeProcessTree { get; init; } = true;

    /// <summary>
    /// When process loopback is unavailable (Windows builds before 20348),
    /// capture the whole default render endpoint instead. False for
    /// official-player sessions, which never get system loopback.
    /// </summary>
    public bool AllowEndpointFallback { get; init; } = true;

    /// <summary>The QPC frequency timestamps are in (the session clock's).</summary>
    public long QpcFrequency { get; init; } = Stopwatch.Frequency;

    /// <summary>WASAPI buffer length. Packets arrive every device period (~10 ms) regardless.</summary>
    public TimeSpan Buffer { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Play silence on the endpoint so loopback keeps delivering while nothing else plays.</summary>
    public bool KeepLoopbackAlive { get; init; } = true;

    /// <summary>Analysis hop format for the hop callback.</summary>
    public AnalysisFormat AnalysisFormat { get; init; } = AnalysisFormat.Default;

    public static AudioSourceOptions ForGame(AudioPlan plan, uint processId = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new AudioSourceOptions
        {
            Kind = plan.Game ?? throw new InvalidOperationException($"This session gets no game audio: {plan.Reason}"),
            ProcessId = processId,
            AllowEndpointFallback = plan.AllowEndpointFallback,
        };
    }
}

/// <summary>
/// One WASAPI capture stream in shared, event-driven mode: endpoint loopback
/// of the default render device, process loopback (build 20348+, otherwise
/// endpoint loopback with <see cref="FallbackReason"/> set, if allowed), or
/// the default microphone. A dedicated MMCSS thread pulls packets, converts
/// <c>u64QPCPosition</c> (100 ns) to raw QPC, smooths it with a
/// <see cref="SampleClock"/> against the device position, and resamples to
/// 48 kHz stereo at the measured device rate so the sample count tracks QPC.
/// Silent packets become zeros, discontinuity flags and glitches reset the
/// clock, and a default-device change (or the device going away) re-opens
/// the stream on the new default while the timeline carries on.
/// Output goes to a <see cref="PcmHandler"/> (the AAC encoder) and, through
/// a <see cref="LiveAudioAssembler"/>, to a hop callback (the live analyzer).
/// Nothing is allocated per packet on the capture thread
/// (<see cref="CaptureThreadAllocatedBytes"/> measures it).
/// </summary>
public sealed unsafe class WasapiSource : IDisposable, IDeviceNotifications
{
    private const int WarmupPackets = 50;
    private static readonly TimeSpan FitBeforeTrim = TimeSpan.FromSeconds(5);

    private readonly AudioSourceOptions _options;
    private readonly PcmHandler? _onPcm;
    private readonly Action<AudioBlock>? _onHop;
    private readonly long _freq;
    private readonly int _flow;
    private readonly StereoResampler _resampler = new();
    private readonly LiveAudioAssembler? _assembler;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _firstOpen = new(false);
    private readonly List<ComCallback> _activations = [];
    private readonly nint _event;
    private readonly nint _wake;

    private nint _enumerator;
    private ComCallback? _notify;
    private nint _client;
    private nint _capture;
    private RenderStream? _keepAlive;
    private SampleClock? _clock;
    private PcmFormat? _format;
    private float[] _float = [];
    private float[] _out = [];
    private bool _firstPacket;
    private long _fitStart;
    private int _lastResets;
    private int _warm;
    private long _allocBase;
    private long _allocPrior;
    private long _allocCurrent;
    private volatile bool _stopping;
    private volatile int _reopen;
    private volatile string? _deviceId;
    private volatile string? _device;
    private volatile string? _lastError;
    private long _packets, _frames, _silent, _discontinuities, _timestampErrors, _deviceChanges, _opens, _invalidations;

    private WasapiSource(AudioSourceOptions options, PcmHandler? onPcm, Action<AudioBlock>? onHop)
    {
        _options = options;
        _onPcm = onPcm;
        _onHop = onHop;
        _freq = options.QpcFrequency;
        Requested = options.Kind;
        Kind = options.Kind;
        if (Kind == AudioSourceKind.ProcessLoopback && !AudioCapturePolicy.ProcessLoopbackSupported(AudioCapturePolicy.OsBuild))
        {
            string why = $"process loopback needs Windows build {AudioCapturePolicy.ProcessLoopbackMinBuild}+, this is {AudioCapturePolicy.OsBuild}";
            if (!options.AllowEndpointFallback)
            {
                throw new NotSupportedException($"{why}, and this session may not fall back to system loopback.");
            }

            Kind = AudioSourceKind.SystemLoopback;
            FallbackReason = $"{why}; capturing the default render endpoint instead";
        }

        if (Kind == AudioSourceKind.ProcessLoopback && options.ProcessId == 0)
        {
            throw new ArgumentException("Process loopback needs a process id.", nameof(options));
        }

        _flow = Kind == AudioSourceKind.Microphone ? Wasapi.FlowCapture : Wasapi.FlowRender;
        if (onHop is not null)
        {
            _assembler = new LiveAudioAssembler(StereoResampler.OutputRate, options.AnalysisFormat, _freq);
        }

        _event = Native.CreateEvent();
        _wake = Native.CreateEvent();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"lookback-audio-{Kind}",
            Priority = ThreadPriority.Highest,
        };
    }

    /// <summary>What was asked for.</summary>
    public AudioSourceKind Requested { get; }

    /// <summary>What is captured (differs from <see cref="Requested"/> after a fallback).</summary>
    public AudioSourceKind Kind { get; }

    /// <summary>Why process loopback fell back to the endpoint, or null.</summary>
    public string? FallbackReason { get; }

    /// <summary>The device's friendly name while open, or null.</summary>
    public string? Device => _device;

    /// <summary>The device stream format while open.</summary>
    public PcmFormat? Format => Volatile.Read(ref _format);

    public string? LastError => _lastError;

    public long Packets => Volatile.Read(ref _packets);

    /// <summary>Device frames received.</summary>
    public long Frames => Volatile.Read(ref _frames);

    public long SilentPackets => Volatile.Read(ref _silent);

    /// <summary>Packets flagged as a discontinuity (a glitch: the engine lost data), after the first.</summary>
    public long Discontinuities => Volatile.Read(ref _discontinuities);

    public long TimestampErrors => Volatile.Read(ref _timestampErrors);

    /// <summary>Default-device changes that re-opened the stream.</summary>
    public long DeviceChanges => Volatile.Read(ref _deviceChanges);

    /// <summary>Times the device went away under us.</summary>
    public long Invalidations => Volatile.Read(ref _invalidations);

    public long Opens => Volatile.Read(ref _opens);

    /// <summary>Clock-fit resets (each open, discontinuity or timing jump).</summary>
    public int ClockResets => _clock?.Resets ?? 0;

    /// <summary>Device frames per second measured against QPC.</summary>
    public double MeasuredRate => _clock?.MeasuredRate ?? 0;

    /// <summary>Silence the hop assembler inserted for gaps, in 48 kHz samples.</summary>
    public long SilenceInserted => _assembler?.SilenceInserted ?? 0;

    /// <summary>Bytes the capture thread allocated per packet after warm-up (should stay 0).</summary>
    public long CaptureThreadAllocatedBytes => Volatile.Read(ref _allocPrior) + Volatile.Read(ref _allocCurrent);

    public string Description =>
        $"{Kind switch { AudioSourceKind.ProcessLoopback => $"process loopback of pid {_options.ProcessId}", AudioSourceKind.Microphone => "microphone", _ => "endpoint loopback" }}"
        + $"{(Device is { } d ? $" of {d}" : "")}{(Format is { } f ? $" ({f})" : "")}";

    /// <summary>
    /// Starts capture and waits for the first open. If no device is there yet
    /// (no mic plugged in), it keeps retrying in the background: check
    /// <see cref="Device"/> and <see cref="LastError"/>.
    /// </summary>
    public static WasapiSource Start(AudioSourceOptions options, PcmHandler? onPcm, Action<AudioBlock>? onHop = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WASAPI is Windows only.");
        }

        var source = new WasapiSource(options, onPcm, onHop);
        source._thread.Start();
        source._firstOpen.Wait(TimeSpan.FromSeconds(10));
        return source;
    }

    /// <summary>Active render and capture endpoints, default first, for the bench and diagnostics.</summary>
    public static IReadOnlyList<AudioDeviceInfo> ListDevices()
    {
        int init = Native.CoInitializeEx(0, Native.CoInitMultithreaded);
        List<AudioDeviceInfo> found = [];
        nint enumerator = Wasapi.CreateEnumerator();
        try
        {
            foreach (int flow in (int[])[Wasapi.FlowRender, Wasapi.FlowCapture])
            {
                string? defaultId = null;
                if (Wasapi.GetDefaultAudioEndpoint(enumerator, flow, Wasapi.RoleConsole, out nint def) >= 0)
                {
                    defaultId = Wasapi.GetId(def);
                    Marshal.Release(def);
                }

                if (Wasapi.EnumAudioEndpoints(enumerator, flow, Wasapi.DeviceStateActive, out nint coll) < 0)
                {
                    continue;
                }

                Wasapi.CollectionCount(coll, out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (Wasapi.CollectionItem(coll, i, out nint dev) >= 0)
                    {
                        string? id = Wasapi.GetId(dev);
                        found.Add(new AudioDeviceInfo(flow == Wasapi.FlowRender, Wasapi.GetFriendlyName(dev) ?? "(unnamed)", id ?? "", id is not null && id == defaultId));
                        Marshal.Release(dev);
                    }
                }

                Marshal.Release(coll);
            }
        }
        finally
        {
            Marshal.Release(enumerator);
            if (init >= 0)
            {
                Native.CoUninitialize();
            }
        }

        return [.. found.OrderBy(d => !d.IsRender).ThenBy(d => !d.IsDefault)];
    }

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        Native.SetEvent(_wake);
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        foreach (ComCallback cb in _activations)
        {
            cb.Dispose();
        }

        Native.CloseHandle(_event);
        Native.CloseHandle(_wake);
        _firstOpen.Dispose();
    }

    void IDeviceNotifications.DefaultDeviceChanged(int flow, int role, string? deviceId)
    {
        if (flow != _flow || role != Wasapi.RoleConsole || Kind == AudioSourceKind.ProcessLoopback || deviceId == _deviceId)
        {
            return;
        }

        Interlocked.Increment(ref _deviceChanges);
        _reopen = 1;
        Native.SetEvent(_wake);
    }

    void IDeviceNotifications.DeviceStateChanged(string deviceId, uint state)
    {
        if (deviceId == _deviceId && state != Wasapi.DeviceStateActive)
        {
            _reopen = 1;
            Native.SetEvent(_wake);
        }
    }

    private void Run()
    {
        int init = Native.CoInitializeEx(0, Native.CoInitMultithreaded);
        uint taskIndex = 0;
        nint mmcss = Native.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        try
        {
            _enumerator = Wasapi.CreateEnumerator();
            if (Kind != AudioSourceKind.ProcessLoopback)
            {
                _notify = ComCallback.ForNotifications(this);
                Native.Check(Wasapi.RegisterNotification(_enumerator, _notify.Pointer), "RegisterEndpointNotificationCallback");
            }

            while (!_stopping)
            {
                try
                {
                    Open();
                }
                catch (Exception ex) when (ex is WasapiException or TimeoutException)
                {
                    _lastError = ex.Message;
                    CloseStream();
                    _firstOpen.Set();
                    _ = Native.WaitForSingleObject(_wake, 1000);
                    continue;
                }

                _lastError = null;
                _firstOpen.Set();
                Pump();
                CloseStream();
            }
        }
        catch (Exception ex)
        {
            // A consumer callback that throws must not take the recorder down: capture stops and says why.
            _lastError = $"capture stopped: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            CloseStream();
            if (_notify is not null)
            {
                Wasapi.UnregisterNotification(_enumerator, _notify.Pointer);
            }

            Native.Release(ref _enumerator);
            _notify?.Dispose();
            if (mmcss != 0)
            {
                Native.AvRevertMmThreadCharacteristics(mmcss);
            }

            if (init >= 0)
            {
                Native.CoUninitialize();
            }

            _firstOpen.Set();
        }
    }

    private void Open()
    {
        _reopen = 0;
        long bufferHns = _options.Buffer.Ticks;
        PcmFormat format;
        if (Kind == AudioSourceKind.ProcessLoopback)
        {
            _client = ActivateProcessLoopback();
            WaveFormatExtensible want = WaveFormatExtensible.Float(StereoResampler.OutputRate, 2);
            Native.Check(
                Wasapi.Initialize(_client, Wasapi.ShareModeShared, Wasapi.StreamFlagsLoopback | Wasapi.StreamFlagsEventCallback | Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality, bufferHns, 0, &want),
                "IAudioClient.Initialize(process loopback)");
            format = new PcmFormat(StereoResampler.OutputRate, 2, SampleEncoding.IeeeFloat, 8, 32);
            _device = $"pid {_options.ProcessId}";
            _deviceId = null;
        }
        else
        {
            Native.Check(Wasapi.GetDefaultAudioEndpoint(_enumerator, _flow, Wasapi.RoleConsole, out nint device), _flow == Wasapi.FlowRender ? "no default render device" : "no default capture device");
            WaveFormatExtensible* mix = null;
            try
            {
                _deviceId = Wasapi.GetId(device);
                _device = Wasapi.GetFriendlyName(device);
                Native.Check(Wasapi.Activate(device, Wasapi.IidAudioClient, out _client), "IMMDevice.Activate");
                Native.Check(Wasapi.GetMixFormat(_client, out mix), "IAudioClient.GetMixFormat");
                format = WaveFormatExtensible.Describe(mix) ?? throw new WasapiException($"Mix format tag 0x{mix->FormatTag:X} / {mix->BitsPerSample} bit is not one we convert.");
                uint flags = Wasapi.StreamFlagsEventCallback | (Kind == AudioSourceKind.Microphone ? 0 : Wasapi.StreamFlagsLoopback);
                Native.Check(Wasapi.Initialize(_client, Wasapi.ShareModeShared, flags, bufferHns, 0, mix), "IAudioClient.Initialize");
                if (Kind == AudioSourceKind.SystemLoopback && _options.KeepLoopbackAlive)
                {
                    _keepAlive = RenderStream.Open(device);
                    _keepAlive.TopUp(null, out _);
                    _keepAlive.Start();
                }
            }
            finally
            {
                if (mix != null)
                {
                    Marshal.FreeCoTaskMem((nint)mix);
                }

                Marshal.Release(device);
            }
        }

        Native.Check(Wasapi.SetEventHandle(_client, _event), "IAudioClient.SetEventHandle");
        Native.Check(Wasapi.GetService(_client, Wasapi.IidAudioCaptureClient, out _capture), "IAudioClient.GetService(capture)");
        Native.Check(Wasapi.GetBufferSize(_client, out uint bufferFrames), "IAudioClient.GetBufferSize");

        // Allocation happens here, per open, never per packet.
        int maxFrames = (int)Math.Max(bufferFrames, (uint)format.SampleRate / 10);
        if (_float.Length < maxFrames * format.Channels)
        {
            _float = new float[maxFrames * format.Channels];
        }

        _resampler.Step = (double)format.SampleRate / StereoResampler.OutputRate;
        int outFrames = _resampler.MaxOutputFrames(maxFrames);
        if (_out.Length < outFrames * 2)
        {
            _out = new float[outFrames * 2];
        }

        if (_clock is null || _clock.SampleRate != format.SampleRate)
        {
            _clock = new SampleClock(format.SampleRate, _freq);
            _lastResets = 0;
        }

        _resampler.Reset();
        _firstPacket = true;
        Volatile.Write(ref _format, format);
        _allocPrior += _allocCurrent;
        _allocCurrent = 0;
        _warm = 0;
        Native.Check(Wasapi.Start(_client), "IAudioClient.Start");
        Interlocked.Increment(ref _opens);
    }

    private nint ActivateProcessLoopback()
    {
        var waiter = new ActivationWaiter();
        ComCallback cb = ComCallback.ForActivation(waiter);
        _activations.Add(cb); // freed on Dispose: mmdevapi may release it after the callback
        byte* activation = stackalloc byte[12];
        *(int*)activation = 1;                                    // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
        *(uint*)(activation + 4) = _options.ProcessId;            // TargetProcessId
        *(int*)(activation + 8) = _options.IncludeProcessTree ? 0 : 1; // PROCESS_LOOPBACK_MODE_INCLUDE/EXCLUDE_TARGET_PROCESS_TREE
        byte* pv = stackalloc byte[24];
        new Span<byte>(pv, 24).Clear();
        *(ushort*)pv = 65;                                         // VT_BLOB
        *(uint*)(pv + 8) = 12;
        *(byte**)(pv + 16) = activation;

        Native.Check(Native.ActivateAudioInterfaceAsync(Wasapi.ProcessLoopbackDevice, Wasapi.IidAudioClient, pv, cb.Pointer, out nint op), "ActivateAudioInterfaceAsync");
        try
        {
            if (!waiter.Done.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Process loopback activation did not complete.");
            }

            Native.Check(Wasapi.GetActivateResult(op, out int result, out nint client), "GetActivateResult");
            Native.Check(result, $"process loopback activation for pid {_options.ProcessId}");
            return client;
        }
        finally
        {
            Marshal.Release(op);
            if (waiter.Done.IsSet)
            {
                waiter.Done.Dispose(); // a late callback after a timeout would still Set it
            }
        }
    }

    private void Pump()
    {
        nint* handles = stackalloc nint[2];
        handles[0] = _event;
        handles[1] = _wake;
        while (true)
        {
            Native.WaitForMultipleObjects(2, handles, waitAll: false, 20);
            if (_stopping || _reopen != 0)
            {
                return;
            }

            if (_keepAlive is not null && _keepAlive.TopUp(null, out _) < 0)
            {
                Invalidated();
                return;
            }

            while (true)
            {
                if (Wasapi.CaptureNextPacketSize(_capture, out uint next) < 0)
                {
                    Invalidated();
                    return;
                }

                if (next == 0)
                {
                    break;
                }

                int hr = Wasapi.CaptureGetBuffer(_capture, out byte* data, out uint frames, out uint flags, out ulong position, out ulong qpc100ns);
                if (hr == Wasapi.BufferEmpty)
                {
                    break;
                }

                if (hr < 0)
                {
                    Invalidated();
                    return;
                }

                PcmFormat format = _format!;
                int count = (int)Math.Min(frames, (uint)(_float.Length / format.Channels));
                Span<float> pcm = _float.AsSpan(0, count * format.Channels);
                if ((flags & Wasapi.BufferFlagsSilent) != 0)
                {
                    pcm.Clear();
                    _silent++;
                }
                else
                {
                    format.ToFloat(new ReadOnlySpan<byte>(data, count * format.BlockAlign), count, pcm);
                }

                if (Wasapi.CaptureReleaseBuffer(_capture, frames) < 0)
                {
                    Invalidated();
                    return;
                }

                Deliver(pcm, format, flags, (long)position, (long)qpc100ns);
            }
        }
    }

    private void Deliver(ReadOnlySpan<float> pcm, PcmFormat format, uint flags, long position, long qpc100ns)
    {
        SampleClock clock = _clock!;
        long qpc = QpcTime.Rescale(qpc100ns, 10_000_000, _freq);
        bool discontinuity = _firstPacket || (flags & Wasapi.BufferFlagsDataDiscontinuity) != 0;
        if (!_firstPacket && (flags & Wasapi.BufferFlagsDataDiscontinuity) != 0)
        {
            _discontinuities++;
        }

        if ((flags & Wasapi.BufferFlagsTimestampError) != 0)
        {
            _timestampErrors++;
            qpc = _firstPacket ? QpcTime.Rescale(Stopwatch.GetTimestamp(), Stopwatch.Frequency, _freq) : clock.ToQpc(position);
        }

        long first = clock.Observe(position, qpc, discontinuity);
        if (clock.Resets != _lastResets)
        {
            _lastResets = clock.Resets;
            _fitStart = position;
            _resampler.Reset();
        }

        // Follow the measured device rate once the fit has seen a few seconds, so 48 kHz output stays on QPC.
        double rate = position - _fitStart >= FitBeforeTrim.TotalSeconds * format.SampleRate ? clock.MeasuredRate : format.SampleRate;
        _resampler.Step = rate / StereoResampler.OutputRate;
        int n = _resampler.Process(pcm, format.Channels, _out, out double offset);
        _firstPacket = false;
        _packets++;
        _frames += pcm.Length / format.Channels;

        if (n > 0)
        {
            long outQpc = first + (long)Math.Round(offset * _freq / rate);
            ReadOnlySpan<float> stereo = _out.AsSpan(0, n * 2);
            _onPcm?.Invoke(stereo, outQpc);
            if (_assembler is not null)
            {
                _assembler.Add(stereo, 2, outQpc, _onHop!);
            }
        }

        if (_warm < WarmupPackets)
        {
            if (++_warm == WarmupPackets)
            {
                _allocBase = GC.GetAllocatedBytesForCurrentThread();
            }
        }
        else
        {
            Volatile.Write(ref _allocCurrent, GC.GetAllocatedBytesForCurrentThread() - _allocBase);
        }
    }

    private void Invalidated()
    {
        Interlocked.Increment(ref _invalidations);
        _lastError = "the device went away; re-opening on the default device";
    }

    private void CloseStream()
    {
        if (_client != 0)
        {
            Wasapi.Stop(_client);
        }

        _keepAlive?.Dispose();
        _keepAlive = null;
        Native.Release(ref _capture);
        Native.Release(ref _client);
        _device = null;
    }

    private sealed class ActivationWaiter : IActivationCompleted
    {
        public ManualResetEventSlim Done { get; } = new(false);

        public void Completed(nint operation) => Done.Set();
    }
}

/// <summary>An audio endpoint as the bench lists it.</summary>
public sealed record AudioDeviceInfo(bool IsRender, string Name, string Id, bool IsDefault);
