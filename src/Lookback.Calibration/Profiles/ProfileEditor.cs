using System.Globalization;
using System.Text;
using System.Text.Json;
using Lookback.Clipper;
using Lookback.Profiles.Model;

namespace Lookback.Calibration.Profiles;

/// <summary>
/// The user's own edits to a profile's weights and thresholds, made in the
/// desktop app's profile editor. Numbers left out are not touched.
/// <list type="bullet">
/// <item>Rule weights and thresholds are written to the file that defines the
/// rule (an inherited rule changes for every profile that inherits it).</item>
/// <item>Clipper settings are overrides in the edited profile's own
/// <c>clipper</c> block; a <c>null</c> removes the override, so the value is
/// inherited again (from the parent, then the clipper's defaults).</item>
/// </list>
/// </summary>
public sealed record ProfileEdits
{
    public IReadOnlyList<RuleEdit> Events { get; init; } = [];

    public IReadOnlyList<CompositeEdit> Composites { get; init; } = [];

    /// <summary>
    /// <c>high</c>, <c>low</c>, <c>preMs</c>, <c>postMs</c>,
    /// <c>refractoryMs</c>, <c>peakScore</c>: a value sets this profile's
    /// override, <c>null</c> removes it.
    /// </summary>
    public IReadOnlyDictionary<string, double?> Clipper { get; init; } = new Dictionary<string, double?>(StringComparer.Ordinal);

    /// <summary>Component weights by signal: a value sets this profile's override (0 switches the signal off), <c>null</c> removes it.</summary>
    public IReadOnlyDictionary<string, double?> Components { get; init; } = new Dictionary<string, double?>(StringComparer.Ordinal);

    public bool IsEmpty => Events.Count == 0 && Composites.Count == 0 && Clipper.Count == 0 && Components.Count == 0;

    /// <summary>The clipper numbers a profile can override, and whether each is a whole number of milliseconds.</summary>
    public static IReadOnlyDictionary<string, bool> ClipperFields { get; } = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["high"] = false,
        ["low"] = false,
        ["preMs"] = true,
        ["postMs"] = true,
        ["refractoryMs"] = true,
        ["peakScore"] = false,
    };
}

/// <summary>A rule's weight and thresholds, by condition index (<c>when</c> first, then <c>whenAny</c>, as calibration numbers them).</summary>
public sealed record RuleEdit
{
    public required string Name { get; init; }

    public double? Weight { get; init; }

    public IReadOnlyDictionary<int, double> Thresholds { get; init; } = new Dictionary<int, double>();
}

/// <summary>A composite rule's weight.</summary>
public sealed record CompositeEdit
{
    public required string Name { get; init; }

    public required double Weight { get; init; }
}

/// <summary>What a save changed.</summary>
public sealed record ProfileSaveResult(IReadOnlyList<string> Files, ProfileView View);

/// <summary>
/// Shows a profile with everything it inherits, saying where each number
/// comes from, and saves the user's edits to weights and thresholds into the
/// profiles folder. Numbers are changed in place, and an override is added or
/// removed on its own line, so a profile's git diff shows exactly what
/// changed. Every save is checked by loading the whole folder the way the
/// recorder does (<see cref="ProfileFolder.Load"/>) before a byte is written,
/// and must change nothing but weights, thresholds and the <c>clipper</c>
/// block: never a region, a signal, a rule's conditions or the capture policy.
/// These are the user's own edits in their checkout, not data derived from a
/// watched session, so they are not a <c>RetentionRules</c> derived write.
/// </summary>
public sealed class ProfileEditor(string profilesDirectory)
{
    /// <summary>The source of a value that no profile in the path sets.</summary>
    public const string Defaults = "defaults";

    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions Compare = new(JsonSerializerDefaults.Web);

    public string Directory { get; } = Path.GetFullPath(profilesDirectory ?? throw new ArgumentNullException(nameof(profilesDirectory)));

    /// <summary>Every profile in the folder, games first and each followed by its children.</summary>
    public IReadOnlyList<ProfileSummary> List()
    {
        ProfileCatalog catalog = ProfileFolder.Load(Directory);
        return [.. catalog.All.Where(p => string.IsNullOrEmpty(p.Parent)).OrderBy(p => p.Id, StringComparer.Ordinal)
            .SelectMany(g => catalog.Subtree(g.Id))
            .Select(p => new ProfileSummary(p.Id, p.DisplayName, p.Parent, Level(p), catalog.PathTo(p.Id).Count - 1))];
    }

