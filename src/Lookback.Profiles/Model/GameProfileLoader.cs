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

        if (string.IsNullOrWhiteSpace(p.Id) || p.Id.Split('.').Any(part => part.Length == 0 || part.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))))
        {
            problems.Add("id must be lower-case letters, digits and dashes, with dots between levels.");
        }

        ValidateHierarchy(p, problems);

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
            if (!eventNames.Contains(c.Of) && string.IsNullOrEmpty(p.Parent)) // a child may build on inherited events; the catalog checks those
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

    private static void ValidateHierarchy(GameProfile p, List<string> problems)
    {
        bool root = string.IsNullOrEmpty(p.Parent);
        if (root != (p.Level == ProfileLevelKind.Game))
        {
            problems.Add("a game has no parent; a subgame or mode must name one.");
        }

        if (!root && !p.Id.StartsWith(p.Parent + ".", StringComparison.Ordinal))
        {
            problems.Add($"id must start with the parent id: '{p.Parent}.'.");
        }

        if (!root && p.Executables.Count > 0)
        {
            problems.Add("only a game lists executables; children are detected beneath it.");
        }

        if (root && (p.Detect.Hints.Count > 0 || p.Detect.Visual.Count > 0))
        {
            problems.Add("a game is detected by its executables; detect hints and fingerprints are for subgames and modes.");
        }

        foreach (Condition c in p.Detect.Visual)
        {
            if ((c.Above.HasValue ? 1 : 0) + (c.Below.HasValue ? 1 : 0) != 1 || c.Rise.HasValue)
            {
                problems.Add($"fingerprint condition on '{c.Signal}' must set exactly one of above or below.");
            }
        }

        if (p.Detect.MinScore is <= 0 or > 1 || p.Detect.WindowSeconds <= 0 || p.Detect.HoldSeconds < 0)
        {
            problems.Add("detect: minScore must be in (0, 1], windowSeconds positive, holdSeconds not negative.");
        }

        if (!root && p.IdHints.Count > 0)
        {
            problems.Add("only a game has idHints; children list their ids.");
        }

        foreach (IdHint h in p.IdHints)
        {
            try
            {
                if (new System.Text.RegularExpressions.Regex(h.Regex, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)).GetGroupNumbers().Length < 2)
                {
                    problems.Add($"id hint '{h.Regex}' needs a capture group for the id.");
                }
            }
            catch (ArgumentException ex)
            {
                problems.Add($"id hint '{h.Regex}' is not a valid regular expression: {ex.Message}");
            }
        }

        foreach ((string kind, IReadOnlyList<string> values) in p.Ids)
        {
            if (values.Any(v => v.Length == 0 || !v.All(char.IsAsciiDigit)) && kind.StartsWith("roblox.", StringComparison.Ordinal))
            {
                problems.Add($"ids.{kind}: Roblox ids are numbers.");
            }
        }

        foreach (DetectHint h in p.Detect.Hints)
        {
            try
            {
                var re = new System.Text.RegularExpressions.Regex(h.Regex, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
                if (h.Values.Count > 0 && re.GetGroupNumbers().Length < 2)
                {
                    problems.Add($"hint '{h.Regex}' lists values but has no capture group to compare them with.");
                }
            }
            catch (ArgumentException ex)
            {
                problems.Add($"hint '{h.Regex}' is not a valid regular expression: {ex.Message}");
            }
        }
    }

    /// <summary>Signals the profile's rules use that are not in <paramref name="available"/>.</summary>
    public static IReadOnlyList<string> ValidateSignals(GameProfile p, IReadOnlySet<string> available)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(available);
        return p.Events
            .SelectMany(e => e.When.Concat(e.WhenAny).Select(c => c.Signal).Concat(e.Boosts.Select(b => b.Signal)))
            .Concat(p.Detect.Visual.Select(c => c.Signal))
            .Where(s => !available.Contains(s))
            .Distinct()
            .ToArray();
    }
}
