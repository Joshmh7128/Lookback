using System.Diagnostics;
using Lookback.Core.Diagnostics;
using Lookback.Core.Time;
using Lookback.Encode;
using Lookback.Replay.Packets;
using Vortice.Direct3D11;

namespace Lookback.Capture.Tests;

/// <summary>
/// Fault injection for the encode path: encoders that hang inside a call,
/// accept frames but never output, or all fail. The ticker must swap in the
/// next encoder on the same tick grid, remember the bad one, never wait on it,
/// and end the process (here: a recorded fatal) only when nothing is left.
/// No GPU needed; Windows only because of the waitable timer.
/// </summary>
public sealed class EncodeTickerFaultTests : IDisposable
{
    private const int Fps = 60;
    private static readonly TimeSpan StallAfter = TimeSpan.FromMilliseconds(300);
    private readonly ManualResetEventSlim _release = new(); // lets hung fakes return once the test is over
    private readonly List<string> _fatal = [];
    private readonly List<string> _log = [];
    private readonly StallWatchdog _dog = new(SystemClock.Instance, TimeSpan.FromMilliseconds(25));

    public void Dispose()
    {
        _release.Set();
        _dog.Dispose();
        _release.Dispose();
    }

    private (EncodeTicker Ticker, Dictionary<string, FakeEncoder> Made) Start(EncoderBlocklist? blocklist, int maxRecoveries, params (string Name, Fault Fault)[] chain)
    {
        var made = new Dictionary<string, FakeEncoder>();
        var settings = new VideoEncoderSettings(CodecKind.H264, 1280, 720, Fps, 6_000_000);
        var candidates = chain.Select(c => new EncoderCandidate(CodecKind.H264, c.Name, Hardware: true)).ToList();
        var options = new EncodeTickerOptions
        {
            Watchdog = _dog,
            StallAfter = StallAfter,
            MaxRecoveries = maxRecoveries,
            Blocklist = blocklist,
            Fatal = m => { lock (_fatal) { _fatal.Add(m); } },
            Log = m => { lock (_log) { _log.Add(m); } },
        };
        EncodeTicker ticker = EncodeTicker.StartForTest(SystemClock.Instance, Fps, candidates, c =>
        {
            var e = new FakeEncoder(c, settings, chain.First(x => x.Name == c.Name).Fault, _release);
            lock (made)
            {
                made[c.Name] = e;
            }

            return e;
        }, options);
        ticker.SimulateFrame(SystemClock.Instance.Now);
        return (ticker, made);
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan within)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > within)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    [Fact]
    public void A_hung_encoder_is_abandoned_and_the_next_one_takes_over_on_the_same_grid()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the ticker's timer is Windows only");
        var blocklist = new EncoderBlocklist(path: null);
        (EncodeTicker ticker, Dictionary<string, FakeEncoder> made) = Start(blocklist, 3, ("hangs", Fault.HangInEncode), ("works", Fault.None));
        using (ticker)
        {
            Assert.True(WaitFor(() => made.TryGetValue("works", out FakeEncoder? w) && w.FramesIn > 30, TimeSpan.FromSeconds(5)), string.Join(" | ", _log));
            Assert.Equal("works", ticker.EncoderName);
            Assert.Equal(1, ticker.Stats.Recoveries);
        }

        Assert.True(made["hangs"].Abandoned);
        Assert.False(made["hangs"].Disposed); // never waited on while stuck
        Assert.Empty(_fatal);
        Assert.True(blocklist.IsBlocked("test", made["hangs"].Candidate));
        Assert.Contains(_log, m => m.Contains("switched to works", StringComparison.Ordinal));

        // The replacement continues on the original tick grid: every PTS is origin + floor(n * freq / fps).
        long freq = SystemClock.Instance.Frequency;
        long origin = made["hangs"].Times[0];
        long previous = -1;
        foreach (long t in made["works"].Times)
        {
            long n = (long)Math.Round((double)(t - origin) * Fps / freq);
            long onGrid = origin + QpcTime.Rescale(n, Fps, freq);
            Assert.True(Math.Abs(t - onGrid) <= 1, $"PTS {t} is {t - onGrid} ticks off tick {n}");
            Assert.True(n > previous, "ticks must keep increasing across the swap");
            previous = n;
        }
    }

    [Fact]
    public void An_encoder_that_takes_frames_but_never_outputs_is_replaced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the ticker's timer is Windows only");
        (EncodeTicker ticker, Dictionary<string, FakeEncoder> made) = Start(null, 3, ("silent", Fault.NoOutput), ("works", Fault.None));
        using (ticker)
        {
            Assert.True(WaitFor(() => made.ContainsKey("works") && made["works"].FramesIn > 30, TimeSpan.FromSeconds(5)), string.Join(" | ", _log));
        }

        Assert.True(made["silent"].Abandoned);
        Assert.Contains(_log, m => m.Contains("no packets", StringComparison.Ordinal));
        Assert.Empty(_fatal);
    }

    [Fact]
    public void An_encoder_that_refuses_all_input_is_replaced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the ticker's timer is Windows only");
        (EncodeTicker ticker, Dictionary<string, FakeEncoder> made) = Start(null, 3, ("refuses", Fault.RefuseInput), ("works", Fault.None));
        using (ticker)
        {
            Assert.True(WaitFor(() => made.ContainsKey("works") && made["works"].FramesIn > 30, TimeSpan.FromSeconds(5)), string.Join(" | ", _log));
        }

        Assert.True(made["refuses"].Abandoned);
        Assert.Empty(_fatal);
    }

    [Fact]
    public void When_every_encoder_stalls_the_process_is_ended_instead_of_hanging()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the ticker's timer is Windows only");
        (EncodeTicker ticker, Dictionary<string, FakeEncoder> made) = Start(null, 3, ("first", Fault.HangInEncode), ("second", Fault.HangInEncode));
        using (ticker)
        {
            Assert.True(WaitFor(() => { lock (_fatal) { return _fatal.Count > 0; } }, TimeSpan.FromSeconds(5)), string.Join(" | ", _log));
        }

        string reason = Assert.Single(_fatal);
        Assert.Contains("no encoder left", reason, StringComparison.Ordinal);
        Assert.Contains("second", reason, StringComparison.Ordinal);
        Assert.True(made["first"].Abandoned);
        Assert.True(made["second"].Abandoned);
    }

    [Fact]
    public void Too_many_swaps_in_one_session_end_the_process()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the ticker's timer is Windows only");
        (EncodeTicker ticker, _) = Start(null, maxRecoveries: 1, ("a", Fault.HangInEncode), ("b", Fault.HangInEncode), ("c", Fault.None));
        using (ticker)
        {
            Assert.True(WaitFor(() => { lock (_fatal) { return _fatal.Count > 0; } }, TimeSpan.FromSeconds(5)), string.Join(" | ", _log));
        }

        Assert.Contains("stalled 2 times", Assert.Single(_fatal), StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_returns_promptly_even_with_a_stuck_encoder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the ticker's timer is Windows only");
        var options = new EncodeTickerOptions { Fatal = m => _fatal.Add(m) }; // no watchdog: nothing rescues it before Dispose
        FakeEncoder? stuck = null;
        var settings = new VideoEncoderSettings(CodecKind.H264, 1280, 720, Fps, 6_000_000);
        EncodeTicker ticker = EncodeTicker.StartForTest(SystemClock.Instance, Fps, [new EncoderCandidate(CodecKind.H264, "stuck", true)], c => stuck = new FakeEncoder(c, settings, Fault.HangInEncode, _release), options);
        ticker.SimulateFrame(SystemClock.Instance.Now);
        Assert.True(WaitFor(() => stuck!.InEncode, TimeSpan.FromSeconds(2)));

        var sw = Stopwatch.StartNew();
        ticker.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Dispose took {sw.Elapsed}");
        Assert.True(stuck!.Abandoned);
    }

    [Fact]
    public void The_blocklist_survives_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lookback-blocklist-{Guid.NewGuid():N}.json");
        try
        {
            var c = new EncoderCandidate(CodecKind.Hevc, "AMDh265Encoder", true);
            new EncoderBlocklist(path).Add("VEN_1002&DEV_164E 31.0.1.1", c, "stalled in Handoff for 2.0 s");
            var reloaded = new EncoderBlocklist(path);
            Assert.True(reloaded.IsBlocked("VEN_1002&DEV_164E 31.0.1.1", c));
            Assert.False(reloaded.IsBlocked("VEN_1002&DEV_164E 32.0.0.0", c)); // a driver update gets a fresh chance
            Assert.False(reloaded.IsBlocked("VEN_1002&DEV_164E 31.0.1.1", c with { Codec = CodecKind.H264 }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public enum Fault
    {
        None,
        HangInEncode,
        NoOutput,
        RefuseInput,
    }

    /// <summary>An encoder that behaves, or misbehaves, on purpose.</summary>
    private sealed class FakeEncoder(EncoderCandidate candidate, VideoEncoderSettings settings, Fault fault, ManualResetEventSlim release) : IVideoEncoder
    {
        private long _in;
        private long _out;
        private volatile bool _inEncode;

        public EncoderCandidate Candidate { get; } = candidate;

        public VideoEncoderSettings Settings { get; } = settings;

        public long FramesIn => Interlocked.Read(ref _in);

        public long PacketsOut => Interlocked.Read(ref _out);

        public long Dropped => 0;

        public double Load => 0;

        public string Phase => _inEncode ? "fake: inside Encode" : "fake: idle";

        public bool Abandoned { get; private set; }

        public bool Disposed { get; private set; }

        public bool InEncode => _inEncode;

        public List<long> Times { get; } = [];

        public bool Encode(ID3D11Texture2D nv12, long qpc)
        {
            lock (Times)
            {
                Times.Add(qpc);
            }

            switch (fault)
            {
                case Fault.HangInEncode:
                    _inEncode = true;
                    release.Wait(); // stuck in the "driver" until the test ends
                    _inEncode = false;
                    return false;
                case Fault.RefuseInput:
                    return false;
                case Fault.NoOutput:
                    Interlocked.Increment(ref _in);
                    return true;
                default:
                    Interlocked.Increment(ref _in);
                    Interlocked.Increment(ref _out);
                    return true;
            }
        }

        public void ForceKeyframe()
        {
        }

        public void Abandon() => Abandoned = true;

        public void Dispose() => Disposed = true;
    }
}
