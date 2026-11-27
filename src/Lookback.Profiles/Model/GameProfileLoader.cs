using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lookback.Profiles.Model;

/// <summary>Loads and validates profiles from the built-in set or from JSON files.</summary>
public static class GameProfileLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a typo in a rule fails loudly
    };

    /// <summary>Ids of the profiles compiled into the assembly.</summary>
    public static IReadOnlyList<string> BuiltInIds { get; } = Assembly.GetExecutingAssembly()
        .GetManifestResourceNames()
        .Where(n => n.StartsWith("profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
        .Select(n => n["profiles.".Length..^".json".Length])
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>A built-in id, or a path to a JSON file.</summary>
    public static GameProfile Load(string idOrPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(idOrPath);
        if (File.Exists(idOrPath))
        {
            return Parse(File.ReadAllText(idOrPath), idOrPath);
        }

        using System.IO.Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"profiles.{idOrPath}.json")
            ?? throw new FileNotFoundException($"No built-in profile '{idOrPath}' (have: {string.Join(", ", BuiltInIds)}) and no such file.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), idOrPath);
    }

    public static GameProfile Parse(string json, string source = "profile")
    {
        GameProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<GameProfile>(json, Json)
                ?? throw new InvalidDataException($"{source}: empty profile.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: {ex.Message}", ex);
        }

        IReadOnlyList<string> problems = Validate(profile);
        if (problems.Count > 0)
        {
            throw new InvalidDataException($"{source} is invalid:\n  " + string.Join("\n  ", problems));
        }

        return profile;
    }

    /// <summary>
    /// Structural checks. Signal names are checked separately against the
    /// signals actually produced (<see cref="ValidateSignals"/>).
    /// </summary>
    public static IReadOnlyList<string> Validate(GameProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(p.Id) || p.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
        {
            problems.Add("id must be lower-case letters, digits and dashes.");
        }

        foreach (HudRegion r in p.Regions)
        {
            if (r.W <= 0 || r.H <= 0 || r.X < 0 || r.Y < 0 || r.X + r.W > 1.0001 || r.Y + r.H > 1.0001)
            {
                problems.Add($"region '{r.Name}' must lie inside the 0..1 frame.");
            }

            if (r.Name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
            {
                problems.Add($"region '{r.Name}' name must be lower-case letters, digits and underscores.");
            }
        }

        if (p.Regions.Select(r => r.Name).Distinct().Count() != p.Regions.Count)
        {
            problems.Add("region names must be unique.");
        }

        var eventNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (EventRule e in p.Events)
        {
            if (!eventNames.Add(e.Name))
            {
                problems.Add($"event '{e.Name}' is defined twice.");
            }

            if (e.When.Count == 0 && e.WhenAny.Count == 0)
            {
                problems.Add($"event '{e.Name}' has no conditions.");
            }

            foreach (Condition c in e.When.Concat(e.WhenAny))
            {
                int set = (c.Above.HasValue ? 1 : 0) + (c.Below.HasValue ? 1 : 0) + (c.Rise.HasValue ? 1 : 0);
                if (set != 1)
                {
                    problems.Add($"event '{e.Name}': condition on '{c.Signal}' must set exactly one of above, below, rise.");
                }

                if (c.BaselineAlpha is <= 0 or > 1)
                {
                    problems.Add($"event '{e.Name}': baselineAlpha must be in (0, 1].");
                }
            }

            if (e.BaseConfidence is < 0 or > 1)
            {
                problems.Add($"event '{e.Name}': baseConfidence must be in 0..1.");
            }
        }

        foreach (CompositeRule c in p.Composites)
        {
            if (!eventNames.Contains(c.Of))
            {
                problems.Add($"composite '{c.Name}' refers to unknown event '{c.Of}'.");
            }

            if (!eventNames.Add(c.Name))
            {
                problems.Add($"composite '{c.Name}' reuses an event name.");
            }

            if (c.AtLeast < 2)
            {
                problems.Add($"composite '{c.Name}': atLeast must be 2 or more.");
            }
        }

        return problems;
    }

    /// <summary>Signals the profile's rules use that are not in <paramref name="available"/>.</summary>
    public static IReadOnlyList<string> ValidateSignals(GameProfile p, IReadOnlySet<string> available)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(available);
        return p.Events
            .SelectMany(e => e.When.Concat(e.WhenAny).Select(c => c.Signal).Concat(e.Boosts.Select(b => b.Signal)))
            .Where(s => !available.Contains(s))
            .Distinct()
            .ToArray();
    }
}
