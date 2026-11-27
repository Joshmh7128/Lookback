using System.Runtime.InteropServices;
using Lookback.Capture;
using Lookback.Capture.Gpu;
using Lookback.Core.Time;
using Lookback.Replay.Packets;
using Vortice.Direct3D11;

namespace Lookback.Encode;

/// <summary>Counters for an <see cref="EncodeTicker"/>.</summary>
public readonly record struct EncodeStats(long Captured, long Ticks, long Encoded, long Repeated, long Skipped, long EncoderBusy, long PacketsOut);

/// <summary>
/// The constant-frame-rate encode clock. Capture sources deliver frames
/// irregularly (WGC only when the window changes, DXGI at the monitor's
/// rate), but the recording is CFR at the tier's fps: each captured frame is
/// converted to NV12 at the encode size on the capture thread
/// (<see cref="Nv12Converter"/>, GPU only), and a timer thread encodes the
/// latest converted frame at every tick, repeating it when nothing new came
/// in. A tick's PTS is its scheduled QPC time, so output timing is exact
/// whatever the scheduler does; ticks that are already a full period late are
/// skipped (counted) rather than encoded in a burst.
/// Attach <see cref="FrameTap"/> to <see cref="LiveCaptureSession.FrameTap"/>.
/// Ephemeral sessions never create one.
/// </summary>
public sealed partial class EncodeTicker : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly Nv12Converter _nv12;
    private readonly MfVideoEncoder _encoder;
    private readonly IClock _clock;
    private readonly Thread _thread;
    private readonly nint _timer;
    private readonly int _fps;
    private long _origin = long.MinValue;
    private long _captured;
    private long _ticks;
    private long _encoded;
    private long _repeated;
    private long _skipped;
    private long _busy;
    private volatile bool _stop;
    private volatile EncodePhase _capturePhase;
    private int _disposed;

    private EncodeTicker(GpuDevice gpu, Nv12Converter nv12, MfVideoEncoder encoder, IClock clock)
    {
        _gpu = gpu;
        _nv12 = nv12;
        _encoder = encoder;
        _clock = clock;
        _fps = encoder.Settings.Fps;
        FrameTap = OnFrame;
        _timer = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (_timer == 0)
        {
            // Older than Windows 10 1803: a normal-resolution timer, still fine at 30-60 fps with the catch-up below.
            _timer = CreateWaitableTimerExW(0, 0, 0, TimerAllAccess);
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "lookback-encode-ticker", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>The handler for <see cref="LiveCaptureSession.FrameTap"/>. Runs on the capture thread.</summary>
    public CapturedFrameHandler FrameTap { get; }

    /// <summary>The encoder MFT in use, e.g. "NVIDIA HEVC Encoder MFT".</summary>
    public string EncoderName => _encoder.Name;

    public bool IsHardware => _encoder.IsHardware;

    public VideoEncoderSettings Settings => _encoder.Settings;

    /// <summary>Where each thread is, for stall diagnosis: capture-thread conversion, encoder input, encoder events.</summary>
    public string Phase
    {
        get
        {
            (EncodePhase input, EncodePhase output) = _encoder.Phase;
            return $"capture {_capturePhase}, encode {input}, events {output}";
        }
    }

    /// <summary>Encoder backlog over its pool, for the governor.</summary>
    public double EncoderLoad => _encoder.Load;

    public EncodeStats Stats => new(
        Interlocked.Read(ref _captured),
        Interlocked.Read(ref _ticks),
        Interlocked.Read(ref _encoded),
        Interlocked.Read(ref _repeated),
        Interlocked.Read(ref _skipped),
        Interlocked.Read(ref _busy),
        _encoder.PacketsOut);

    /// <summary>
    /// Creates the NV12 converter and the encoder on <paramref name="gpu"/>
    /// (the capture device) and starts the clock. The first tick is the first
    /// captured frame's time.
    /// </summary>
    public static EncodeTicker Start(GpuDevice gpu, VideoEncoderSettings settings, IPacketSink sink, int trackId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        MediaFoundation.Startup();
        var nv12 = new Nv12Converter(gpu, settings.Width, settings.Height, settings.Fps);
        try
        {
            MfVideoEncoder encoder = MfVideoEncoder.Create(gpu, settings, sink, trackId, clock.Frequency);
            return new EncodeTicker(gpu, nv12, encoder, clock);
        }
        catch
        {
            nv12.Dispose();
            throw;
        }
    }

    /// <summary>Asks for an IDR at the next tick.</summary>
    public void ForceKeyframe() => _encoder.ForceKeyframe();

    private void OnFrame(ID3D11Texture2D texture, long qpc, TransferFunction transfer)
    {
        if (_stop)
        {
            return;
        }

        // HDR sources are converted as if SDR until the M6 tone-map pass; the analysis tap handles them correctly.
        _capturePhase = EncodePhase.WaitingForGpuLock;
        lock (_gpu.Lock)
        {
            _capturePhase = EncodePhase.Converting;
            _nv12.Convert(texture);
        }

        _capturePhase = EncodePhase.Idle;

        Interlocked.CompareExchange(ref _origin, qpc, long.MinValue);
        Interlocked.Increment(ref _captured);
    }

    /// <summary>QPC time of tick <paramref name="n"/>; computed from the origin each time so it never drifts.</summary>
    private long TickTime(long n) => Volatile.Read(ref _origin) + QpcTime.Rescale(n, _fps, _clock.Frequency);

    private void Run()
    {
        long n = 0;
        long lastCaptured = 0;
        long freq = _clock.Frequency;
        while (!_stop)
        {
            if (Volatile.Read(ref _origin) == long.MinValue)
            {
                Sleep(freq / 1000);
                continue;
            }

            long due = TickTime(n);
            long now = _clock.Now;
            if (now < due)
            {
                Sleep(due - now);
                continue;
            }

            // A full period or more behind (the thread was starved): drop those ticks instead of bursting.
            long late = QpcTime.Rescale(now - due, freq, _fps);
            if (late >= 1)
            {
                n += late;
                Interlocked.Add(ref _skipped, late);
                due = TickTime(n);
            }

            long captured = Interlocked.Read(ref _captured);
            if (captured == lastCaptured)
            {
                Interlocked.Increment(ref _repeated);
            }

            lastCaptured = captured;
            Interlocked.Increment(ref _ticks);
            if (_encoder.Encode(_nv12.Latest, due))
            {
                Interlocked.Increment(ref _encoded);
            }
            else
            {
                Interlocked.Increment(ref _busy);
            }

            n++;
        }
    }

    private void Sleep(long qpcTicks)
    {
        long hns = Math.Max(1, QpcTime.Rescale(qpcTicks, _clock.Frequency, 10_000_000));
        if (_timer != 0)
        {
            long due = -hns; // negative: relative, in 100 ns units
            if (SetWaitableTimer(_timer, in due, 0, 0, 0, false))
            {
                _ = WaitForSingleObject(_timer, 100);
                return;
            }
        }

        Thread.Sleep(TimeSpan.FromTicks(hns));
    }

    /// <summary>Stops the clock, then drains and releases the encoder.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));
        _encoder.Dispose();
        lock (_gpu.Lock)
        {
            _nv12.Dispose();
        }

        if (_timer != 0)
        {
            _ = CloseHandle(_timer);
        }
    }

    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completion, nint arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
