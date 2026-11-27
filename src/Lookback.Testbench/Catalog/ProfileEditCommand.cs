using System.Globalization;
using System.Text.Json;
using Lookback.Calibration.Profiles;

namespace Lookback.Testbench.Catalog;

/// <summary>
/// <c>profile-view</c> and <c>profile-save</c>: the desktop app's profile
/// editor runs these against the checkout's <c>profiles/</c> folder. Both
/// print one JSON object on stdout: <c>{"ok":true,…}</c>, or
/// <c>{"ok":false,"message":…}</c> with the loader's message when the folder
/// or the edit is invalid (then nothing was written).
/// </summary>
public static class ProfileEditCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 };

    /// <summary>The profile list, and the profile <paramref name="id"/> resolved when given.</summary>
    public static (int Exit, string Json) View(string profilesDirectory, string? id)
    {
        return Run(() =>
        {
            var editor = new ProfileEditor(profilesDirectory);
            IReadOnlyList<ProfileSummary> list = editor.List();
            ProfileView? view = id is null ? null : editor.View(id);
            return JsonSerializer.Serialize(new { ok = true, directory = editor.Directory, profiles = list, view }, Json);
        });
    }

    /// <summary>Applies the edits in <paramref name="editsJson"/> (see <see cref="ParseEdits"/>) to <paramref name="id"/>.</summary>
    public static (int Exit, string Json) Save(string profilesDirectory, string id, string editsJson)
    {
        return Run(() =>
        {
            var editor = new ProfileEditor(profilesDirectory);
            ProfileSaveResult r = editor.Save(id, ParseEdits(editsJson));
            string[] files = [.. r.Files.Select(f => Path.GetRelativePath(editor.Directory, f).Replace('\\', '/'))];
            return JsonSerializer.Serialize(new { ok = true, directory = editor.Directory, files, view = r.View }, Json);
        });
    }

    /// <summary>
    /// The edit format:
    /// <code>
    /// { "events": [ { "name": "elimination", "weight": 1.5, "thresholds": { "0": 0.06 } } ],
    ///   "composites": [ { "name": "multikill", "weight": 3 } ],
    ///   "clipper": { "high": 1.8, "low": null },             // null: inherit again
    ///   "components": { "audio.rms_db": 0.5, "scene.cut": null } }
    /// </code>
    /// Anything else is refused.
    /// </summary>
    public static ProfileEdits ParseEdits(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument doc = ParseDocument(json);
        JsonElement root = Object(doc.RootElement, "edits");
        Only(root, "edits", "events", "composites", "clipper", "components");

        var events = new List<RuleEdit>();
        foreach (JsonElement e in Array(root, "events"))
        {
            Only(Object(e, "event edit"), "event edit", "name", "weight", "thresholds");
            var thresholds = new Dictionary<int, double>();
            if (e.TryGetProperty("thresholds", out JsonElement t))
            {
                foreach (JsonProperty p in Object(t, "thresholds").EnumerateObject())
                {
                    if (!int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    {
                        throw new ArgumentException($"thresholds: '{p.Name}' is not a condition index.");
                    }

                    thresholds[index] = Number(p.Value, $"threshold {p.Name}");
                }
            }

            events.Add(new RuleEdit
            {
                Name = Name(e),
                Weight = e.TryGetProperty("weight", out JsonElement w) ? Number(w, "weight") : null,
                Thresholds = thresholds,
            });
        }

        var composites = new List<CompositeEdit>();
        foreach (JsonElement c in Array(root, "composites"))
        {
            Only(Object(c, "composite edit"), "composite edit", "name", "weight");
            composites.Add(new CompositeEdit
            {
                Name = Name(c),
                Weight = c.TryGetProperty("weight", out JsonElement w) ? Number(w, "weight") : throw new ArgumentException("A composite edit needs a weight."),
            });
        }

        return new ProfileEdits
        {
            Events = events,
            Composites = composites,
            Clipper = Nullable(root, "clipper"),
            Components = Nullable(root, "components"),
        };
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The edits are not JSON: {ex.Message}", ex);
        }
    }

    private static (int, string) Run(Func<string> body)
    {
        try
        {
            return (0, body());
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (1, JsonSerializer.Serialize(new { ok = false, message = ex.Message }, Json));
        }
    }

    private static Dictionary<string, double?> Nullable(JsonElement root, string name)
    {
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        if (root.TryGetProperty(name, out JsonElement o))
        {
            foreach (JsonProperty p in Object(o, name).EnumerateObject())
            {
                values[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : Number(p.Value, $"{name}.{p.Name}");
            }
        }

        return values;
    }

    private static JsonElement Object(JsonElement e, string what) =>
        e.ValueKind == JsonValueKind.Object ? e : throw new ArgumentException($"{what} must be an object.");

    private static JsonElement[] Array(JsonElement root, string name) =>
        !root.TryGetProperty(name, out JsonElement a) ? [] : a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray()] : throw new ArgumentException($"{name} must be a list.");

    private static void Only(JsonElement o, string what, params string[] allowed)
    {
        foreach (JsonProperty p in o.EnumerateObject().Where(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
        {
            throw new ArgumentException($"{what}: unknown field '{p.Name}'.");
        }
    }

    private static string Name(JsonElement e) =>
        e.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s ? s : throw new ArgumentException("Every rule edit needs a name.");

    private static double Number(JsonElement e, string what) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double v) && double.IsFinite(v) ? v : throw new ArgumentException($"{what} must be a number.");
}
