using Lookback.Calibration.Probe;
using Lookback.Core.Time;
using Lookback.Profiles.Model;

namespace Lookback.Calibration.Tests;

public class TunerTests
{
    private const long Hz = 10_000_000;

    [Fact]
    public void ProposesAThresholdThatCatchesTheMarksAndLittleElse()
    {
        var rule = new EventRule
        {
            Name = "elimination",
            CooldownMs = 250,
            When = [new Condition { Signal = "region.kill_feed.activity", Rise = 0.5f }], // far too high to start with
        };
        var tuner = new ThresholdTuner([("cs2", rule)], ["luma.mean", "region.kill_feed.activity"], Hz);
        var rng = new Random(3);
        float[] values = new float[2];
        for (int i = 0; i < 3600; i++)
        {
            values[1] = (float)(rng.NextDouble() * 0.01) + (i % 300 is >= 150 and < 153 ? 0.12f : 0);
            tuner.OnFrame(QpcTime.Rescale(i, 30, Hz), values);
            if (i % 300 == 165)
            {
                tuner.Mark("elimination", QpcTime.Rescale(i, 30, Hz));
            }
        }

        ThresholdProposal t = Assert.Single(tuner.Propose("elimination"));
        Assert.Equal(new ConditionKey("cs2", "elimination", 0), t.Key);
        Assert.Equal(0, t.CurrentCaught);
        Assert.Equal(t.Marks, t.Caught);
        Assert.Equal(0, t.FalsePerMinute);
        Assert.InRange(t.Proposed, 0.02f, 0.12f);
    }
}
