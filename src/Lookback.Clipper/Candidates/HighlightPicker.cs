namespace Lookback.Clipper.Candidates;

/// <summary>
/// Where the highlights of a long candidate could be: one window around each
/// of its strongest moments (game events and curve peaks). A window starts at
/// the shortest highlight length with the moment <see cref="Lead"/> of the way
/// in (more build-up than aftermath), then grows to take in the strongest
/// nearby moments while it stays within the longest length, and is kept
/// inside the candidate. The engine scores each window as a candidate of its
/// own and keeps the best that do not overlap.
/// </summary>
internal static class HighlightPicker
{
    /// <summary>How far into a window its moment sits.</summary>
    public const double Lead = 0.6;

    /// <summary>At most this many moments are tried as anchors.</summary>
    public const int MaxAnchors = 8;

    /// <summary>The windows to try, strongest anchor first, without duplicates.</summary>
    /// <param name="items">The candidate's moments.</param>
    /// <param name="start">The candidate's start.</param>
    /// <param name="end">The candidate's end.</param>
    /// <param name="min">Shortest highlight, in ticks.</param>
    /// <param name="max">Longest highlight, in ticks.</param>
    /// <param name="frequency">Ticks per second.</param>
    public static List<(long Start, long End)> Windows(IReadOnlyList<Contribution> items, long start, long end, long min, long max, long frequency)
    {
        var windows = new List<(long, long)>();
        if (items.Count == 0 || end <= start || min <= 0 || max < min)
        {
            return windows;
        }

        long before = (long)(min * Lead), after = min - before;
        long reachBefore = 3 * frequency, reachAfter = 2 * frequency; // a moment taken in brings a little build-up and aftermath
        foreach (Contribution anchor in items.OrderByDescending(c => c.Score).ThenBy(c => c.At).Take(MaxAnchors))
        {
            long a = anchor.At - before, b = anchor.At + after;
            while (true)
            {
                Contribution? next = null;
                long na = a, nb = b;
                foreach (Contribution c in items)
                {
                    if (c.At >= a && c.At <= b)
                    {
                        continue;
                    }

                    long ca = Math.Min(a, c.At - reachBefore), cb = Math.Max(b, c.At + reachAfter);
                    if (cb - ca <= max && (next is null || c.Score > next.Score))
                    {
                        next = c;
                        na = ca;
                        nb = cb;
                    }
                }

                if (next is null)
                {
                    break;
                }

                a = na;
                b = nb;
            }

            // Inside the candidate, keeping the length where it can.
            if (a < start)
            {
                b = Math.Min(end, b + (start - a));
                a = start;
            }

            if (b > end)
            {
                a = Math.Max(start, a - (b - end));
                b = end;
            }

            if (!windows.Contains((a, b)))
            {
                windows.Add((a, b));
            }
        }

        return windows;
    }
}
