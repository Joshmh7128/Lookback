using Lookback.Media.Frames;

namespace Lookback.Recorder.Tests;

public class AnalysisSizeOptionTests
{
    [Theory]
    [InlineData(null, "384x216")]
    [InlineData("", "384x216")]
    [InlineData("768x432", "768x432")]
    [InlineData("1920x1080", "1920x1080")]
    [InlineData("769x432", "384x216")] // invalid: the default, and a warning at start
    [InlineData("big", "384x216")]
    public void Analysis_size_resolves_with_the_default_as_fallback(string? setting, string expected)
    {
        var o = new RecorderOptions { AnalysisSize = setting };
        Assert.Equal(expected, o.ResolvedAnalysisFormat.Size);
    }

    [Fact]
    public void Unset_is_todays_format()
    {
        Assert.Same(AnalysisFormat.Default, new RecorderOptions().ResolvedAnalysisFormat);
    }
}
