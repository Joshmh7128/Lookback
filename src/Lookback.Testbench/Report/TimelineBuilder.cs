using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Stream.SessionLog;

namespace Lookback.Testbench.Report;

/// <summary>
/// Rebuilds a signal timeline from a session log, so the report shows exactly
/// what was recorded. Video stays at frame resolution; audio hops (10 ms) are
/// bucketed to 100 ms so a long session stays a reasonable page size. Each
/// bucket keeps the maximum, which preserves short spikes such as onsets.
/// </summary>
internal static class TimelineBuilder
{
    public const double AudioBucketSeconds = 0.1;

    public static async Task<TimelineData> BuildAsync(string sessionBasePath, string? videoHref, CancellationToken ct)
    {
        await using SessionLogReader reader = await SessionLogReader.OpenAsync(sessionBasePath, ct).ConfigureAwait(false);
        SessionStarted header = reader.Header.SessionStarted;
        long frequency = header.QpcFrequency > 0 ? (long)header.QpcFrequency : 10_000_000;

        var schemas = new Dictionary<uint, SignalSchema>();
        var steps = new Dictionary<uint, double>(); // seconds per bucket, per schema
        var series = new Dictionary<(uint Schema, int Index), SortedDictionary<long, float>>();
        var melBuckets = new SortedDictionary<long, float[]>();
        var melIndex = new Dictionary<uint, (int Start, int Count)>();
        double maxSeconds = 0;

        await foreach (Envelope e in reader.ReadAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            if (e.PayloadCase == Envelope.PayloadOneofCase.SignalSchema)
            {
                schemas[e.SignalSchema.SchemaId] = e.SignalSchema;
                int start = -1, count = 0;
                for (int i = 0; i < e.SignalSchema.Names.Count; i++)
                {
                    if (e.SignalSchema.Names[i].StartsWith("mel.", StringComparison.Ordinal))
                    {
                        start = start < 0 ? i : start;
                        count++;
                    }
                }

                if (count > 0)
                {
                    melIndex[e.SignalSchema.SchemaId] = (start, count);
                }

                continue;
            }

            if (e.PayloadCase != Envelope.PayloadOneofCase.SignalFrame || !schemas.TryGetValue(e.SignalFrame.SchemaId, out SignalSchema? schema))
            {
                continue;
            }

            SignalFrame f = e.SignalFrame;
            double t = QpcTime.ToTimeSpan((long)f.WindowStartQpc, frequency).TotalSeconds;
            maxSeconds = Math.Max(maxSeconds, QpcTime.ToTimeSpan((long)f.WindowEndQpc, frequency).TotalSeconds);
            bool audio = schema.Family == SignalFamily.Audio;
            if (!steps.TryGetValue(schema.SchemaId, out double step))
            {
                // Video: one bucket per frame, using the frame's own window length. Audio: 100 ms.
                step = audio ? AudioBucketSeconds : Math.Max(1e-3, QpcTime.ToTimeSpan((long)(f.WindowEndQpc - f.WindowStartQpc), frequency).TotalSeconds);
                steps[schema.SchemaId] = step;
            }

            long bucket = audio ? (long)Math.Floor(t / step) : (long)Math.Round(t / step);

            for (int i = 0; i < f.Values.Count && i < schema.Names.Count; i++)
            {
                if (melIndex.TryGetValue(schema.SchemaId, out (int Start, int Count) mel) && i >= mel.Start && i < mel.Start + mel.Count)
                {
                    if (!melBuckets.TryGetValue(bucket, out float[]? row))
                    {
                        row = new float[mel.Count];
                        Array.Fill(row, float.MinValue);
                        melBuckets[bucket] = row;
                    }

                    row[i - mel.Start] = Math.Max(row[i - mel.Start], f.Values[i]);
                    continue;
                }

                if (!series.TryGetValue((schema.SchemaId, i), out SortedDictionary<long, float>? s))
                {
                    s = [];
                    series[(schema.SchemaId, i)] = s;
                }

                s[bucket] = s.TryGetValue(bucket, out float prev) ? Math.Max(prev, f.Values[i]) : f.Values[i];
            }
        }

        var data = new TimelineData
        {
            Title = string.IsNullOrEmpty(header.Title) ? reader.Header.SessionId : header.Title,
            SessionId = reader.Header.SessionId,
            SourceKind = header.SourceKind.ToString(),
            Retention = header.Retention.ToString(),
            Video = videoHref,
            DurationSeconds = maxSeconds,
            MelBucketSeconds = AudioBucketSeconds,
        };

        foreach (((uint schemaId, int index), SortedDictionary<long, float> s) in series.OrderBy(kv => kv.Key.Schema).ThenBy(kv => kv.Key.Index))
        {
            SignalSchema schema = schemas[schemaId];
            data.Lanes.Add(new TimelineData.Lane
            {
                Name = schema.Names[index],
                Family = schema.Family == SignalFamily.Audio ? "audio" : "video",
                Step = steps[schemaId],
                Values = Densify(s),
            });
        }

        foreach (float[] row in Densify(melBuckets))
        {
            data.Mel.Add(row);
        }

        return data;
    }

    /// <summary>Turns sparse buckets into a dense array starting at bucket 0, holding the last value across gaps.</summary>
    private static List<float> Densify(SortedDictionary<long, float> s)
    {
        var dense = new List<float>();
        if (s.Count == 0)
        {
            return dense;
        }

        float hold = 0;
        for (long key = 0; key <= s.Keys.Last(); key++)
        {
            if (s.TryGetValue(key, out float v))
            {
                hold = v;
            }

            dense.Add(hold);
        }

        return dense;
    }

    private static IEnumerable<float[]> Densify(SortedDictionary<long, float[]> rows)
    {
        if (rows.Count == 0)
        {
            yield break;
        }

        float[] hold = rows.First().Value;
        for (long key = 0; key <= rows.Keys.Last(); key++)
        {
            if (rows.TryGetValue(key, out float[]? r))
            {
                hold = r;
            }

            yield return hold;
        }
    }
}
