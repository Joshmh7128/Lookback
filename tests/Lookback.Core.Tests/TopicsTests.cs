using Lookback.Contracts;

namespace Lookback.Core.Tests;

public class TopicsTests
{
    [Theory]
    [InlineData("*", "signals.video", true)]
    [InlineData("signals.*", "signals.video", true)]
    [InlineData("signals.*", "signals", false)]
    [InlineData("signals.*", "signalsx.video", false)]
    [InlineData("clip.candidate", "clip.candidate", true)]
    [InlineData("clip.candidate", "clip.saved", false)]
    public void Matches(string pattern, string topic, bool expected) =>
        Assert.Equal(expected, Topics.Matches(pattern, topic));
}
