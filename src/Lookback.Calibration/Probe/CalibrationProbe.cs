using Lookback.Core.Time;
using Lookback.Media.Frames;
using Lookback.Profiles.Model;
using Lookback.Signals.Video;

namespace Lookback.Calibration.Probe;

/// <summary>A proposed rectangle for one region, with why it was proposed.</summary>
public sealed record RegionProposal(string Name, double X, double Y, double W, double H, string Source, double Strength, string Evidence);

/// <summary>
/// Watches the analysis frames of a calibration session and remembers, in
/// memory only, what it needs to propose HUD regions: a coarse grid of
/// per-cell activity for the last few seconds, slow per-cell baselines, and a
/// per-pixel static mask. When the user marks a moment ("that was an
/// elimination") it takes the grid's response around the mark and adds it to
/// that event's map, so the cells that light up at every marked moment stand
/// out. Nothing here is a new kind of measurement: it is the same luma change
/// the region signals use, measured everywhere so a region can be found.
/// Allocation-free per frame; everything is dropped with the session.
/// </summary>
public sealed class CalibrationProbe : IVideoSignal
{
    public const int Cell = 8;
    public const float MinResponse = 2f; // z-score a cell must reach to count
    private const int RingSeconds = 4;
    private const float BaselineAlpha = 0.01f;
    private const float StaticAlpha = 1f / 150; // about 5 s at 30 fps
    private const float StaticStdDev = 6f;
    private const byte Letterbox = 16;

    private readonly int _w, _h, _cols, _rows, _cells, _ringFrames;
    private readonly long _frequency;
    private readonly float[] _ring;     // [frame][cell] activity
    private readonly long[] _ringQpc;
    private readonly float[] _mean, _meanSq;
    private readonly float[] _pxMean, _pxVar;
    private readonly byte[] _previous;
    private readonly Lock _gate = new();
    private readonly List<(string Event, long Qpc)> _pendingMarks = [];
    private readonly Dictionary<string, (float[] Sum, int Count)> _maps = new(StringComparer.Ordinal);
    private int _head;
    private long _frames;
    private long _lastQpc;

    public CalibrationProbe(AnalysisFormat format, long frequency)
    {
        ArgumentNullException.ThrowIfNull(format);
        _w = format.Width;
        _h = format.Height;
        _cols = _w / Cell;
        _rows = _h / Cell;
        _cells = _cols * _rows;
        _frequency = frequency;
        _ringFrames = format.FramesPerSecond * RingSeconds;
        _ring = new float[_ringFrames * _cells];
        _ringQpc = new long[_ringFrames];
        _mean = new float[_cells];
        _meanSq = new float[_cells];
        _pxMean = new float[_w * _h];
        _pxVar = new float[_w * _h];
        _previous = new byte[_w * _h];
    }

    /// <summary>Marks are matched to frames from this long before the mark (people react late)…</summary>
    public static TimeSpan MarkLead { get; } = TimeSpan.FromMilliseconds(2500);

    /// <summary>…to this long after it.</summary>
    public static TimeSpan MarkLag { get; } = TimeSpan.FromMilliseconds(300);

    public IReadOnlyList<string> Names { get; } = [];

    public int Columns => _cols;

    public int Rows => _rows;

    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>QPC of the newest frame seen.</summary>
    public long LastQpc => Interlocked.Read(ref _lastQpc);

    public void Process(VideoFrame frame, LumaPlane luma, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(luma);
        lock (_gate)
        {
            ReadOnlySpan<byte> y = luma.Values;
            bool first = _frames == 0;
            Span<float> slot = _ring.AsSpan(_head * _cells, _cells);
            slot.Clear();
            for (int row = 0; row < _rows * Cell; row++)
            {
                int cellRow = (row / Cell) * _cols;
                int o = row * _w;
                for (int col = 0; col < _cols * Cell; col++)
                {
                    int i = o + col;
                    byte v = y[i];
                    slot[cellRow + (col / Cell)] += Math.Abs(v - _previous[i]);
                    _previous[i] = v;

                    float d = v - _pxMean[i];
                    _pxMean[i] += first ? d : StaticAlpha * d;
                    _pxVar[i] = first ? 0 : (1 - StaticAlpha) * (_pxVar[i] + (StaticAlpha * d * d));
                }
            }

            const float scale = 1f / (255f * Cell * Cell);
            for (int c = 0; c < _cells; c++)
            {
                float a = first ? 0 : slot[c] * scale;
                slot[c] = a;
                _mean[c] += BaselineAlpha * (a - _mean[c]);
                _meanSq[c] += BaselineAlpha * ((a * a) - _meanSq[c]);
            }

            _ringQpc[_head] = frame.Qpc;
            _head = (_head + 1) % _ringFrames;
            _lastQpc = frame.Qpc;
            _frames++;
            SettleMarks(frame.Qpc);
        }
    }

