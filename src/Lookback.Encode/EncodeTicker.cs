using System.Runtime.InteropServices;
using Lookback.Capture;
using Lookback.Capture.Gpu;
using Lookback.Core.Diagnostics;
using Lookback.Core.Time;
using Lookback.Replay.Packets;
using Vortice.Direct3D11;

namespace Lookback.Encode;

/// <summary>Counters for an <see cref="EncodeTicker"/>.</summary>
public readonly record struct EncodeStats(long Captured, long Ticks, long Encoded, long Repeated, long Skipped, long EncoderBusy, long PacketsOut, int Recoveries);

/// <summary>How an <see cref="EncodeTicker"/> watches for stalls and what it does about them.</summary>
public sealed record EncodeTickerOptions
{
    /// <summary>The watchdog to register with. Null: no stall detection (bare tests only).</summary>
    public StallWatchdog? Watchdog { get; init; }

    /// <summary>How long a step may run, or ticks go by without a packet, before the encoder counts as stalled.</summary>
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Encoder swaps allowed in one session before giving up with <see cref="Fatal"/>.</summary>
    public int MaxRecoveries { get; init; } = 3;

    /// <summary>Called when nothing can recover the encode path. The default ends the process.</summary>
    public Action<string> Fatal { get; init; } = Core.Diagnostics.Fatal.Default;

    /// <summary>Where stalled encoders are remembered, so the next run skips them.</summary>
    public EncoderBlocklist? Blocklist { get; init; }

    /// <summary>Recovery messages (stderr in the bench and recorder).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The constant-frame-rate encode clock, with a watchdog around the encoder.
/// Capture sources deliver frames irregularly (WGC only when the window
/// changes, DXGI at the monitor's rate), but the recording is CFR at the
/// tier's fps: each captured frame is converted to NV12 at the encode size on
/// the capture thread (<see cref="Nv12Converter"/>, GPU only), and a timer
/// thread encodes the latest converted frame at every tick, repeating it when
/// nothing new came in. A tick's PTS is its scheduled QPC time; ticks that are
/// already a full period late are skipped (counted) rather than encoded in a burst.
/// <para>
/// No deadlocks: the encoder runs on its own device by default, and every
/// step of the encode thread reports a heartbeat. If a step runs past
/// <see cref="EncodeTickerOptions.StallAfter"/>, or ticks go by without a
/// packet coming out, the stuck encoder is abandoned (never waited on), the
/// next encoder in the fallback chain takes over on a fresh thread on the
/// same tick grid, and the stalled one is remembered in the blocklist. When
/// the chain is exhausted or too many swaps happen, <see cref="EncodeTickerOptions.Fatal"/>
/// ends the process rather than leaving it hung.
/// </para>
/// Attach <see cref="FrameTap"/> to <see cref="LiveCaptureSession.FrameTap"/>.
/// Ephemeral sessions never create one.
/// </summary>
public sealed partial class EncodeTicker : IDisposable
{
    private readonly GpuDevice? _gpu;
    private readonly Nv12Converter? _nv12;
    private readonly IClock _clock;
    private readonly int _fps;
    private readonly IReadOnlyList<EncoderCandidate> _candidates;
    private readonly Func<EncoderCandidate, IVideoEncoder> _create;
    private readonly EncodeTickerOptions _options;
    private readonly string _adapter;
    private readonly Lock _recoverLock = new();
    private readonly Beat _captureBeat = new();
    private volatile Lane _lane;
    private long _origin = long.MinValue;
    private long _nextTick;
    private long _captured;
    private long _ticks;
    private long _encoded;
    private long _repeated;
    private long _skipped;
    private long _busy;
    private int _recoveries;
    private volatile bool _stop;
    private int _disposed;

    private EncodeTicker(GpuDevice? gpu, Nv12Converter? nv12, IClock clock, int fps, IReadOnlyList<EncoderCandidate> candidates, Func<EncoderCandidate, IVideoEncoder> create, EncodeTickerOptions options)
    {
        _gpu = gpu;
        _nv12 = nv12;
        _clock = clock;
        _fps = fps;
        _candidates = candidates;
        _create = create;
        _options = options;
        _adapter = gpu?.Identity ?? "test";
        FrameTap = OnFrame;

        List<string> failures = [];
        for (int i = 0; i < candidates.Count; i++)
        {
            try
            {
                _lane = StartLane(create(candidates[i]), i);
                return;
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException or AggregateException)
            {
                failures.Add($"{candidates[i]}: {ex.Message}");
            }
        }

        throw new InvalidOperationException($"No encoder could start ({string.Join("; ", failures)}).");
    }

