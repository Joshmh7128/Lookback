using Lookback.Calibration.Probe;
using Lookback.Core.Time;
using Lookback.Media.Frames;
using Lookback.Profiles.Model;
using Lookback.Signals.Video;

namespace Lookback.Calibration.Tests;

public class ProbeTests
{
    private const long Hz = 10_000_000;

    private static CalibrationProbe Run(Synthetic s, int frames, Func<int, bool> lit, IEnumerable<int> markAt)
    {
        var probe = new CalibrationProbe(AnalysisFormat.Default, Hz);
        var frame = new VideoFrame(Synthetic.W, Synthetic.H);
        var luma = new LumaPlane(Synthetic.W, Synthetic.H);
        var marks = markAt.ToHashSet();
        for (int i = 0; i < frames; i++)
        {
            s.Frame(lit(i)).CopyTo(frame.Pixels);
            frame.Qpc = QpcTime.Rescale(i, 30, Hz);
            luma.Compute(frame);
            probe.Process(frame, luma, []);
            if (marks.Contains(i))
            {
                probe.Mark("elimination", frame.Qpc);
            }
        }

        return probe;
    }

    [Fact]
    public void FindsTheBoxThatLightsUpAtEveryMark()
    {
        var s = new Synthetic();
        // The feed lights for 6 frames every 4 s; the user marks 0.5 s after each.
        static bool Lit(int i) => i % 120 is >= 60 and < 66;
        CalibrationProbe probe = Run(s, 1200, Lit, Enumerable.Range(0, 10).Select(k => (k * 120) + 75));

        var estimate = new HudRegion { Name = "kill_feed", X = 0.70, Y = 0.06, W = 0.29, H = 0.22 };
        RegionProposal p = probe.ProposeFromMarks("elimination", estimate)!;

        Assert.NotNull(p);
        Assert.Equal("marks", p.Source);
        Assert.Equal(280.0 / 384, p.X, 0.006);
        Assert.Equal(24.0 / 216, p.Y, 0.006);
        Assert.Equal(88.0 / 384, p.W, 0.006);
        Assert.Equal(48.0 / 216, p.H, 0.006);
    }

    [Fact]
    public void FindsTheStaticHud()
    {
        var s = new Synthetic();
        CalibrationProbe probe = Run(s, 300, _ => false, []);
        var estimate = new HudRegion { Name = "health", X = 0.0, Y = 0.85, W = 0.22, H = 0.15 };

        RegionProposal p = probe.ProposeStatic(estimate)!;

        Assert.NotNull(p);
        Assert.Equal(16.0 / 384, p.X, 0.006);
        Assert.Equal(176.0 / 216, p.Y, 0.006);
        Assert.Equal(80.0 / 384, p.W, 0.006);
        Assert.Equal(32.0 / 216, p.H, 0.006);
    }

    [Fact]
    public void NeedsThreeMarks()
    {
        CalibrationProbe probe = Run(new Synthetic(), 300, i => i % 120 is >= 60 and < 66, [75, 195]);
        Assert.Null(probe.ProposeFromMarks("elimination", new HudRegion { Name = "f", X = 0.7, Y = 0.06, W = 0.29, H = 0.22 }));
    }
}
