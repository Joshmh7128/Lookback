using Lookback.Calibration.Profiles;

namespace Lookback.Calibration.Tests;

public class PatcherTests
{
    private const string Json = """
        {
          // a comment that must survive
          "id": "x",
          "reference": { "calibrated": false },
          "regions": [ { "name": "feed", "x": 0.70, "y": 0.06, "w": 0.29, "h": 0.22 } ],
          "events": [ { "name": "kill", "when": [ { "signal": "region.feed.activity", "rise": 0.02 } ] } ],
        }
        """;

    [Fact]
    public void IndexesValuesByNamedPath()
    {
        var index = ProfileTextPatcher.Index(Json);
        Assert.Contains("regions[feed].x", index.Keys);
        Assert.Contains("events[kill].when[0].rise", index.Keys);
        Assert.Contains("reference.calibrated", index.Keys);
    }

    [Fact]
    public void ChangesOnlyTheNumbersAsked()
    {
        string patched = ProfileTextPatcher.Apply(Json, new Dictionary<string, string>
        {
            ["regions[feed].x"] = "0.688",
            ["events[kill].when[0].rise"] = "0.035",
            ["reference.calibrated"] = "true",
        });

        Assert.Contains("// a comment that must survive", patched, StringComparison.Ordinal);
        Assert.Contains("\"x\": 0.688, \"y\": 0.06", patched, StringComparison.Ordinal);
        Assert.Contains("\"rise\": 0.035 }", patched, StringComparison.Ordinal);
        Assert.Contains("\"calibrated\": true }", patched, StringComparison.Ordinal);
        Assert.Equal(Json.Length + 1, patched.Length); // 0.70 -> 0.688, 0.02 -> 0.035, false -> true
    }

    [Fact]
    public void RefusesAnythingButNumbersAndBooleans()
    {
        Assert.Throws<InvalidOperationException>(() => ProfileTextPatcher.Apply(Json, new Dictionary<string, string> { ["regions[feed].x"] = "\"https://example\"" }));
        Assert.Throws<KeyNotFoundException>(() => ProfileTextPatcher.Apply(Json, new Dictionary<string, string> { ["id"] = "1" }));
    }
}