    /// <summary>The handler for <see cref="LiveCaptureSession.FrameTap"/>. Runs on the capture thread.</summary>
    public CapturedFrameHandler FrameTap { get; }

    /// <summary>The encoder MFT in use now, e.g. "NVIDIA HEVC Encoder MFT".</summary>
    public string EncoderName => _lane.Encoder.Candidate.Name;

    public bool IsHardware => _lane.Encoder.Candidate.Hardware;

    /// <summary>The settings of the encoder in use (its codec can differ from the request after a fallback).</summary>
    public VideoEncoderSettings Settings => _lane.Encoder.Settings;

    /// <summary>True when the current encoder runs on its own device.</summary>
    public bool IsIsolated => _lane.Encoder is MfVideoEncoder { IsIsolated: true };

    /// <summary>Encoder backlog over its pool, for the governor.</summary>
    public double EncoderLoad => _lane.Encoder.Load;

    /// <summary>The capture thread's heartbeat inside <see cref="FrameTap"/> (NV12 conversion).</summary>
    public Beat CaptureBeat => _captureBeat;

    /// <summary>Where each thread is, for stall reports.</summary>
    public string Phase
    {
        get
        {
            Lane lane = _lane;
            Heartbeat h = lane.Beat.Read();
            Heartbeat c = _captureBeat.Read();
            return $"capture {(c.Busy ? c.Step : "idle")}, ticker {(h.Busy ? h.Step : "idle")}, {lane.Encoder.Phase}";
        }
    }

    public EncodeStats Stats => new(
        Interlocked.Read(ref _captured),
        Interlocked.Read(ref _ticks),
        Interlocked.Read(ref _encoded),
        Interlocked.Read(ref _repeated),
        Interlocked.Read(ref _skipped),
        Interlocked.Read(ref _busy),
        _lane.Encoder.PacketsOut,
        Volatile.Read(ref _recoveries));

    /// <summary>
    /// Creates the NV12 converter on <paramref name="gpu"/> (the capture
    /// device), builds the encoder fallback chain for the adapter, starts the
    /// first encoder that works and starts the clock. The first tick is the
    /// first captured frame's time.
    /// </summary>
    public static EncodeTicker Start(GpuDevice gpu, VideoEncoderSettings settings, IPacketSink sink, int trackId, IClock clock, EncodeTickerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clock);
        options ??= new EncodeTickerOptions();
        MediaFoundation.Startup();
        IReadOnlyList<EncoderCandidate> chain = MfVideoEncoder.Candidates(gpu, settings, options.Blocklist);
        if (chain.Count == 0)
        {
            throw new InvalidOperationException($"No {settings.Codec} encoder is available on {gpu.Name} (all blocklisted or none installed).");
        }

        var nv12 = new Nv12Converter(gpu, settings.Width, settings.Height, settings.Fps);
        try
        {
            return new EncodeTicker(gpu, nv12, clock, settings.Fps, chain, c => MfVideoEncoder.Create(gpu, settings, c, sink, trackId, clock.Frequency), options);
        }
        catch
        {
            nv12.Dispose();
            throw;
        }
    }

    /// <summary>A ticker with no GPU, for fault-injection tests: frames come from <see cref="SimulateFrame"/> and encoders get a null texture.</summary>
    internal static EncodeTicker StartForTest(IClock clock, int fps, IReadOnlyList<EncoderCandidate> candidates, Func<EncoderCandidate, IVideoEncoder> create, EncodeTickerOptions options)
        => new(null, null, clock, fps, candidates, create, options);

    /// <summary>Test hook: a frame arrived at <paramref name="qpc"/>.</summary>
    internal void SimulateFrame(long qpc)
    {
        Interlocked.CompareExchange(ref _origin, qpc, long.MinValue);
        Interlocked.Increment(ref _captured);
    }

    /// <summary>Asks for an IDR at the next tick.</summary>
    public void ForceKeyframe() => _lane.Encoder.ForceKeyframe();

    private void OnFrame(ID3D11Texture2D texture, long qpc, TransferFunction transfer)
    {
        if (_stop || _nv12 is null)
        {
            return;
        }

        // HDR sources are converted as if SDR until the M6 tone-map pass; the analysis tap handles them correctly.
        _captureBeat.Enter("waiting for the capture device", _clock.Now);
        lock (_gpu!.Lock)
        {
            _captureBeat.Enter("NV12 convert", _clock.Now);
            _nv12.Convert(texture);
        }

        _captureBeat.Exit();
        Interlocked.CompareExchange(ref _origin, qpc, long.MinValue);
        Interlocked.Increment(ref _captured);
    }

