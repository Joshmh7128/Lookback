using Lookback.Capture.Dxgi;
using Lookback.Capture.Gpu;
using Lookback.Capture.Targets;
using Lookback.Capture.Wgc;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Diagnostics;
using Lookback.Core.Time;
using Lookback.Media.Frames;
using Lookback.Signals;
using Lookback.Signals.Video;
using Windows.Graphics.Capture;

namespace Lookback.Capture;

/// <summary>Which capture backend to use.</summary>
public enum CaptureBackend
{
    /// <summary>WGC when the OS supports it, otherwise DXGI duplication of the target's monitor.</summary>
    Auto,
    Wgc,
    Dxgi,

    /// <summary>The present hook in one of our own test apps (<see cref="LiveSessionOptions.HookTarget"/>); never games.</summary>
    Hook,
}

/// <summary>What a live session captures and what may be kept of it.</summary>
public sealed record LiveSessionOptions
{
    public required CaptureTarget Target { get; init; }

    public CaptureBackend Backend { get; init; } = CaptureBackend.Auto;

    public SourceKind SourceKind { get; init; } = SourceKind.LiveGame;

    public Retention Retention { get; init; } = Retention.Owned;

    public string PermissionRef { get; init; } = "";

    public string Title { get; init; } = "";

    public string GameProfile { get; init; } = "";

    public bool CaptureCursor { get; init; }

    public string? SessionId { get; init; }

    /// <summary>For <see cref="CaptureBackend.Hook"/>: the test app <see cref="Hook.HookInjector"/> launched. The target's adapter must be the hook's.</summary>
    public Hook.HookedProcess? HookTarget { get; init; }

    /// <summary>Extra per-frame signals, such as a game profile's regions. Region signals must be laid out for <see cref="Analysis"/>.</summary>
    public IReadOnlyList<IVideoSignal> ExtraVideoSignals { get; init; } = [];

    /// <summary>The analysis frame the GPU downscales to and the signals read. Default 384x216.</summary>
    public AnalysisFormat Analysis { get; init; } = AnalysisFormat.Default;
}

/// <summary>
/// One live capture session: picks the adapter and backend for the target,
/// runs the analysis tap (GPU downscale → readback → <see cref="LiveAnalyzer"/>),
/// and brackets it with <c>SessionStarted</c>/<c>SessionEnded</c> on the bus.
/// Every timestamp is raw QPC from the capture source. The encode path
/// attaches through <see cref="FrameTap"/> (M3 part 4); an ephemeral session
/// never gets one.
/// </summary>
public sealed class LiveCaptureSession : IAsyncDisposable
{
    private readonly IEventBus _bus;
    private readonly EnvelopeFactory _envelopes;
    private readonly GpuDevice _gpu;
    private readonly ICaptureSource _source;
    private readonly GpuDownscaler _downscaler;
    private readonly LiveAnalyzer _analyzer;
    private readonly FrameReady _deliver;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _disposed;

    private LiveCaptureSession(IEventBus bus, LiveSessionOptions options, SessionStarted started)
    {
        _bus = bus;
        Options = options;
        SessionId = options.SessionId ?? $"live-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        Started = started;
        _envelopes = new EnvelopeFactory(SystemClock.Instance, SessionId, "recorder.capture");

        _gpu = GpuDevice.Create(options.Target.AdapterLuid);
        try
        {
            _source = CreateSource(_gpu, options);
            _downscaler = new GpuDownscaler(_gpu, options.Analysis);
            _analyzer = new LiveAnalyzer(bus, SessionId, SystemClock.Instance, options.Analysis, options.ExtraVideoSignals);
        }
        catch
        {
            _gpu.Dispose();
            throw;
        }

        _deliver = (span, pitch, qpc) => _analyzer.SubmitVideo(span, pitch, qpc);
        _source.Stopped += (_, why) => StoppedReason = why;
    }

    public string SessionId { get; }

    public LiveSessionOptions Options { get; }

    public SessionStarted Started { get; }

    /// <summary>True when the retention rules let this session's video reach the encoder and disk.</summary>
    public bool CanPersist => RetentionRules.CanPersist(Started);

    public string Backend => _source.Backend;

    public string AdapterName => _gpu.Name;

    public GpuDevice Gpu => _gpu;

    public LiveAnalyzer Analyzer => _analyzer;

    public string? StoppedReason { get; private set; }

    /// <summary>
    /// The capture thread's heartbeat: busy for the whole frame callback
    /// (analysis downscale and the encode path's conversion). Hosts watch it
    /// and end the process if it stays busy: a wedged capture device cannot
    /// be recovered in-process.
    /// </summary>
    public Beat CaptureBeat { get; } = new();

