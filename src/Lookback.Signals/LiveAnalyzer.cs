using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Buffers;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Frames;
using Lookback.Signals.Audio;
using Lookback.Signals.Video;

namespace Lookback.Signals;

/// <summary>
/// Live counterpart of <see cref="FootageAnalyzer"/>: capture and audio
/// threads hand in analysis frames and hops, and a below-normal-priority
/// worker runs the signal banks and publishes them with the capture QPC.
/// Hand-off is through fixed pools and SPSC rings, so the capture side never
/// allocates or blocks. When the worker falls behind, frames are dropped
/// (analysis frame rate degrades first; capture and encode never wait), and
/// video is thinned to the analysis rate by QPC.
/// </summary>
public sealed class LiveAnalyzer : IAsyncDisposable
{
    private const int VideoPool = 4;
    private const int AudioPool = 64;

    private readonly AnalysisFormat _format;
    private readonly IClock _clock;
    private readonly long _frequency;
    private readonly VideoSignalBank _videoBank;
    private readonly AudioSignalBank _audioBank;
    private readonly SignalPublisher _videoPublisher;
    private readonly SignalPublisher _audioPublisher;
    private readonly VideoFrame[] _frames;
    private readonly AudioBlock[] _blocks;
    private readonly SpscRing<int> _freeFrames = new(VideoPool * 2);
    private readonly SpscRing<int> _readyFrames = new(VideoPool * 2);
    private readonly SpscRing<int> _freeBlocks = new(AudioPool * 2);
    private readonly SpscRing<int> _readyBlocks = new(AudioPool * 2);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private readonly long _frameTicks;
    private long _nextVideoSlot = long.MinValue;
    private long _videoIndex;
    private long _droppedVideo;
    private long _droppedAudio;
    private long _processedVideo;
    private long _processedAudio;
    private long _videoBusyTicks;
    private long _videoMaxTicks;

    public LiveAnalyzer(IEventBus bus, string sessionId, IClock clock, AnalysisFormat? format = null, IEnumerable<IVideoSignal>? extraVideoSignals = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(clock);
        _format = format ?? AnalysisFormat.Default;
        _clock = clock;
        _frequency = clock.Frequency;
        _frameTicks = QpcTime.Rescale(1, _format.FramesPerSecond, _frequency);
        _videoBank = VideoSignalBank.CreateDefault(_format, extraVideoSignals);
        _audioBank = AudioSignalBank.CreateDefault(_format);
        _videoPublisher = new SignalPublisher(bus, new EnvelopeFactory(clock, sessionId, "signals.video"), Topics.SignalsVideo, SignalPublisher.VideoSchemaId, SignalFamily.Video, _videoBank.Names);
        _audioPublisher = new SignalPublisher(bus, new EnvelopeFactory(clock, sessionId, "signals.audio"), Topics.SignalsAudio, SignalPublisher.AudioSchemaId, SignalFamily.Audio, _audioBank.Names);
        long now = clock.Now;
        _videoPublisher.PublishSchema(now);
        _audioPublisher.PublishSchema(now);

        _frames = new VideoFrame[VideoPool];
        for (int i = 0; i < VideoPool; i++)
        {
            _frames[i] = new VideoFrame(_format.Width, _format.Height);
            _freeFrames.TryWrite(i);
        }

        _blocks = new AudioBlock[AudioPool];
        for (int i = 0; i < AudioPool; i++)
        {
            _blocks[i] = new AudioBlock(_format.SampleRate, _format.HopSamples);
            _freeBlocks.TryWrite(i);
        }

        _worker = new Thread(Run) { IsBackground = true, Name = "Lookback analysis", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public AnalysisFormat Format => _format;

    /// <summary>
    /// Publishes both signal schemas again. Signal frames mean nothing without
    /// them, and the analyzer first publishes them as it is built: before its
    /// session is announced (the late-join snapshot starts a session afresh at
    /// <c>SessionStarted</c>) and before consumers created after it (the
    /// profile runner, the clipper) subscribe. Call it once those are in place.
    /// </summary>
    public void PublishSchemas()
    {
        long now = _clock.Now;
        _videoPublisher.PublishSchema(now);
        _audioPublisher.PublishSchema(now);
    }

    public long DroppedVideo => Interlocked.Read(ref _droppedVideo);

    public long DroppedAudio => Interlocked.Read(ref _droppedAudio);

    public long ProcessedVideo => Interlocked.Read(ref _processedVideo);

    public long ProcessedAudio => Interlocked.Read(ref _processedAudio);

    /// <summary>Worker time spent in the video signal bank so far (summed over processed frames).</summary>
    public TimeSpan VideoBusy => System.Diagnostics.Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _videoBusyTicks));

