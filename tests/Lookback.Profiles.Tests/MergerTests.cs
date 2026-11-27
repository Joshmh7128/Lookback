using Lookback.Contracts.V1;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;

namespace Lookback.Profiles.Tests;

public class MergerTests
{
    private long _wall;

    private TimeOrderedMerger<T> New<T>(long stallTimeout = 1000) => new(stallTimeout, () => _wall);

    [Fact]
    public void ReleasesInTimeOrderOnlyBelowTheWatermark()
    {
        var m = New<string>();
        m.Register(1);
        m.Register(2);
        var output = new List<string>();

        m.Add(1, 10, "v10");
        m.Add(1, 30, "v30");
        m.Release(output);
        Assert.Empty(output); // stream 2 has said nothing yet

        m.Add(2, 20, "a20");
        m.Release(output);
        Assert.Equal(["v10", "a20"], output); // watermark is 20; v30 waits

        m.Add(2, 40, "a40");
        m.Release(output);
        Assert.Equal(["v10", "a20", "v30"], output);

        m.Flush(output);
        Assert.Equal(["v10", "a20", "v30", "a40"], output);
        Assert.Equal(0, m.Pending);
    }

    [Fact]
    public void TiesKeepArrivalOrder()
    {
        var m = New<string>();
        m.Add(1, 5, "first");
        m.Add(2, 5, "second");
        var output = new List<string>();
        m.Release(output);
        Assert.Equal(["first", "second"], output);
    }

    [Fact]
    public void StreamFarBehindInFootageTimeStillHoldsTheWatermark()
    {
        // At 8x replay the audio decoder can run seconds of footage behind video.
        // That must not change the output, so the lagging stream still holds the watermark.
        var m = New<int>();
        m.Register(1);
        m.Register(2);
        var output = new List<int>();
        for (int t = 0; t <= 5000; t += 100)
        {
            m.Add(1, t, t);
        }

        m.Add(2, 50, 50);
        m.Release(output);
        Assert.Equal([0, 50], output);
    }

    [Fact]
    public void SilentStreamStopsHoldingBackAfterTheWallClockTimeout()
    {
        var m = New<int>(stallTimeout: 100);
        m.Register(1);
        m.Register(2); // never sends: a game with no audio device
        var output = new List<int>();

        m.Add(1, 50, 50);
        m.Release(output);
        Assert.Empty(output);

        _wall += 101;
        m.Add(1, 60, 60);
        m.Release(output);
        Assert.Equal([50, 60], output);
    }

    [Fact]
    public void DetectorOutputDoesNotDependOnArrivalSkew()
    {
        GameProfile p = new()
        {
            Id = "t",
            DisplayName = "T",
            Events =
            [
                new EventRule { Name = "loud", CooldownMs = 500, When = [new Condition { Signal = "rms", Rise = 6, BaselineAlpha = 0.05f }] },
            ],
        };

        // 20 s of footage: video every 33 ms, audio every 10 ms, with loud bursts every 3 s.
        var video = new List<(long, SignalFrame)>();
        var audio = new List<(long, SignalFrame)>();
        for (long t = 0; t < 20_000; t += 33)
        {
            video.Add((t, new SignalFrame { SchemaId = 1, WindowStartQpc = (ulong)t, Values = { 0 } }));
        }

        for (long t = 0; t < 20_000; t += 10)
        {
            audio.Add((t, new SignalFrame { SchemaId = 2, WindowStartQpc = (ulong)t, Values = { t % 3000 < 200 ? -10f : -30f } }));
        }

        List<string> Run(int audioDelayItems)
        {
            var d = new ProfileEventDetector(p, 1000);
            d.OnSchema(new SignalSchema { SchemaId = 1, Family = SignalFamily.Video, Names = { "v" } });
            d.OnSchema(new SignalSchema { SchemaId = 2, Family = SignalFamily.Audio, Names = { "rms" } });
            var m = New<SignalFrame>();
            m.Register(1);
            m.Register(2);
            var released = new List<SignalFrame>();
            var events = new List<GameEvent>();

            // Interleave arrivals with audio delayed by a number of video frames.
            int vi = 0, ai = 0;
            while (vi < video.Count || ai < audio.Count)
            {
                if (vi < video.Count)
                {
                    m.Add(1, video[vi].Item1, video[vi].Item2);
                    vi++;
                }

                while (ai < audio.Count && audio[ai].Item1 <= (vi - audioDelayItems) * 33L)
                {
                    m.Add(2, audio[ai].Item1, audio[ai].Item2);
                    ai++;
                }

                if (vi >= video.Count)
                {
                    while (ai < audio.Count)
                    {
                        m.Add(2, audio[ai].Item1, audio[ai].Item2);
                        ai++;
                    }
                }

                m.Release(released);
                foreach (SignalFrame f in released)
                {
                    d.OnFrame(f, events);
                }

                released.Clear();
            }

            m.Flush(released);
            foreach (SignalFrame f in released)
            {
                d.OnFrame(f, events);
            }

            d.Flush(events);
            return [.. events.Select(e => $"{e.Name}@{e.AtQpc}")];
        }

        List<string> inStep = Run(0);
        Assert.Equal(6, inStep.Count);  // bursts at 3, 6, ... 18 s (the one at 0 s is where the baseline starts)
        Assert.Equal(inStep, Run(30));  // audio a second behind
        Assert.Equal(inStep, Run(150)); // audio five seconds behind
    }
}
