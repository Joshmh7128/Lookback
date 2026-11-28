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

    private const string Lines = """
        {
          "id": "x",
          "events": [
            { "name": "kill", "when": [ { "signal": "s", "rise": 0.02 } ] } // the only event
          ]
        }
        """;

    [Fact]
    public void SetAddsAMissingPropertyOnItsOwnLineOrInline()
    {
        string own = ProfileTextPatcher.Set(Lines, "events[kill].weight", "1.5");
        Assert.Contains("\"when\": [ { \"signal\": \"s\", \"rise\": 0.02 } ], \"weight\": 1.5 } // the only event", own, StringComparison.Ordinal);

        string block = ProfileTextPatcher.Set(Lines, "clipper", "{}");
        block = ProfileTextPatcher.Set(block, "clipper.high", "1.8");
        block = ProfileTextPatcher.Set(block, "clipper.low", "0.9");
        Assert.EndsWith("  ],\n  \"clipper\": {\n    \"high\": 1.8,\n    \"low\": 0.9\n  }\n}", block.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        Assert.Throws<KeyNotFoundException>(() => ProfileTextPatcher.Set(Lines, "clipper.high", "1.8")); // no clipper object yet
        Assert.Throws<InvalidOperationException>(() => ProfileTextPatcher.Set(Lines, "events[kill].when[0].rise", "\"x\""));
    }

    [Fact]
    public void RemoveTakesTheLineOrTheSeparatorWithIt()
    {
        string withTwo = ProfileTextPatcher.Append(Lines, "events", "{ \"name\": \"death\", \"when\": [ { \"signal\": \"s\", \"above\": 1 } ] }");
        Assert.Contains("}, // the only event\n", withTwo.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("\n    { \"name\": \"death\"", withTwo.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(Lines.ReplaceLineEndings("\n"), ProfileTextPatcher.Remove(withTwo, "events[death]").ReplaceLineEndings("\n"));

        // Inline: the comma on one side goes with the value.
        Assert.Contains("[ { \"name\": \"feed\", \"y\": 0.06,", ProfileTextPatcher.Remove(Json, "regions[feed].x"), StringComparison.Ordinal);
        Assert.Contains("\"w\": 0.29 } ]", ProfileTextPatcher.Remove(Json, "regions[feed].h"), StringComparison.Ordinal);

        string trailing = ProfileTextPatcher.Remove(Json, "events");
        Assert.DoesNotContain("events", trailing, StringComparison.Ordinal);
        Assert.Contains("\"regions\": [ { \"name\": \"feed\", \"x\": 0.70, \"y\": 0.06, \"w\": 0.29, \"h\": 0.22 } ]\n}", trailing.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Throws<KeyNotFoundException>(() => ProfileTextPatcher.Remove(Json, "clipper"));
    }
}
