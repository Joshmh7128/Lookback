using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lookback.Calibration.Profiles;

/// <summary>
/// Changes a profile's JSON text in place, so comments, layout and everything
/// not being edited stay exactly as they were and a profile's git diff shows
/// only what changed. Values are addressed by path: <c>regions[kill_feed].x</c>
/// (array items by their <c>name</c>, otherwise by index),
/// <c>events[elimination].when[1].rise</c>, <c>reference.calibrated</c>,
/// <c>clipper.components[0].weight</c>.
/// <see cref="Apply"/> only replaces existing numbers and booleans (what
/// calibration writes); <see cref="Set"/>, <see cref="Append"/> and
/// <see cref="Remove"/> also add and remove properties and array items, for
/// the profile editor's overrides.
/// </summary>
internal static class ProfileTextPatcher
{
    private static readonly JsonReaderOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Every scalar in <paramref name="json"/> by path, with its byte range.</summary>
    public static Dictionary<string, (int Start, int Length, JsonTokenType Type)> Index(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Map(Encoding.UTF8.GetBytes(json)).Values
            .Where(n => n.Kind is JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False)
            .ToDictionary(n => n.Path, n => (n.ValueStart, n.ValueEnd - n.ValueStart, n.Kind), StringComparer.Ordinal);
    }

    /// <summary>Paths of every value (scalars, objects and arrays) in <paramref name="json"/>.</summary>
    public static IReadOnlyCollection<string> Paths(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Map(Encoding.UTF8.GetBytes(json)).Keys;
    }

    /// <summary>
    /// Returns <paramref name="json"/> with each path's value replaced. Every
    /// path must exist and hold a number or boolean; nothing else can be changed.
    /// </summary>
    public static string Apply(string json, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(values);
        Dictionary<string, (int Start, int Length, JsonTokenType Type)> index = Index(json);
        var edits = new List<(int Start, int Length, byte[] Text)>();
        foreach ((string path, string text) in values)
        {
            if (!index.TryGetValue(path, out (int Start, int Length, JsonTokenType Type) at))
            {
                throw new KeyNotFoundException($"The profile has no value at '{path}'.");
            }

            CheckScalar(path, at.Type, text);
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

    /// <summary>
    /// Sets the property at <paramref name="path"/> (<c>clipper.high</c>) to
    /// <paramref name="valueText"/>, raw JSON. An existing number or boolean is
    /// replaced in place and must stay a number or boolean; a missing property
    /// is added at the end of its object, which must exist, on a line of its
    /// own when the object spans lines (with the object's indentation).
    /// </summary>
    public static string Set(string json, string path, string valueText)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(valueText);
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        Dictionary<string, Node> map = Map(utf8);
        if (map.TryGetValue(path, out Node? existing))
        {
            CheckScalar(path, existing.Kind, valueText);
            return Splice(utf8, existing.ValueStart, existing.ValueEnd - existing.ValueStart, valueText);
        }

        (string parentPath, string name) = SplitProperty(path);
        if (!map.TryGetValue(parentPath, out Node? parent) || parent.Kind != JsonTokenType.StartObject)
        {
            throw new KeyNotFoundException($"The profile has no object at '{(parentPath.Length == 0 ? "(root)" : parentPath)}' to add '{name}' to.");
        }

        return InsertChild(utf8, map, parent, $"\"{name}\": {valueText}");
    }

    /// <summary>Adds <paramref name="itemText"/> (raw JSON) as the last item of the array at <paramref name="arrayPath"/>.</summary>
    public static string Append(string json, string arrayPath, string itemText)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrEmpty(itemText);
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        Dictionary<string, Node> map = Map(utf8);
        if (!map.TryGetValue(arrayPath, out Node? array) || array.Kind != JsonTokenType.StartArray)
        {
            throw new KeyNotFoundException($"The profile has no array at '{arrayPath}'.");
        }

        return InsertChild(utf8, map, array, itemText);
    }

    /// <summary>
    /// Removes the property or array item at <paramref name="path"/>, with its
    /// line when it has one to itself (a comment at the end of that line goes too),
    /// and the comma that separated it.
    /// </summary>
    public static string Remove(string json, string path)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        Dictionary<string, Node> map = Map(utf8);
        if (!map.TryGetValue(path, out Node? node) || node.Parent is null)
        {
            throw new KeyNotFoundException($"The profile has no value at '{path}'.");
        }

        Node parent = map[node.Parent];
        int i = parent.Children.IndexOf(path);
        Node? prev = i > 0 ? map[parent.Children[i - 1]] : null;
        Node? next = i + 1 < parent.Children.Count ? map[parent.Children[i + 1]] : null;

