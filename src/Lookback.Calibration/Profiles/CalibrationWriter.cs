using System.Text;
using Lookback.Calibration.Probe;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Profiles.Model;
using Lookback.Stream.SessionLog;

namespace Lookback.Calibration.Profiles;

/// <summary>
/// Writes confirmed calibration numbers into the profile files they belong
/// to, in place: each region's x, y, w, h in the file that defines the
/// region, each threshold in the file that defines the rule, and
/// <c>reference.calibrated</c> where regions moved. Nothing else in the files
/// changes. Every write is checked by <see cref="RetentionRules.ValidateDerived"/>
/// first, and the whole result must load as a valid catalog before any file is touched.
/// </summary>
public sealed class CalibrationWriter(string profilesDirectory)
{
    public string Directory { get; } = Path.GetFullPath(profilesDirectory ?? throw new ArgumentNullException(nameof(profilesDirectory)));

    /// <summary>Profile files under the directory, by profile id.</summary>
    public Dictionary<string, string> Files()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.json", SearchOption.AllDirectories))
        {
            files[GameProfileLoader.Parse(File.ReadAllText(path), path).Id] = path;
        }

        return files;
    }

    /// <summary>The catalog the directory holds (for running the session on the same profiles it will write).</summary>
    public ProfileCatalog Catalog() => new(Files().Values.Select(GameProfileLoader.Load));

    /// <summary>Writes <paramref name="edits"/>; returns the files changed.</summary>
    /// <exception cref="RetentionViolationException">The session may not write, or the user did not confirm.</exception>
    public IReadOnlyList<string> Save(SessionStarted session, CalibrationEdits edits, bool userConfirmed)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(edits);
        string? refused = RetentionRules.ValidateDerived(session, DerivedWriteKind.ConfirmedProfileParameters, userConfirmed);
        if (refused is not null)
        {
            throw new RetentionViolationException(refused);
        }

        if (edits.IsEmpty)
        {
            return [];
        }

        Dictionary<string, string> files = Files();
        var profiles = files.ToDictionary(kv => kv.Key, kv => GameProfileLoader.Load(kv.Value), StringComparer.Ordinal);
        var patches = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string> PatchFor(string id) =>
            patches.TryGetValue(id, out var p) ? p : patches[id] = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string name, (double x, double y, double w, double h)) in edits.Regions)
        {
            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1.0001 || y + h > 1.0001)
            {
                throw new ArgumentException($"Region '{name}' must lie inside the frame.", nameof(edits));
            }

            GameProfile owner = profiles.Values.FirstOrDefault(p => p.Regions.Any(r => r.Name == name))
                ?? throw new KeyNotFoundException($"No profile file defines region '{name}'.");
            Dictionary<string, string> patch = PatchFor(owner.Id);
            patch[$"regions[{name}].x"] = ProfileTextPatcher.Number(x, 3);
            patch[$"regions[{name}].y"] = ProfileTextPatcher.Number(y, 3);
            patch[$"regions[{name}].w"] = ProfileTextPatcher.Number(w, 3);
            patch[$"regions[{name}].h"] = ProfileTextPatcher.Number(h, 3);
            patch["reference.calibrated"] = "true";
        }

        foreach ((ConditionKey key, float value) in edits.Thresholds)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException($"Threshold for {key.Event} is not a number.", nameof(edits));
            }

            GameProfile owner = profiles.GetValueOrDefault(key.Owner) ?? throw new KeyNotFoundException($"No profile file for '{key.Owner}'.");
            EventRule rule = owner.Events.FirstOrDefault(e => e.Name == key.Event)
                ?? throw new KeyNotFoundException($"'{key.Owner}' defines no event '{key.Event}'.");
            int n = rule.When.Count;
            Condition c = key.Index < n ? rule.When[key.Index] : key.Index - n < rule.WhenAny.Count ? rule.WhenAny[key.Index - n]
                : throw new KeyNotFoundException($"'{key.Event}' has no condition {key.Index}.");
            string list = key.Index < n ? $"when[{key.Index}]" : $"whenAny[{key.Index - n}]";
            string field = c.Rise is not null ? "rise" : c.Above is not null ? "above" : "below";
            PatchFor(owner.Id)[$"events[{key.Event}].{list}.{field}"] = ProfileTextPatcher.Number(value, 4);
        }

        // Patch everything in memory and check the whole tree loads before writing a byte.
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string id, Dictionary<string, string> patch) in patches)
        {
            string path = files[id];
            string text = File.ReadAllText(path);
            if (!ProfileTextPatcher.Index(text).ContainsKey("reference.calibrated"))
            {
                patch.Remove("reference.calibrated"); // a file without the flag keeps its shape; the region numbers still change
            }

            string patched = ProfileTextPatcher.Apply(text, patch);
            profiles[id] = GameProfileLoader.Parse(patched, path);
            written[path] = patched;
        }

        _ = new ProfileCatalog(profiles.Values);
        foreach ((string path, string text) in written)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }

        return [.. written.Keys.Order(StringComparer.Ordinal)];
    }
}
