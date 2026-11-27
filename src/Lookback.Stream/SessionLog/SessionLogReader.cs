using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Lookback.Contracts.V1;
using Lookback.Stream.Framing;

namespace Lookback.Stream.SessionLog;

/// <summary>
/// Reads a session log, from the start or from a point in session time. A log
/// whose writer crashed mid-record is read up to the last complete record.
/// </summary>
public sealed class SessionLogReader : IAsyncDisposable
{
    private readonly FileStream _log;
    private readonly (ulong Watermark, long Offset)[] _index;
    private readonly long _firstRecordOffset;

    private SessionLogReader(FileStream log, (ulong, long)[] index, Envelope header, long firstRecordOffset)
    {
        _log = log;
        _index = index;
        Header = header;
        _firstRecordOffset = firstRecordOffset;
    }

    /// <summary>The session's first record, carrying <c>SessionStarted</c>.</summary>
    public Envelope Header { get; }

    public static async Task<SessionLogReader> OpenAsync(string basePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(basePath);

        var log = new FileStream(basePath + SessionLogFormat.LogExtension, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
        try
        {
            byte[] magic = new byte[SessionLogFormat.Magic.Length];
            await log.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
            if (!magic.AsSpan().SequenceEqual(SessionLogFormat.Magic))
            {
                throw new InvalidDataException("Not a Lookback session log.");
            }

            long first = log.Position;
            Envelope header = await EnvelopeFraming.ReadAsync(log, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Session log has no header record.");
            if (header.PayloadCase != Envelope.PayloadOneofCase.SessionStarted)
            {
                throw new InvalidDataException("Session log does not start with SessionStarted.");
            }

            (ulong, long)[] index = await ReadIndexAsync(basePath + SessionLogFormat.IndexExtension, cancellationToken).ConfigureAwait(false);
            return new SessionLogReader(log, index, header, first);
        }
        catch
        {
            await log.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<(ulong, long)[]> ReadIndexAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        int count = bytes.Length / SessionLogFormat.IndexEntrySize; // ignore a torn final entry
        var entries = new (ulong, long)[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = bytes.AsSpan(i * SessionLogFormat.IndexEntrySize, SessionLogFormat.IndexEntrySize);
            entries[i] = (BinaryPrimitives.ReadUInt64LittleEndian(e), (long)BinaryPrimitives.ReadUInt64LittleEndian(e[8..]));
        }

        return entries;
    }

    /// <summary>
    /// Every record with <c>qpc &gt;= fromQpc</c>, in file order. With no
    /// argument, every record including the header.
    /// </summary>
    public async IAsyncEnumerable<Envelope> ReadAsync(ulong fromQpc = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _log.Position = FindStartOffset(fromQpc);
        while (true)
        {
            Envelope? envelope;
            try
            {
                envelope = await EnvelopeFraming.ReadAsync(_log, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                yield break; // torn tail from an unclean shutdown
            }

            if (envelope is null)
            {
                yield break;
            }

            if (envelope.Qpc >= fromQpc)
            {
                yield return envelope;
            }
        }
    }

    /// <summary>Offset of the last index entry whose watermark is strictly below <paramref name="fromQpc"/>.</summary>
    internal long FindStartOffset(ulong fromQpc)
    {
        int lo = 0;
        int hi = _index.Length - 1;
        int best = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (_index[mid].Watermark < fromQpc)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best >= 0 ? Math.Max(_index[best].Offset, _firstRecordOffset) : _firstRecordOffset;
    }

    public ValueTask DisposeAsync() => _log.DisposeAsync();
}