    /// <summary>QPC time of tick <paramref name="n"/>; computed from the origin each time so it never drifts.</summary>
    private long TickTime(long n) => Volatile.Read(ref _origin) + QpcTime.Rescale(n, _fps, _clock.Frequency);

    private Lane StartLane(IVideoEncoder encoder, int index)
    {
        var lane = new Lane(encoder, index, CreateTimer());
        lane.Thread = new Thread(() => Run(lane)) { IsBackground = true, Name = $"lookback-encode-ticker-{index}", Priority = ThreadPriority.AboveNormal };
        if (_options.Watchdog is { } dog)
        {
            lane.Watches.Add(dog.Watch($"encode ticker ({encoder.Candidate.Name})", lane.Beat, _options.StallAfter, r => Recover(lane, $"{r.Step} for {r.For.TotalSeconds:F1} s")));
            lane.Watches.Add(dog.Watch($"encoder output ({encoder.Candidate.Name})", () => OutputProgress(lane), _options.StallAfter, r => Recover(lane, $"no packets for {r.For.TotalSeconds:F1} s while frames were due")));
        }

        _lane = lane;
        lane.Thread.Start();
        return lane;
    }

    /// <summary>Busy while ticks are due but no packet comes out. Called on the watchdog thread only.</summary>
    private Heartbeat OutputProgress(Lane lane)
    {
        long now = _clock.Now;
        long packets = lane.Encoder.PacketsOut;
        long ticks = Interlocked.Read(ref lane.Ticks);
        if (packets != lane.LastPackets || ticks == lane.TicksAtProgress)
        {
            lane.LastPackets = packets;
            lane.TicksAtProgress = ticks;
            lane.ProgressQpc = now;
            return new Heartbeat(false, "output", now);
        }

        return new Heartbeat(true, "waiting for output", lane.ProgressQpc);
    }

