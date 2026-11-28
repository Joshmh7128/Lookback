using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Profiles.Model;
using Lookback.Signals.Video;

namespace Lookback.Profiles.Detection;

/// <summary>
/// Tells play from a profile's menu states (<see cref="MenuSpec"/>), frame by
/// frame. Each frame's colour grid is compared with every state's signature;
/// the closest state within its <see cref="MenuState.MaxDistance"/> is this
/// frame's <see cref="Raw"/> match, or <see cref="Play"/> when none is close
/// enough. The <see cref="Held"/> state changes only once a new raw match has
/// lasted <see cref="MenuSpec.EnterMs"/> (into a menu, or from one menu to
/// another) or <see cref="MenuSpec.ExitMs"/> (back to play), so a single
/// frame that happens to look like a menu changes nothing. Allocation-free per frame.
/// </summary>
public sealed class MenuStateDetector
{
    /// <summary>The state index meaning "no menu state matches".</summary>
    public const int Play = -1;

    private readonly string[] _names;
    private readonly float[][] _references;
    private readonly bool[][] _compared;
    private readonly float[] _max;
    private readonly float[] _distances;
    private readonly long _enter;
    private readonly long _exit;
    private int _candidate = Play;
    private long _candidateSince;

    public MenuStateDetector(MenuSpec spec, long frequency)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        int n = spec.States.Count;
        _names = new string[n];
        _references = new float[n][];
        _compared = new bool[n][];
        _max = new float[n];
        _distances = new float[n];
        for (int i = 0; i < n; i++)
        {
            MenuState s = spec.States[i];
            _names[i] = s.Name;
            _references[i] = new float[ColorGridSignal.Length];
            _compared[i] = new bool[ColorGridSignal.Cells];
            if (!MenuSignature.TryParse(s.Colors, _references[i], _compared[i], out string? problem))
            {
                throw new ArgumentException($"menu state '{s.Name}': {problem}", nameof(spec));
            }

            _max[i] = s.MaxDistance;
        }

        _enter = QpcTime.Rescale(spec.EnterMs ?? MenuSpec.DefaultEnterMs, 1000, frequency);
        _exit = QpcTime.Rescale(spec.ExitMs ?? MenuSpec.DefaultExitMs, 1000, frequency);
        Array.Fill(_distances, float.PositiveInfinity);
    }

    /// <summary>True when the profile names no menu state; such a detector always reports play.</summary>
    public bool IsEmpty => _names.Length == 0;

    public IReadOnlyList<string> StateNames => _names;

    /// <summary>The closest matching state on the last frame, or <see cref="Play"/>.</summary>
    public int Raw { get; private set; } = Play;

    /// <summary>The state after hysteresis, or <see cref="Play"/>.</summary>
    public int Held { get; private set; } = Play;

    public string HeldName => Held == Play ? "play" : _names[Held];

    /// <summary>When the raw match first moved to the current <see cref="Held"/> state: where the screen actually changed.</summary>
    public long HeldFrom { get; private set; } = long.MinValue;

    /// <summary>When <see cref="Held"/> last changed (later than <see cref="HeldFrom"/> by the hysteresis).</summary>
    public long HeldAt { get; private set; } = long.MinValue;

    /// <summary>Each state's distance on the last frame (infinite before any frame).</summary>
    public ReadOnlySpan<float> Distances => _distances;

    /// <summary>A state's <see cref="MenuState.MaxDistance"/>.</summary>
    public float MaxDistance(int state) => _max[state];

    /// <summary>
    /// One frame's colour grid (as <see cref="ColorGridSignal.Names"/>) at time
    /// <paramref name="t"/>. Returns true when <see cref="Held"/> changed.
    /// </summary>
    public bool Step(long t, ReadOnlySpan<float> grid)
    {
        if (grid.Length < ColorGridSignal.Length)
        {
            throw new ArgumentException("Need the whole colour grid.", nameof(grid));
        }

        int raw = Play;
        float best = float.PositiveInfinity;
        for (int i = 0; i < _names.Length; i++)
        {
            float d = MenuSignature.Distance(grid, _references[i], _compared[i]);
            _distances[i] = d;
            // Closest relative to each state's own tolerance, so a tight state is not shadowed by a loose one.
            float score = d / _max[i];
            if (d <= _max[i] && score < best)
            {
                best = score;
                raw = i;
            }
        }

        Raw = raw;
        if (raw == Held)
        {
            _candidate = Held;
            return false;
        }

        if (raw != _candidate)
        {
            _candidate = raw;
            _candidateSince = t;
        }

        if (t - _candidateSince < (raw == Play ? _exit : _enter))
        {
            return false;
        }

        Held = raw;
        HeldFrom = _candidateSince;
        HeldAt = t;
        return true;
    }

    /// <summary>Back to play with no history (a new session, or a change of profile).</summary>
    public void Reset()
    {
        Raw = Play;
        Held = Play;
        _candidate = Play;
        HeldFrom = long.MinValue;
        HeldAt = long.MinValue;
        Array.Fill(_distances, float.PositiveInfinity);
    }
}

/// <summary>
/// Finds the colour grid in signal frames: remembers where its values sit in
/// each video schema, then copies them out of a frame. Allocation-free per frame.
/// </summary>
public sealed class ColorGridReader
{
    private readonly Dictionary<uint, int[]> _positions = [];

    /// <summary>Learns a schema; returns true when it is a video schema carrying the whole grid.</summary>
    public bool OnSchema(SignalSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Family != SignalFamily.Video)
        {
            return false;
        }

        IReadOnlyList<string> names = ColorGridSignal.AllNames;
        int[] positions = new int[names.Count];
        int first = schema.Names.IndexOf(names[0]);
        for (int k = 0; k < names.Count; k++)
        {
            // The bank lays the grid out contiguously; fall back to a search if a schema does not.
            int guess = first + k;
            positions[k] = first >= 0 && guess < schema.Names.Count && schema.Names[guess] == names[k] ? guess : schema.Names.IndexOf(names[k]);
            if (positions[k] < 0)
            {
                _positions.Remove(schema.SchemaId);
                return false;
            }
        }

        _positions[schema.SchemaId] = positions;
        return true;
    }

    /// <summary>Copies the grid out of <paramref name="frame"/>; false when its schema has no grid or the frame is short.</summary>
    public bool TryRead(SignalFrame frame, Span<float> grid)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_positions.TryGetValue(frame.SchemaId, out int[]? positions) || grid.Length < positions.Length)
        {
            return false;
        }

        int count = frame.Values.Count;
        for (int k = 0; k < positions.Length; k++)
        {
            int p = positions[k];
            if (p >= count)
            {
                return false;
            }

            grid[k] = frame.Values[p];
        }

        return true;
    }
}
