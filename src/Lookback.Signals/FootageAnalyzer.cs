using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Ffmpeg;
using Lookback.Media.Frames;
using Lookback.Signals.Audio;
using Lookback.Signals.Video;

namespace Lookback.Signals;

/// <summary>Counts from one analysis run, and the HUD mask learned from the footage (null if warm-up never finished).</summary>
public sealed record FootageAnalysisResult(long VideoFrames, long AudioHops, TimeSpan Elapsed, HudMask? HudMask = null)
{
    /// <summary>The analysis format the frames were converted to.</summary>
    public AnalysisFormat Format { get; init; } = AnalysisFormat.Default;

    /// <summary>Time spent in the video signal bank, summed over every frame (decode and publishing excluded).</summary>
    public TimeSpan VideoSignalTime { get; init; }

    /// <summary>The slowest single frame in the video signal bank.</summary>
    public TimeSpan VideoSignalMax { get; init; }

    /// <summary>Mean video signal bank time per frame, in milliseconds.</summary>
    public double VideoMsPerFrame => VideoFrames == 0 ? 0 : VideoSignalTime.TotalMilliseconds / VideoFrames;
}

/// <summary>A learned HUD mask at analysis resolution; true marks a HUD pixel.</summary>
public sealed record HudMask(int Width, int Height, bool[] Pixels);

/// <summary>Optional parts of a footage run.</summary>
public sealed record FootageAnalysisOptions
{
    /// <summary>False skips the audio decoder (files without an audio stream).</summary>
    public bool HasAudio { get; init; } = true;

    /// <summary>Keeps lossless consumers from falling behind.</summary>
    public ReplayPacer? Pacer { get; init; }

    /// <summary>Reports the media time reached, about once a second of footage.</summary>
    public IProgress<TimeSpan>? Progress { get; init; }

    /// <summary>Extra per-frame signals, such as a game profile's region signals.</summary>
    public IReadOnlyList<IVideoSignal> ExtraVideoSignals { get; init; } = [];
}

/// <summary>
/// Runs a footage file through the video and audio signal banks and
/// publishes everything onto the bus for one session. Video and audio decode
/// in parallel; each publishes with its own timestamps, and consumers (the
/// session log, the clipper) order by QPC rather than arrival.
/// </summary>
public sealed class FootageAnalyzer
{
    private readonly IEventBus _bus;
    private readonly AnalysisFormat _format;
    private readonly long _frequency;

    public FootageAnalyzer(IEventBus bus, AnalysisFormat? format = null, long frequency = 10_000_000)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _format = format ?? AnalysisFormat.Default;
        _frequency = frequency;
    }

    public long Frequency => _frequency;

    public async Task<FootageAnalysisResult> AnalyzeAsync(string path, string sessionId, FootageAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new FootageAnalysisOptions();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var source = new FfmpegFileSource(path, _format, _frequency);
        VideoSignalBank bank = VideoSignalBank.CreateDefault(_format, options.ExtraVideoSignals);

        var timing = new BankTiming();
        Task<long> video = Task.Run(() => RunVideoAsync(source, bank, sessionId, options.Pacer, options.Progress, timing, cancellationToken), cancellationToken);
        Task<long> audio = options.HasAudio
            ? Task.Run(() => RunAudioAsync(source, sessionId, options.Pacer, cancellationToken), cancellationToken)
            : Task.FromResult(0L);

        await Task.WhenAll(video, audio).ConfigureAwait(false);

        HudSignal? hud = bank.Signals.OfType<HudSignal>().FirstOrDefault();
        HudMask? mask = hud is { IsReady: true } ? new HudMask(_format.Width, _format.Height, hud.Mask.ToArray()) : null;
        return new FootageAnalysisResult(await video.ConfigureAwait(false), await audio.ConfigureAwait(false), started.Elapsed, mask)
        {
            Format = _format,
            VideoSignalTime = System.Diagnostics.Stopwatch.GetElapsedTime(0, timing.Total),
            VideoSignalMax = System.Diagnostics.Stopwatch.GetElapsedTime(0, timing.Max),
        };
    }

    private sealed class BankTiming
    {
        public long Total { get; set; }

        public long Max { get; set; }
    }

    private async Task<long> RunVideoAsync(FfmpegFileSource source, VideoSignalBank bank, string sessionId, ReplayPacer? pacer, IProgress<TimeSpan>? progress, BankTiming timing, CancellationToken ct)
    {
        var publisher = new SignalPublisher(_bus, new EnvelopeFactory(new ManualClock(_frequency), sessionId, "signals.video"),
            Topics.SignalsVideo, SignalPublisher.VideoSchemaId, SignalFamily.Video, bank.Names);
        publisher.PublishSchema(0);

        long frameTicks = QpcTime.Rescale(1, _format.FramesPerSecond, _frequency);
        long count = 0;
        await foreach (VideoFrame frame in source.ReadVideoAsync(ct).ConfigureAwait(false))
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ReadOnlySpan<float> values = bank.Process(frame);
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            timing.Total += dt;
            timing.Max = Math.Max(timing.Max, dt);
            publisher.Publish(frame.Qpc, frame.Qpc + frameTicks, values);
            count++;
            if (pacer is not null)
            {
                await pacer.WaitAsync(ct).ConfigureAwait(false);
            }

            if (progress is not null && count % _format.FramesPerSecond == 0)
            {
                progress.Report(QpcTime.ToTimeSpan(frame.Qpc, _frequency));
            }
        }

        return count;
    }

    private async Task<long> RunAudioAsync(FfmpegFileSource source, string sessionId, ReplayPacer? pacer, CancellationToken ct)
    {
        AudioSignalBank bank = AudioSignalBank.CreateDefault(_format);
        var publisher = new SignalPublisher(_bus, new EnvelopeFactory(new ManualClock(_frequency), sessionId, "signals.audio"),
            Topics.SignalsAudio, SignalPublisher.AudioSchemaId, SignalFamily.Audio, bank.Names);
        publisher.PublishSchema(0);

        long count = 0;
        await foreach (AudioBlock block in source.ReadAudioAsync(ct).ConfigureAwait(false))
        {
            long end = block.Qpc + QpcTime.Rescale(block.Count, block.SampleRate, _frequency);
            publisher.Publish(block.Qpc, end, bank.Process(block));
            count++;
            if (pacer is not null && count % 16 == 0)
            {
                await pacer.WaitAsync(ct).ConfigureAwait(false);
            }

        }

        return count;
    }
}