    public void Reset()
    {
    }

    /// <summary>The user saw <paramref name="eventName"/> just now (<paramref name="qpc"/> on the session clock).</summary>
    public void Mark(string eventName, long qpc)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        lock (_gate)
        {
            _pendingMarks.Add((eventName, qpc));
        }
    }

    /// <summary>Marks taken into each event's map so far.</summary>
    public IReadOnlyDictionary<string, int> MarkCounts()
    {
        lock (_gate)
        {
            return _maps.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
        }
    }

    /// <summary>Forgets every mark (for example when the profile changes).</summary>
    public void ClearMarks()
    {
        lock (_gate)
        {
            _maps.Clear();
            _pendingMarks.Clear();
        }
    }

    /// <summary>Per-cell static fraction, 0..1, row-major; for the panel's overview.</summary>
    public float[] StaticCells()
    {
        lock (_gate)
        {
            var cells = new float[_cells];
            if (_frames < 2)
            {
                return cells;
            }

            for (int row = 0; row < _rows * Cell; row++)
            {
                for (int col = 0; col < _cols * Cell; col++)
                {
                    int i = (row * _w) + col;
                    if (IsStatic(i))
                    {
                        cells[((row / Cell) * _cols) + (col / Cell)] += 1f / (Cell * Cell);
                    }
                }
            }

            return cells;
        }
    }

    /// <summary>The average response map of an event's marks, z-scores per cell; null with no marks.</summary>
    public float[]? ResponseMap(string eventName)
    {
        lock (_gate)
        {
            if (!_maps.TryGetValue(eventName, out (float[] Sum, int Count) m) || m.Count == 0)
            {
                return null;
            }

            return [.. m.Sum.Select(v => v / m.Count)];
        }
    }

    /// <summary>
    /// Where the cells that respond at <paramref name="eventName"/>'s marks
    /// sit near <paramref name="current"/>: the connected group around the
    /// strongest cell in a search window around the current estimate.
    /// </summary>
    public RegionProposal? ProposeFromMarks(string eventName, HudRegion current, int minMarks = 3)
    {
        ArgumentNullException.ThrowIfNull(current);
        float[]? map = ResponseMap(eventName);
        int marks = MarkCounts().GetValueOrDefault(eventName);
        if (map is null || marks < minMarks)
        {
            return null;
        }

        (int c0, int r0, int c1, int r1) = Window(current);
        int best = -1;
        for (int r = r0; r < r1; r++)
        {
            for (int c = c0; c < c1; c++)
            {
                int i = (r * _cols) + c;
                if (best < 0 || map[i] > map[best])
                {
                    best = i;
                }
            }
        }

        if (best < 0 || map[best] < MinResponse)
        {
            return null;
        }

        float cut = Math.Max(MinResponse, map[best] * 0.5f);
        Rect box = Grow(best, i => map[i] >= cut, c0, r0, c1, r1);
        return ToProposal(current.Name, box, "marks", map[best],
            $"{marks} marks of {eventName}; strongest response z {map[best]:0.0}, cells above z {cut:0.0} kept");
    }

    /// <summary>
    /// The static (HUD-like) pixels near <paramref name="current"/>: the
    /// group of mostly static cells that overlaps the estimate most.
    /// </summary>
    public RegionProposal? ProposeStatic(HudRegion current, long minFrames = 150)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (Frames < minFrames)
        {
            return null;
        }

        float[] cells = StaticCells();
        (int c0, int r0, int c1, int r1) = Window(current);
        (int ic0, int ir0, int ic1, int ir1) = CellRect(current.X, current.Y, current.X + current.W, current.Y + current.H);
        var seen = new HashSet<int>();
        Rect? bestBox = null;
        int bestOverlap = 0;
        for (int r = ir0; r < ir1; r++)
        {
            for (int c = ic0; c < ic1; c++)
            {
                int i = (r * _cols) + c;
                if (cells[i] < 0.3f || seen.Contains(i))
                {
                    continue;
                }

                Rect box = Grow(i, j => cells[j] >= 0.3f, c0, r0, c1, r1, seen);
                int overlap = box.Overlap(ic0, ir0, ic1, ir1);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    bestBox = box;
                }
            }
        }

        if (bestBox is not { } b)
        {
            return null;
        }

        double inside = (double)bestOverlap / Math.Max(1, (ic1 - ic0) * (ir1 - ir0));
        return ToProposal(current.Name, b, "static", inside,
            $"static HUD pixels over {inside:P0} of the current rectangle, after {Frames} frames");
    }

    private readonly record struct Rect(int C0, int R0, int C1, int R1)
    {
        public int Overlap(int c0, int r0, int c1, int r1) =>
            Math.Max(0, Math.Min(C1, c1) - Math.Max(C0, c0)) * Math.Max(0, Math.Min(R1, r1) - Math.Max(R0, r0));
    }

    private Rect Grow(int start, Func<int, bool> member, int c0, int r0, int c1, int r1, HashSet<int>? seen = null)
    {
        seen ??= [];
        var queue = new Queue<int>();
        queue.Enqueue(start);
        seen.Add(start);
        int minC = int.MaxValue, minR = int.MaxValue, maxC = -1, maxR = -1;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int c = i % _cols, r = i / _cols;
            minC = Math.Min(minC, c);
            maxC = Math.Max(maxC, c);
            minR = Math.Min(minR, r);
            maxR = Math.Max(maxR, r);
            foreach ((int dc, int dr) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
            {
                int nc = c + dc, nr = r + dr;
                int n = (nr * _cols) + nc;
                if (nc >= c0 && nc < c1 && nr >= r0 && nr < r1 && !seen.Contains(n) && member(n))
                {
                    seen.Add(n);
                    queue.Enqueue(n);
                }
            }
        }

        return new Rect(minC, minR, maxC + 1, maxR + 1);
    }

    private RegionProposal ToProposal(string name, Rect b, string source, double strength, string evidence)
    {
        double x = (double)b.C0 * Cell / _w, y = (double)b.R0 * Cell / _h;
        double w = Math.Min(1 - x, (double)(b.C1 - b.C0) * Cell / _w), h = Math.Min(1 - y, (double)(b.R1 - b.R0) * Cell / _h);
        return new RegionProposal(name, Math.Round(x, 3), Math.Round(y, 3), Math.Round(w, 3), Math.Round(h, 3), source, strength, evidence);
    }

    // The estimate grown on every side by half its size, at least 8% of the frame.
    private (int, int, int, int) Window(HudRegion r)
    {
        double gx = Math.Max(r.W / 2, 0.08), gy = Math.Max(r.H / 2, 0.08);
        return CellRect(r.X - gx, r.Y - gy, r.X + r.W + gx, r.Y + r.H + gy);
    }

    private (int, int, int, int) CellRect(double x0, double y0, double x1, double y1) =>
        (Math.Clamp((int)Math.Floor(x0 * _w / Cell), 0, _cols - 1),
         Math.Clamp((int)Math.Floor(y0 * _h / Cell), 0, _rows - 1),
         Math.Clamp((int)Math.Ceiling(x1 * _w / Cell), 1, _cols),
         Math.Clamp((int)Math.Ceiling(y1 * _h / Cell), 1, _rows));

    private bool IsStatic(int i) => _pxVar[i] < StaticStdDev * StaticStdDev && _pxMean[i] > Letterbox;

    private void SettleMarks(long now)
    {
        long lead = QpcTime.FromTimeSpan(MarkLead, _frequency), lag = QpcTime.FromTimeSpan(MarkLag, _frequency);
        for (int k = 0; k < _pendingMarks.Count; k++)
        {
            (string name, long at) = _pendingMarks[k];
            if (now < at + lag)
            {
                continue;
            }

            if (!_maps.TryGetValue(name, out (float[] Sum, int Count) m))
            {
                m = (new float[_cells], 0);
            }

            for (int c = 0; c < _cells; c++)
            {
                float sd = Math.Max(0.005f, MathF.Sqrt(Math.Max(0, _meanSq[c] - (_mean[c] * _mean[c]))));
                float peak = 0;
                for (int f = 0; f < _ringFrames; f++)
                {
                    long q = _ringQpc[f];
                    if (q >= at - lead && q <= at + lag && f < _frames)
                    {
                        peak = Math.Max(peak, (_ring[(f * _cells) + c] - _mean[c]) / sd);
                    }
                }

                m.Sum[c] += peak;
            }

            _maps[name] = (m.Sum, m.Count + 1);
            _pendingMarks.RemoveAt(k--);
        }
    }
}