        if (OwnLine(utf8, node) is (int lineStart, int lineEnd))
        {
            var cuts = new List<(int Start, int Length)> { (lineStart, lineEnd - lineStart) };
            if (next is null && prev is not null && CommaAfter(utf8, prev.ValueEnd) is int comma)
            {
                cuts.Add((comma, 1)); // the previous item becomes the last one
            }

            return Cut(utf8, cuts);
        }

        // Inline: take the separator on one side with it.
        if (next is not null)
        {
            return Cut(utf8, [(node.EntryStart, next.EntryStart - node.EntryStart)]);
        }

        if (prev is not null)
        {
            return Cut(utf8, [(prev.ValueEnd, node.ValueEnd - prev.ValueEnd)]);
        }

        int end = CommaAfter(utf8, node.ValueEnd) is int c ? c + 1 : node.ValueEnd;
        return Cut(utf8, [(node.EntryStart, end - node.EntryStart)]);
    }

    /// <summary>Children's paths of the object or array at <paramref name="path"/>, in order; empty when there is none.</summary>
    public static IReadOnlyList<string> Children(string json, string path)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Map(Encoding.UTF8.GetBytes(json)).TryGetValue(path, out Node? n) ? n.Children : [];
    }

    /// <summary>Invariant text for a value, with at least one decimal so it reads as a number of the same kind.</summary>
    public static string Number(double value, int decimals) =>
        Math.Round(value, decimals).ToString("0.0" + new string('#', Math.Max(0, decimals - 1)), CultureInfo.InvariantCulture);

    private static void CheckScalar(string path, JsonTokenType type, string text)
    {
        bool number = type == JsonTokenType.Number && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        bool boolean = type is JsonTokenType.True or JsonTokenType.False && text is "true" or "false";
        if (!number && !boolean)
        {
            throw new InvalidOperationException($"'{path}' can only be changed to another {type}, not '{text}'.");
        }
    }

    private static (string Parent, string Name) SplitProperty(string path)
    {
        int dot = path.LastIndexOf('.');
        int bracket = path.LastIndexOf(']');
        if (path.EndsWith(']') || bracket > dot)
        {
            throw new ArgumentException($"'{path}' does not name a property.", nameof(path));
        }

        return dot < 0 ? ("", path) : (path[..dot], path[(dot + 1)..]);
    }

    private static string InsertChild(byte[] utf8, Dictionary<string, Node> map, Node container, string text)
    {
        string nl = utf8.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
        if (container.Children.Count == 0)
        {
            // An empty {} or []: on lines of its own when its own entry starts a line.
            int open = container.ValueStart;
            int close = container.ValueEnd - 1;
            if (StartsLine(utf8, container.EntryStart))
            {
                string indent = IndentAt(utf8, container.EntryStart);
                string inner = indent + IndentUnit(utf8, map);
                return Splice(utf8, open + 1, close - (open + 1), $"{nl}{inner}{text}{nl}{indent}");
            }

            return Splice(utf8, open + 1, close - (open + 1), $" {text} ");
        }

        Node last = map[container.Children[^1]];
        int? trailing = CommaAfter(utf8, last.ValueEnd);
        Node first = map[container.Children[0]];
        if (StartsLine(utf8, first.EntryStart) && LineEndAfter(utf8, trailing is int t ? t + 1 : last.ValueEnd) is int lineEnd)
        {
            // One per line: a comma after the last value (unless it has one), then a new line with the same indentation.
            string indent = IndentAt(utf8, last.EntryStart);
            var edits = new List<(int, int, string)>();
            if (trailing is null)
            {
                edits.Add((last.ValueEnd, 0, ",")); // first: the line may end right after the value (edits at one offset keep this order)
            }

            edits.Add((lineEnd, 0, $"{nl}{indent}{text}"));
            return SpliceMany(utf8, edits);
        }

        return Splice(utf8, last.ValueEnd, 0, $", {text}");
    }

    // [start of line, after its newline) when the node has its line(s) to itself: only
    // whitespace before it, and only a comma, whitespace or a // comment after it.
    private static (int Start, int End)? OwnLine(byte[] b, Node node)
    {
        if (!StartsLine(b, node.EntryStart))
        {
            return null;
        }

        int pos = node.ValueEnd;
        if (CommaAfter(b, pos) is int comma)
        {
            pos = comma + 1;
        }

        int? end = LineEndAfter(b, pos);
        if (end is not int e)
        {
            return null;
        }

        int start = node.EntryStart;
        while (start > 0 && b[start - 1] is (byte)' ' or (byte)'\t')
        {
            start--;
        }

        if (e < b.Length && b[e] == '\r')
        {
            e++;
        }

        if (e < b.Length && b[e] == '\n')
        {
            e++;
        }

        return (start, e);
    }

    private static bool StartsLine(byte[] b, int pos)
    {
        for (int i = pos - 1; i >= 0; i--)
        {
            if (b[i] == '\n')
            {
                return true;
            }

            if (b[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
    }

    // Where the line ends (the index of its \r or \n) when only whitespace or a // comment follows pos on it.
    private static int? LineEndAfter(byte[] b, int pos)
    {
        int i = pos;
        while (i < b.Length && b[i] is (byte)' ' or (byte)'\t')
        {
            i++;
        }

        if (i + 1 < b.Length && b[i] == '/' && b[i + 1] == '/')
        {
            while (i < b.Length && b[i] is not ((byte)'\n' or (byte)'\r'))
            {
                i++;
            }
        }

        return i >= b.Length || b[i] is (byte)'\n' or (byte)'\r' ? i : null;
    }

    private static int? CommaAfter(byte[] b, int pos)
    {
        int i = pos;
        while (i < b.Length && b[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i < b.Length && b[i] == ',' ? i : null;
    }

    private static string IndentAt(byte[] b, int pos)
    {
        int start = pos;
        while (start > 0 && b[start - 1] is (byte)' ' or (byte)'\t')
        {
            start--;
        }

        return Encoding.UTF8.GetString(b, start, pos - start);
    }

    // The file's indentation step: how far the root's first property is indented.
    private static string IndentUnit(byte[] b, Dictionary<string, Node> map)
    {
        if (map.TryGetValue("", out Node? root) && root.Children.Count > 0 && map[root.Children[0]] is { } first && StartsLine(b, first.EntryStart))
        {
            string indent = IndentAt(b, first.EntryStart);
            if (indent.Length > 0)
            {
                return indent;
            }
        }

        return "  ";
    }

    private static string Splice(byte[] b, int start, int length, string text) => SpliceMany(b, [(start, length, text)]);

    private static string Cut(byte[] b, IEnumerable<(int Start, int Length)> cuts) => SpliceMany(b, cuts.Select(c => (c.Start, c.Length, "")));

    private static string SpliceMany(byte[] b, IEnumerable<(int Start, int Length, string Text)> edits)
    {
        var output = new List<byte>(b.Length + 128);
        int pos = 0;
        foreach ((int start, int length, string text) in edits.OrderBy(e => e.Start))
        {
            output.AddRange(b.AsSpan(pos, start - pos).ToArray());
            output.AddRange(Encoding.UTF8.GetBytes(text));
            pos = start + length;
        }

        output.AddRange(b.AsSpan(pos).ToArray());
        return Encoding.UTF8.GetString([.. output]);
    }

    /// <summary>
    /// One value in the text. <see cref="EntryStart"/> is where its entry
    /// begins (the property name's quote, or the value itself for an array item);
    /// <see cref="ValueEnd"/> is just past the value (past the closing bracket for a container).
    /// </summary>
    private sealed record Node(string Path, string? Parent, JsonTokenType Kind, int EntryStart, int ValueStart)
    {
        public int ValueEnd { get; set; }

        public List<string> Children { get; } = [];
    }

    private static Dictionary<string, Node> Map(byte[] utf8)
    {
        var found = new Dictionary<string, Node>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(utf8, Options);
        if (reader.Read())
        {
            Value(ref reader, "", null, (int)reader.TokenStartIndex, found);
        }

        return found;
    }

    // The reader is on the first token of a value.
    private static void Value(ref Utf8JsonReader reader, string path, string? parent, int entryStart, Dictionary<string, Node> found)
    {
        var node = new Node(path, parent, reader.TokenType, entryStart, (int)reader.TokenStartIndex);
        found[path] = node; // a repeated key or name keeps the last, as the loader does
        if (parent is not null)
        {
            found[parent].Children.Add(path);
        }

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = reader.GetString()!;
                    int nameStart = (int)reader.TokenStartIndex;
                    reader.Read();
                    Value(ref reader, path.Length == 0 ? name : $"{path}.{name}", path, nameStart, found);
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

                    Value(ref reader, $"{path}[{key}]", path, (int)reader.TokenStartIndex, found);
                    i++;
                }

                break;

            default:
                break;
        }

        node.ValueEnd = (int)reader.BytesConsumed;
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