    private void Run(Lane lane)
    {
        long freq = _clock.Frequency;
        long lastCaptured = Interlocked.Read(ref _captured);
        try
        {
            while (!_stop && ReferenceEquals(lane, _lane))
            {
                if (Volatile.Read(ref _origin) == long.MinValue)
                {
                    Sleep(lane, freq / 1000);
                    continue;
                }

                long n = Interlocked.Read(ref _nextTick);
                long due = TickTime(n);
                long now = _clock.Now;
                if (now < due)
                {
                    Sleep(lane, due - now);
                    continue;
                }

                // A full period or more behind (the thread was starved, or this lane just took over): skip those ticks instead of bursting.
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
                Interlocked.Increment(ref lane.Ticks);
                lane.Beat.Enter("encode", _clock.Now);
                bool ok = lane.Encoder.Encode(_nv12?.Latest!, due);
                lane.Beat.Exit();
                if (!ReferenceEquals(lane, _lane))
                {
                    return; // replaced while this call was stuck
                }

                Interlocked.Increment(ref ok ? ref _encoded : ref _busy);
                Interlocked.Exchange(ref _nextTick, n + 1);
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or ObjectDisposedException or NullReferenceException or InvalidOperationException)
        {
            lane.Beat.Exit();
            if (!_stop && ReferenceEquals(lane, _lane))
            {
                // The encoder failed outright: treat it like a stall. Runs on this thread, which then exits.
                Recover(lane, $"threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            _ = CloseHandle(lane.Timer);
        }
    }

    /// <summary>Swaps a stalled lane for the next encoder in the chain, or gives up with Fatal. Never waits on the stalled encoder.</summary>
    private void Recover(Lane lane, string reason)
    {
        lock (_recoverLock)
        {
            if (_stop || !ReferenceEquals(lane, _lane))
            {
                return;
            }

            EncoderCandidate bad = lane.Encoder.Candidate;
            string what = $"{bad} stalled: {reason} [{Phase}]";
            _options.Log?.Invoke($"encode: {what}");
            _options.Blocklist?.Add(_adapter, bad, reason);
            foreach (IDisposable w in lane.Watches)
            {
                w.Dispose();
            }

            if (++_recoveries > _options.MaxRecoveries)
            {
                _options.Fatal($"encode path stalled {_recoveries} times; last: {what}");
                return;
            }

            for (int i = lane.Index + 1; i < _candidates.Count; i++)
            {
                IVideoEncoder? next = CreateWithin(_candidates[i], _options.StallAfter * 2, out string? why);
                if (next is null)
                {
                    _options.Log?.Invoke($"encode: {_candidates[i]} could not take over: {why}");
                    continue;
                }

                // Continue on the tick grid from now; the new encoder opens with an IDR.
                Interlocked.Exchange(ref _nextTick, Math.Max(Interlocked.Read(ref _nextTick), CurrentTick()));
                StartLane(next, i);
                lane.Encoder.Abandon();
                _options.Log?.Invoke($"encode: switched to {_candidates[i]}");
                return;
            }

            lane.Encoder.Abandon();
            _options.Fatal($"no encoder left after {what}");
        }
    }

    private long CurrentTick()
    {
        long origin = Volatile.Read(ref _origin);
        return origin == long.MinValue ? 0 : QpcTime.Rescale(Math.Max(0, _clock.Now - origin), _clock.Frequency, _fps);
    }

    /// <summary>Creates an encoder on a helper thread and gives up after <paramref name="within"/>, since creating one can hang in the driver too.</summary>
    private IVideoEncoder? CreateWithin(EncoderCandidate candidate, TimeSpan within, out string? why)
    {
        IVideoEncoder? made = null;
        Exception? error = null;
        int abandoned = 0;
        var t = new Thread(() =>
        {
            try
            {
                IVideoEncoder e = _create(candidate);
                if (Volatile.Read(ref abandoned) != 0)
                {
                    e.Abandon(); // finished too late
                }
                else
                {
                    made = e;
                }
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException or AggregateException)
            {
                error = ex;
            }
        })
        { IsBackground = true, Name = "lookback-encoder-create" };
        t.Start();
        if (!t.Join(within))
        {
            Volatile.Write(ref abandoned, 1);
            why = $"creation took longer than {within.TotalSeconds:F0} s";
            return null;
        }

        why = error?.Message;
        return made;
    }

    private void Sleep(Lane lane, long qpcTicks)
    {
        long hns = Math.Max(1, QpcTime.Rescale(qpcTicks, _clock.Frequency, 10_000_000));
        if (lane.Timer != 0)
        {
            long due = -hns; // negative: relative, in 100 ns units
            if (SetWaitableTimer(lane.Timer, in due, 0, 0, 0, false))
            {
                _ = WaitForSingleObject(lane.Timer, 100);
                return;
            }
        }

        Thread.Sleep(TimeSpan.FromTicks(Math.Min(hns, TimeSpan.TicksPerSecond / 10)));
    }

    /// <summary>
    /// Stops the clock, then drains and releases the encoder. Never hangs:
    /// a ticker or encoder that does not finish within a few seconds is abandoned.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Lane lane;
        lock (_recoverLock)
        {
            _stop = true;
            lane = _lane;
            foreach (IDisposable w in lane.Watches)
            {
                w.Dispose();
            }
        }

        if (!lane.Thread!.Join(TimeSpan.FromSeconds(2)))
        {
            lane.Encoder.Abandon();
        }
        else
        {
            var close = new Thread(lane.Encoder.Dispose) { IsBackground = true, Name = "lookback-encoder-close" };
            close.Start();
            if (!close.Join(TimeSpan.FromSeconds(5)))
            {
                _options.Log?.Invoke($"encode: {lane.Encoder.Candidate} did not close within 5 s; left behind");
            }
        }

        if (_nv12 is not null)
        {
            lock (_gpu!.Lock)
            {
                _nv12.Dispose();
            }
        }
    }

    private static nint CreateTimer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        nint timer = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        // Older than Windows 10 1803: a normal-resolution timer, still fine at 30-60 fps with the catch-up.
        return timer != 0 ? timer : CreateWaitableTimerExW(0, 0, 0, TimerAllAccess);
    }

    /// <summary>One encoder and the thread that feeds it. A stalled lane is replaced, never joined.</summary>
    private sealed class Lane(IVideoEncoder encoder, int index, nint timer)
    {
        public IVideoEncoder Encoder { get; } = encoder;

        public int Index { get; } = index;

        public nint Timer { get; } = timer;

        public Beat Beat { get; } = new();

        public Thread? Thread { get; set; }

        public List<IDisposable> Watches { get; } = [];

        public long Ticks;
        public long LastPackets = -1;
        public long TicksAtProgress = -1;
        public long ProgressQpc;
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