    /// <summary>The slowest single frame in the video signal bank so far.</summary>
    public TimeSpan VideoBusyMax => System.Diagnostics.Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _videoMaxTicks));

    /// <summary>The standard video signals, for the HUD mask and similar state.</summary>
    public VideoSignalBank VideoBank => _videoBank;

    /// <summary>
    /// True when a frame at <paramref name="qpc"/> is wanted: it reaches the
    /// next analysis slot. Lets the capture side skip the GPU downscale and
    /// readback for frames that would be thinned anyway.
    /// </summary>
    public bool WantsVideo(long qpc) => _nextVideoSlot == long.MinValue || qpc >= _nextVideoSlot - (_frameTicks / 4);

    /// <summary>
    /// Hands in one analysis frame: tightly or loosely packed BGRA rows at
    /// the analysis size. Call from one thread. Returns false if the frame was
    /// thinned or dropped.
    /// </summary>
    public bool SubmitVideo(ReadOnlySpan<byte> bgra, int stride, long qpc)
    {
        if (!WantsVideo(qpc))
        {
            return false;
        }

        _nextVideoSlot = (_nextVideoSlot == long.MinValue ? qpc : Math.Max(_nextVideoSlot, qpc - _frameTicks)) + _frameTicks;
        if (!_freeFrames.TryRead(out int slot))
        {
            Interlocked.Increment(ref _droppedVideo);
            return false;
        }

        VideoFrame frame = _frames[slot];
        int row = _format.Width * VideoFrame.BytesPerPixel;
        if (stride < row || bgra.Length < (stride * (_format.Height - 1)) + row)
        {
            _freeFrames.TryWrite(slot);
            throw new ArgumentException("Frame is smaller than the analysis format.", nameof(bgra));
        }

        Span<byte> dst = frame.Pixels;
        for (int y = 0; y < _format.Height; y++)
        {
            bgra.Slice(y * stride, row).CopyTo(dst.Slice(y * row, row));
        }

        frame.Qpc = qpc;
        frame.Index = _videoIndex++;
        _readyFrames.TryWrite(slot);
        _signal.Release();
        return true;
    }

    /// <summary>Hands in one hop of mono analysis audio. Call from one thread (the audio thread).</summary>
    public bool SubmitAudio(AudioBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (!_freeBlocks.TryRead(out int slot))
        {
            Interlocked.Increment(ref _droppedAudio);
            return false;
        }

        AudioBlock b = _blocks[slot];
        b.CopyFrom(block);
        _readyBlocks.TryWrite(slot);
        _signal.Release();
        return true;
    }

    private void Run()
    {
        CancellationToken ct = _stop.Token;
        while (true)
        {
            try
            {
                _signal.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                Drain();
                return;
            }

            Drain();
        }
    }

    private void Drain()
    {
        while (_readyFrames.TryRead(out int slot))
        {
            VideoFrame f = _frames[slot];
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ReadOnlySpan<float> values = _videoBank.Process(f);
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            Interlocked.Add(ref _videoBusyTicks, dt);
            if (dt > Interlocked.Read(ref _videoMaxTicks))
            {
                Interlocked.Exchange(ref _videoMaxTicks, dt); // only the worker writes it
            }

            _videoPublisher.Publish(f.Qpc, f.Qpc + _frameTicks, values);
            _freeFrames.TryWrite(slot);
            Interlocked.Increment(ref _processedVideo);
        }

        while (_readyBlocks.TryRead(out int slot))
        {
            AudioBlock b = _blocks[slot];
            _audioPublisher.Publish(b.Qpc, b.Qpc + QpcTime.Rescale(b.Count, b.SampleRate, _frequency), _audioBank.Process(b));
            _freeBlocks.TryWrite(slot);
            Interlocked.Increment(ref _processedAudio);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            _stop.Cancel();
            _worker.Join();
            _stop.Dispose();
            _signal.Dispose();
            _freeFrames.Dispose();
            _readyFrames.Dispose();
            _freeBlocks.Dispose();
            _readyBlocks.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