    /// <summary>The profile <paramref name="id"/> resolved, with the source of every number.</summary>
    public ProfileView View(string id)
    {
        Dictionary<string, string> files = ProfileFolder.Files(Directory);
        return Build(ProfileFolder.Load(Directory), files, id);
    }

    /// <summary>Saves <paramref name="edits"/> to <paramref name="id"/>'s tree; returns the files changed and the new view.</summary>
    /// <exception cref="InvalidDataException">The result would not load; nothing was written. The message is the loader's.</exception>
    /// <exception cref="ArgumentException">An edit names something the profile does not have, or a value out of range.</exception>
    public ProfileSaveResult Save(string id, ProfileEdits edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        Dictionary<string, string> files = ProfileFolder.Files(Directory);
        ProfileCatalog catalog = ProfileFolder.Load(Directory);
        if (!catalog.Contains(id))
        {
            throw new ArgumentException($"No profile '{id}' in {Directory}.");
        }

        if (edits.IsEmpty)
        {
            return new ProfileSaveResult([], Build(catalog, files, id));
        }

        GameProfile resolved = catalog.Resolve(id);
        IReadOnlyList<GameProfile> path = catalog.PathTo(id);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal); // profile id -> patched text
        string TextOf(string owner) => texts.TryGetValue(owner, out string? t) ? t : File.ReadAllText(files[owner]);

        foreach (RuleEdit e in edits.Events)
        {
            EventRule rule = resolved.Events.FirstOrDefault(r => r.Name == e.Name)
                ?? throw new ArgumentException($"'{id}' has no event '{e.Name}'.");
            string owner = OwnerOf(path, p => p.Events.Any(r => r.Name == e.Name));
            string text = TextOf(owner);
            if (e.Weight is double w)
            {
                text = ProfileTextPatcher.Set(text, $"events[{e.Name}].weight", Weight(w, $"event '{e.Name}'"));
            }

            int n = rule.When.Count;
            foreach ((int index, double value) in e.Thresholds)
            {
                Condition c = index >= 0 && index < n ? rule.When[index] : index >= n && index - n < rule.WhenAny.Count ? rule.WhenAny[index - n]
                    : throw new ArgumentException($"'{e.Name}' has no condition {index}.");
                if (!double.IsFinite(value))
                {
                    throw new ArgumentException($"Threshold {index} of '{e.Name}' is not a number.");
                }

                string list = index < n ? $"when[{index}]" : $"whenAny[{index - n}]";
                string field = c.Rise is not null ? "rise" : c.Above is not null ? "above" : "below";
                text = ProfileTextPatcher.Set(text, $"events[{e.Name}].{list}.{field}", ProfileTextPatcher.Number(value, 4));
            }

            texts[owner] = text;
        }

        foreach (CompositeEdit c in edits.Composites)
        {
            if (!resolved.Composites.Any(r => r.Name == c.Name))
            {
                throw new ArgumentException($"'{id}' has no composite '{c.Name}'.");
            }

            string owner = OwnerOf(path, p => p.Composites.Any(r => r.Name == c.Name));
            texts[owner] = ProfileTextPatcher.Set(TextOf(owner), $"composites[{c.Name}].weight", Weight(c.Weight, $"composite '{c.Name}'"));
        }

        if (edits.Clipper.Count > 0 || edits.Components.Count > 0)
        {
            texts[id] = EditClipper(TextOf(id), edits);
        }

        // Check the whole folder as the recorder would load it, and that nothing else moved, before writing a byte.
        var profiles = catalog.All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach ((string owner, string text) in texts)
        {
            GameProfile patched = GameProfileLoader.Parse(text, files[owner]);
            if (patched.Id != owner || JsonSerializer.Serialize(Fixed(patched), Compare) != JsonSerializer.Serialize(Fixed(profiles[owner]), Compare))
            {
                throw new InvalidDataException($"{files[owner]}: the edit would change more than weights, thresholds and clipper settings; nothing was saved.");
            }

            profiles[owner] = patched;
        }

