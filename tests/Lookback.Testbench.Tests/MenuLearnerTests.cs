using Lookback.Profiles.Model;
using Lookback.Signals.Video;
using Lookback.Testbench.Datasets;

namespace Lookback.Testbench.Tests;

public class MenuLearnerTests
{
    private static float[] Frame(Random rng, float baseValue, float wobble, int noisyCell = -1, float noise = 0)
    {
        float[] f = new float[ColorGridSignal.Length];
        for (int k = 0; k < f.Length; k++)
        {
            float jitter = (float)((rng.NextDouble() - 0.5) * 2 * wobble);
            if (k / ColorGridSignal.Channels == noisyCell)
            {
                jitter += (float)((rng.NextDouble() - 0.5) * 2 * noise);
            }

            f[k] = Math.Clamp(baseValue + jitter, 0, 1);
        }

        return f;
    }

    [Fact]
    public void LearnsASteadyScreenAndLeavesOutItsBusyCell()
    {
        var rng = new Random(3);
        // The menu: dark, steady, except cell 7 (an animated hero) which swings widely.
        List<float[]> menu = [.. Enumerable.Range(0, 200).Select(_ => Frame(rng, 0.1f, 0.01f, noisyCell: 7, noise: 0.4f))];
        List<float[]> play = [.. Enumerable.Range(0, 400).Select(_ => Frame(rng, 0.5f, 0.2f))];
        LearnedMenuState s = MenuLearner.Learn("pause", menu, play, []);

        Assert.Null(s.Problem);
        Assert.NotNull(s.State);
        Assert.Null(s.State!.Colors[7]);
        Assert.Equal(ColorGridSignal.Cells - 1, s.ComparedCells);
        Assert.True(s.Recall >= 0.95, MenuLearner.Describe(s));
        Assert.Equal(0, s.PlayMatchRate);
        Assert.InRange(s.State.MaxDistance, 0.01f, 0.1f);
        Assert.Empty(GameProfileLoader.ValidateMenus(new MenuSpec { States = [s.State] }));
    }

    [Fact]
    public void AScreenThatLooksLikePlayIsNotLearned()
    {
        var rng = new Random(4);
        List<float[]> menu = [.. Enumerable.Range(0, 100).Select(_ => Frame(rng, 0.5f, 0.2f))];
        List<float[]> play = [.. Enumerable.Range(0, 100).Select(_ => Frame(rng, 0.5f, 0.2f))];
        LearnedMenuState s = MenuLearner.Learn("loading", menu, play, []);
        Assert.Null(s.State);
        Assert.Contains("cannot be told apart", s.Problem, StringComparison.Ordinal);

        Assert.Contains("fewer than 5", MenuLearner.Learn("x", [menu[0]], play, []).Problem, StringComparison.Ordinal);
        Assert.Contains("no play", MenuLearner.Learn("x", menu, [], []).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void LabelsSplitFramesIntoStatesPlayAndEdges()
    {
        ScreenLabel[] screens = [new("pause", 10, 20), new(MenuLearner.Ignore, 30, 40)];
        Assert.Equal("", MenuLearner.Classify(screens, 5));        // play
        Assert.Null(MenuLearner.Classify(screens, 9.5));           // near an edge: left out
        Assert.Equal("pause", MenuLearner.Classify(screens, 15));
        Assert.Null(MenuLearner.Classify(screens, 19.5));
        Assert.Null(MenuLearner.Classify(screens, 35));            // ignored footage
        Assert.Equal("", MenuLearner.Classify(screens, 45));
    }
}
