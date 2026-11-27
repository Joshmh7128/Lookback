using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;
using static Lookback.Clipper.Tests.Synth;

namespace Lookback.Clipper.Tests;

public class RunnerTests
{
    [Fact]
    public async Task FollowsTheProfileRunnerAndPublishesOneCandidateSetWithQa()
    {
        ProfileCatalog catalog = new([EngineTests.Game("{}")]);
        using var bus = new InProcessEventBus();
        using IEventSubscription all = bus.Subscribe("*", capacity: 1 << 18);
        var profile = new ProfileRunner(bus, catalog, "g", DetectionMethod.Manual, "s", F, publishCandidates: false);
        var clipper = new ClipperRunner(bus, catalog, "g", "s", F);

        var signals = new EnvelopeFactory(new ManualClock(F), "s", "test");
        Publish(bus, signals, Topics.SignalsVideo, 0, e => e.SignalSchema = Video);
        Publish(bus, signals, Topics.SignalsAudio, 0, e => e.SignalSchema = Audio);
        foreach (SignalFrame f in Session(40, t => Between(t, 15, 15.3)))
        {
            Publish(bus, signals, f.SchemaId == 1 ? Topics.SignalsVideo : Topics.SignalsAudio, (long)f.WindowStartQpc, e => e.SignalFrame = f);
        }

        // Another session on the same bus is ignored.
        var other = new EnvelopeFactory(new ManualClock(F), "other", "test");
        Publish(bus, other, Topics.GameEvent, 0, e => e.GameEvent = EngineTests.Event("kill", 30, 1, 1, 8, 3, "g"));

        await profile.DisposeAsync(); // the profile runner first: the clipper waits on its events
        await clipper.DisposeAsync();
        Assert.Equal(0, clipper.Dropped);

        var seen = new List<Envelope>();
        while (all.TryRead(out Envelope? e))
        {
            seen.Add(e!);
        }

        Envelope[] candidates = [.. seen.Where(e => e.Topic == Topics.ClipCandidate)];
        Envelope only = Assert.Single(candidates); // the event and the curve peak deduped; the profile runner published none
        Assert.Equal("clipper", only.SourceId);
        ClipCandidate c = only.ClipCandidate;
        Assert.Contains(c.Reasons, r => r.StartsWith("kill ×1", StringComparison.Ordinal));
        Assert.Contains(c.Reasons, r => r.StartsWith("excitement ×1", StringComparison.Ordinal));
        Assert.Equal("g", c.GameProfile);
        Assert.Equal(c.PeakQpc, only.Qpc); // stamped with the moment, in raw ticks

        // Its QA report is the clipper's next message.
        int at = seen.IndexOf(only);
        Envelope qa = seen.Skip(at + 1).First(e => e.SourceId == "clipper");
        Assert.Equal(Topics.QaReport, qa.Topic);
        Assert.Equal(c.CandidateId, qa.QaReport.CandidateId);
        Assert.True(qa.QaReport.Passed);

        // The curve, at 10 Hz, for viewers.
        Envelope schema = seen.First(e => e.Topic == Topics.SignalsClipper);
        Assert.Equal(SignalFamily.Clipper, schema.SignalSchema.Family);
        Assert.Equal(ClipperRunner.CurveSchemaId, schema.SignalSchema.SchemaId);
        Assert.Equal("clipper.excitement", schema.SignalSchema.Names[0]);
        int curveFrames = seen.Count(e => e.Topic == Topics.SignalsClipper && e.PayloadCase == Envelope.PayloadOneofCase.SignalFrame);
        Assert.InRange(curveFrames, 390, 410);
        Assert.True(seen.Where(e => e.Topic == Topics.SignalsClipper && e.PayloadCase == Envelope.PayloadOneofCase.SignalFrame)
            .Max(e => e.SignalFrame.Values[0]) > 2);
    }

    [Fact]
    public async Task WithoutAProfileTheCurveAloneMakesCandidates()
    {
        using var bus = new InProcessEventBus();
        using IEventSubscription candidates = bus.Subscribe(Topics.ClipCandidate, capacity: 1024);
        using IEventSubscription qa = bus.Subscribe(Topics.QaReport, capacity: 1024);
        var clipper = new ClipperRunner(bus, "s", F, new ClipperOptions { CurvePublishIntervalMs = 0 });
        var finished = new List<ClipperOutput>();
        clipper.CandidateFinished += (_, e) => finished.Add(e.Output);
        var signals = new EnvelopeFactory(new ManualClock(F), "s", "test");
        Publish(bus, signals, Topics.SignalsVideo, 0, e => e.SignalSchema = Video);
        Publish(bus, signals, Topics.SignalsAudio, 0, e => e.SignalSchema = Audio);
        foreach (SignalFrame f in Session(30, t => Between(t, 15, 15.3)))
        {
            Publish(bus, signals, f.SchemaId == 1 ? Topics.SignalsVideo : Topics.SignalsAudio, (long)f.WindowStartQpc, e => e.SignalFrame = f);
        }

        await clipper.DisposeAsync();
        Assert.True(candidates.TryRead(out Envelope? c));
        Assert.Equal("", c!.ClipCandidate.GameProfile);
        Assert.False(candidates.TryRead(out _));
        Assert.True(qa.TryRead(out Envelope? q));
        Assert.Equal(c.ClipCandidate.CandidateId, q!.QaReport.CandidateId);
        Assert.Equal(1, clipper.CandidatesPublished);

        // The same candidate, with its features, in memory.
        ClipperOutput o = Assert.Single(finished);
        Assert.Equal(c.ClipCandidate, o.Candidate);
        Assert.Equal(clipper.FeatureNames, o.Features.Names);
        Assert.Equal(0f, o.Features["source.events"]);
    }

    private static void Publish(InProcessEventBus bus, EnvelopeFactory factory, string topic, long qpc, Action<Envelope> fill)
    {
        Envelope e = factory.Create(topic, qpc);
        fill(e);
        bus.Publish(e);
    }
}