        var next = new ProfileCatalog(profiles.Values);
        IReadOnlyList<string> problems = ProfileFolder.Check(next);
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Profile catalog is invalid:\n  " + string.Join("\n  ", problems));
        }

        var written = new List<string>();
        foreach ((string owner, string text) in texts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string file = files[owner];
            if (text == File.ReadAllText(file))
            {
                continue;
            }

            string temp = file + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, file, overwrite: true);
            written.Add(file);
        }

        return new ProfileSaveResult(written, Build(next, files, id));
    }

    private static string EditClipper(string text, ProfileEdits edits)
    {
        foreach ((string field, double? value) in edits.Clipper)
        {
            if (!ProfileEdits.ClipperFields.TryGetValue(field, out bool ms))
            {
                throw new ArgumentException($"'{field}' is not a clipper setting (have: {string.Join(", ", ProfileEdits.ClipperFields.Keys)}).");
            }

            if (value is not double v)
            {
                text = RemoveIfPresent(text, $"clipper.{field}");
                continue;
            }

            if (!double.IsFinite(v) || v < 0 || (ms && (v != Math.Floor(v) || v > int.MaxValue)))
            {
                throw new ArgumentException($"clipper {field} must be {(ms ? "a whole number of milliseconds, 0 or more" : "a number, 0 or more")}.");
            }

            text = EnsureObject(text, "clipper");
            text = ProfileTextPatcher.Set(text, $"clipper.{field}", ms ? ((long)v).ToString(CultureInfo.InvariantCulture) : ProfileTextPatcher.Number(v, 3));
        }

        foreach ((string signal, double? weight) in edits.Components)
        {
            if (signal.Length == 0 || signal.Length > 128 || signal.Any(ch => !(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '.' or '_' or '-')))
            {
                throw new ArgumentException($"'{signal}' is not a signal name.");
            }

            int? at = ComponentIndex(text, signal);
            if (weight is not double w)
            {
                if (at is int i)
                {
                    text = ProfileTextPatcher.Remove(text, $"clipper.components[{i}]");
                }

                continue;
            }

            string number = Weight(w, $"component '{signal}'");
            if (at is int j)
            {
                text = ProfileTextPatcher.Set(text, $"clipper.components[{j}].weight", number);
            }
            else
            {
                text = EnsureObject(text, "clipper");
                if (!ProfileTextPatcher.Paths(text).Contains("clipper.components"))
                {
                    text = ProfileTextPatcher.Set(text, "clipper.components", "[]");
                }

                text = ProfileTextPatcher.Append(text, "clipper.components", $"{{ \"signal\": {JsonSerializer.Serialize(signal)}, \"weight\": {number} }}");
            }
        }

        // An override block left empty goes away, so the file is as it was before the first override.
        if (ProfileTextPatcher.Paths(text).Contains("clipper.components") && ProfileTextPatcher.Children(text, "clipper.components").Count == 0)
        {
            text = ProfileTextPatcher.Remove(text, "clipper.components");
        }

        if (ProfileTextPatcher.Paths(text).Contains("clipper") && ProfileTextPatcher.Children(text, "clipper").Count == 0)
        {
            text = ProfileTextPatcher.Remove(text, "clipper");
        }

        return text;
    }

    private static string EnsureObject(string text, string path) =>
        ProfileTextPatcher.Paths(text).Contains(path) ? text : ProfileTextPatcher.Set(text, path, "{}");

    private static string RemoveIfPresent(string text, string path) =>
        ProfileTextPatcher.Paths(text).Contains(path) ? ProfileTextPatcher.Remove(text, path) : text;

    // Index of the signal's item in the file's own clipper.components, read from the text (it may not validate yet mid-edit).
    private static int? ComponentIndex(string text, string signal)
    {
        using JsonDocument doc = JsonDocument.Parse(text, DocOptions);
        if (!doc.RootElement.TryGetProperty("clipper", out JsonElement clipper) || clipper.ValueKind != JsonValueKind.Object
            || !clipper.TryGetProperty("components", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int i = 0;
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("signal", out JsonElement s) && s.ValueKind == JsonValueKind.String && s.GetString() == signal)
            {
                return i;
            }

            i++;
        }

        return null;
    }

    private static string Weight(double w, string what) =>
        double.IsFinite(w) && w >= 0 && w <= 1000 ? ProfileTextPatcher.Number(w, 3) : throw new ArgumentException($"The weight of {what} must be between 0 and 1000.");

    // The nearest profile on the path (the edited one first) that defines the rule: a child that redefines a rule owns it.
    private static string OwnerOf(IReadOnlyList<GameProfile> path, Func<GameProfile, bool> defines)
    {
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (defines(path[i]))
            {
                return path[i].Id;
            }
        }

        throw new InvalidOperationException("No profile on the path defines the rule.");
    }

    // The profile with everything the editor may change blanked out, for checking that nothing else changed.
    private static GameProfile Fixed(GameProfile p)
    {
        static Condition Blank(Condition c) => c with { Above = c.Above is null ? null : 0, Below = c.Below is null ? null : 0, Rise = c.Rise is null ? null : 0 };
        return p with
        {
            Events = [.. p.Events.Select(e => e with { Weight = 0, When = [.. e.When.Select(Blank)], WhenAny = [.. e.WhenAny.Select(Blank)] })],
            Composites = [.. p.Composites.Select(c => c with { Weight = 0 })],
            Clipper = new ClipperSpec(),
        };
    }

    private static string Level(GameProfile p) => p.Level.ToString().ToLowerInvariant();

    private ProfileView Build(ProfileCatalog catalog, Dictionary<string, string> files, string id)
    {
        if (!catalog.Contains(id))
        {
            throw new ArgumentException($"No profile '{id}' in {Directory}.");
        }

        GameProfile own = catalog.Get(id);
        GameProfile r = catalog.Resolve(id);
        IReadOnlyList<GameProfile> path = catalog.PathTo(id);

        var events = r.Events.Select(e =>
        {
            int n = e.When.Count;
            var conditions = e.When.Concat(e.WhenAny).Select((c, i) => new ConditionView(
                i, i < n ? "when" : "whenAny", c.Signal, c.Rise is not null ? "rise" : c.Above is not null ? "above" : "below", c.Rise ?? c.Above ?? c.Below ?? 0)).ToArray();
            return new RuleView(e.Name, OwnerOf(path, p => p.Events.Any(x => x.Name == e.Name)), e.Weight, e.BaseConfidence, conditions);
        }).ToArray();

        var composites = r.Composites.Select(c => new CompositeView(
            c.Name, OwnerOf(path, p => p.Composites.Any(x => x.Name == c.Name)), c.Of, c.AtLeast, c.WithinMs, c.Weight)).ToArray();

        ClipperSpec spec = r.Clipper.Over(ClipperDefaults.Spec);
        string SourceOf(Func<ClipperSpec, bool> sets)
        {
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (sets(path[i].Clipper))
                {
                    return path[i].Id;
                }
            }

            return Defaults;
        }

        var numbers = new List<ClipperNumberView>
        {
            new("high", spec.High!.Value, SourceOf(s => s.High is not null)),
            new("low", spec.Low!.Value, SourceOf(s => s.Low is not null)),
            new("preMs", spec.PreMs!.Value, SourceOf(s => s.PreMs is not null)),
            new("postMs", spec.PostMs!.Value, SourceOf(s => s.PostMs is not null)),
            new("refractoryMs", spec.RefractoryMs!.Value, SourceOf(s => s.RefractoryMs is not null)),
            new("peakScore", spec.PeakScore!.Value, SourceOf(s => s.PeakScore is not null)),
        };
        var components = spec.Components.Select(c => new ComponentView(
            c.Signal, c.Weight, c.MinSpread, SourceOf(s => s.Components.Any(x => x.Signal == c.Signal)))).ToArray();

        string root = path[0].Id;
        string file = Path.GetRelativePath(Directory, files[id]).Replace('\\', '/');
        return new ProfileView(
            id, own.DisplayName, own.Parent, Level(own), file, [.. path.Select(p => p.Id)],
            events, composites, new ClipperView(numbers, components),
            [.. ProfileFolder.Signals(catalog, root).Order(StringComparer.Ordinal)]);
    }
}

