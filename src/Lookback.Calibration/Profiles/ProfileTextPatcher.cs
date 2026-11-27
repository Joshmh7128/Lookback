using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lookback.Calibration.Profiles;

/// <summary>
/// Changes number and boolean values in a profile's JSON text in place, so
/// comments, layout and everything not being calibrated stay exactly as they
/// were. Values are addressed by path: <c>regions[kill_feed].x</c> (array
/// items by their <c>name</c>), <c>events[elimination].when[1].rise</c>,
/// <c>reference.calibrated</c>.
/// </summary>
internal static class ProfileTextPatcher
{
    private static readonly JsonReaderOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Every scalar in <paramref name="json"/> by path, with its byte range.</summary>
    public static Dictionary<string, (int Start, int Length, JsonTokenType Type)> Index(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        var found = new Dictionary<string, (int, int, JsonTokenType)>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(utf8, Options);
        Walk(ref reader, utf8, "", found);
        return found;
    }

    /// <summary>
    /// Returns <paramref name="json"/> with each path's value replaced. Every
    /// path must exist and hold a number or boolean; nothing else can be changed.
    /// </summary>
    public static string Apply(string json, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Dictionary<string, (int Start, int Length, JsonTokenType Type)> index = Index(json);
        var edits = new List<(int Start, int Length, byte[] Text)>();
        foreach ((string path, string text) in values)
        {
            if (!index.TryGetValue(path, out (int Start, int Length, JsonTokenType Type) at))
            {
                throw new KeyNotFoundException($"The profile has no value at '{path}'.");
            }

            bool number = at.Type == JsonTokenType.Number && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            bool boolean = at.Type is JsonTokenType.True or JsonTokenType.False && text is "true" or "false";
            if (!number && !boolean)
            {
                throw new InvalidOperationException($"'{path}' can only be changed to another {at.Type}, not '{text}'.");
            }

            edits.Add((at.Start, at.Length, Encoding.UTF8.GetBytes(text)));
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        var output = new List<byte>(utf8.Length + 64);
        int pos = 0;
        foreach ((int start, int length, byte[] text) in edits.OrderBy(e => e.Start))
        {
            output.AddRange(utf8.AsSpan(pos, start - pos).ToArray());
            output.AddRange(text);
            pos = start + length;
        }

        output.AddRange(utf8.AsSpan(pos).ToArray());
        return Encoding.UTF8.GetString([.. output]);
    }

    /// <summary>Invariant text for a value, with at least one decimal so it reads as a number of the same kind.</summary>
    public static string Number(double value, int decimals) =>
        Math.Round(value, decimals).ToString("0.0" + new string('#', Math.Max(0, decimals - 1)), CultureInfo.InvariantCulture);

    private static void Walk(ref Utf8JsonReader reader, byte[] utf8, string path, Dictionary<string, (int, int, JsonTokenType)> found)
    {
        if (!reader.Read())
        {
            return;
        }

        Value(ref reader, utf8, path, found);
    }

    // The reader is on the first token of a value.
    private static void Value(ref Utf8JsonReader reader, byte[] utf8, string path, Dictionary<string, (int, int, JsonTokenType)> found)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = reader.GetString()!;
                    reader.Read();
                    Value(ref reader, utf8, path.Length == 0 ? name : $"{path}.{name}", found);
                }

                break;

            case JsonTokenType.StartArray:
                int i = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    // Objects with a name are addressed by it; a copy of the reader finds it first.
                    string key = i.ToString(CultureInfo.InvariantCulture);
                    if (reader.TokenType == JsonTokenType.StartObject && NameOf(reader) is { } n)
                    {
                        key = n;
                    }

                    Value(ref reader, utf8, $"{path}[{key}]", found);
                    i++;
                }

                break;

            case JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False:
                found[path] = ((int)reader.TokenStartIndex, (int)(reader.BytesConsumed - reader.TokenStartIndex), reader.TokenType);
                break;

            default:
                break;
        }
    }

    private static string? NameOf(Utf8JsonReader copy)
    {
        int depth = copy.CurrentDepth;
        while (copy.Read() && copy.CurrentDepth > depth)
        {
            if (copy.CurrentDepth == depth + 1 && copy.TokenType == JsonTokenType.PropertyName && copy.ValueTextEquals("name"))
            {
                copy.Read();
                return copy.TokenType == JsonTokenType.String ? copy.GetString() : null;
            }

            if (copy.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                copy.Skip();
            }
        }

        return null;
    }
}