    /// <summary>Called for every captured frame after the analysis tap, on the capture thread. The encode path hooks in here.</summary>
    public CapturedFrameHandler? FrameTap { get; set; }

    public SessionStats Stats => new(
        _source.FramesCaptured,
        _analyzer.ProcessedVideo,
        _analyzer.DroppedVideo,
        _analyzer.ProcessedAudio,
        _clock.Elapsed)
    {
        TapBusy = _downscaler.Busy,
        ReadbackBusy = _downscaler.ReadbackBusy,
        ReadbackWaits = _downscaler.ReadbackWaits,
        AnalysisBusy = _analyzer.VideoBusy,
        AnalysisBusyMax = _analyzer.VideoBusyMax,
    };

    /// <summary>Validates retention, starts capture and announces the session.</summary>
    public static LiveCaptureSession Start(IEventBus bus, LiveSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(options);
        var started = new SessionStarted
        {
            SourceKind = options.SourceKind,
            Retention = options.Retention,
            PermissionRef = options.PermissionRef,
            Title = options.Title.Length > 0 ? options.Title : options.Target.Description,
            QpcFrequency = (ulong)SystemClock.Instance.Frequency,
            GameProfile = options.GameProfile,
        };
        string? problem = RetentionRules.Validate(started);
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }

        var session = new LiveCaptureSession(bus, options, started);
        Envelope e = session._envelopes.Create(Topics.Session);
        e.SessionStarted = started;
        bus.Publish(e);
        session._source.Start(session.OnFrame);
        return session;
    }

    private void OnFrame(Vortice.Direct3D11.ID3D11Texture2D texture, long qpc, TransferFunction transfer)
    {
        CaptureBeat.Enter("frame callback", SystemClock.Instance.Now);
        try
        {
            if (_analyzer.WantsVideo(qpc))
            {
                _downscaler.Process(texture, qpc, transfer, _deliver);
            }

            FrameTap?.Invoke(texture, qpc, transfer);
        }
        finally
        {
            CaptureBeat.Exit();
        }
    }

    private static ICaptureSource CreateSource(GpuDevice gpu, LiveSessionOptions o)
    {
        CaptureBackend backend = o.Backend;
        if (backend == CaptureBackend.Auto)
        {
            backend = GraphicsCaptureSession.IsSupported() ? CaptureBackend.Wgc : CaptureBackend.Dxgi;
        }

        return backend switch
        {
            CaptureBackend.Wgc => new WgcCaptureSource(gpu, o.Target, o.CaptureCursor),
            CaptureBackend.Dxgi => new DxgiDuplicationSource(gpu, o.Target),
            CaptureBackend.Hook => new Hook.HookCaptureSource(gpu, o.HookTarget ?? throw new ArgumentException("The hook backend needs HookTarget (a test app launched by HookInjector).", nameof(o))),
            _ => throw new ArgumentOutOfRangeException(nameof(o)),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _source.Dispose();
        _downscaler.Flush(_deliver);
        await _analyzer.DisposeAsync().ConfigureAwait(false);
        Envelope e = _envelopes.Create(Topics.Session);
        e.SessionEnded = new SessionEnded { Reason = StoppedReason ?? "stopped" };
        _bus.Publish(e);
        _downscaler.Dispose();
        _gpu.Dispose();
    }
}

/// <summary>Counters for a live session.</summary>
public readonly record struct SessionStats(long Captured, long Analysed, long AnalysisDropped, long AudioHops, TimeSpan Elapsed)
{
    /// <summary>Capture-thread time in the analysis tap (GPU downscale dispatch plus readback).</summary>
    public TimeSpan TapBusy { get; init; }

    /// <summary>The readback part of <see cref="TapBusy"/>: mapping staging textures and copying the frame to the analyzer.</summary>
    public TimeSpan ReadbackBusy { get; init; }

    /// <summary>Times the readback ring was full and its oldest slot was mapped with a blocking wait (usually already finished).</summary>
    public long ReadbackWaits { get; init; }

    /// <summary>Analysis worker time in the video signal bank.</summary>
    public TimeSpan AnalysisBusy { get; init; }

    /// <summary>The slowest single analysis frame.</summary>
    public TimeSpan AnalysisBusyMax { get; init; }

    public double CaptureFps => Elapsed.TotalSeconds > 0 ? Captured / Elapsed.TotalSeconds : 0;

    public double AnalysisFps => Elapsed.TotalSeconds > 0 ? Analysed / Elapsed.TotalSeconds : 0;
}