/// <summary>A profile in the list: <see cref="Depth"/> 0 for a game.</summary>
public sealed record ProfileSummary(string Id, string DisplayName, string? Parent, string Level, int Depth);

/// <summary>
/// A profile with everything inherited applied. Every number carries its
/// source: the id of the profile whose file sets it, or
/// <see cref="ProfileEditor.Defaults"/> (the clipper's defaults).
/// </summary>
public sealed record ProfileView(
    string Id,
    string DisplayName,
    string? Parent,
    string Level,
    string File,
    IReadOnlyList<string> Path,
    IReadOnlyList<RuleView> Events,
    IReadOnlyList<CompositeView> Composites,
    ClipperView Clipper,
    IReadOnlyList<string> Signals);

public sealed record RuleView(string Name, string Source, float Weight, float BaseConfidence, IReadOnlyList<ConditionView> Conditions);

/// <summary>One condition of a rule; <see cref="Index"/> is what <see cref="RuleEdit.Thresholds"/> takes.</summary>
public sealed record ConditionView(int Index, string List, string Signal, string Kind, float Value);

public sealed record CompositeView(string Name, string Source, string Of, int AtLeast, int WithinMs, float Weight);

public sealed record ClipperView(IReadOnlyList<ClipperNumberView> Numbers, IReadOnlyList<ComponentView> Components);

public sealed record ClipperNumberView(string Field, double Value, string Source);

public sealed record ComponentView(string Signal, float Weight, float? MinSpread, string Source);
