using System.Buffers.Binary;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Stream.Framing;

namespace Lookback.Stream.SessionLog;

/// <summary>
/// Append-only writer for one session's events. Refuses to create a file at
/// all unless the session's retention allows persistence, so an ephemeral
/// source (an official player) cannot leave anything on disk by mistake.
/// Not thread-safe: feed it from one consumer, such as <see cref="SessionLogRecorder"/>.
/// </summary>
public sealed class SessionLogWriter : IAsyncDisposable
{
    private readonly FileStream _log;
    private readonly FileStream _index;
    private readonly long _indexIntervalTicks;
    private readonly byte[] _indexEntry = new byte[SessionLogFormat.IndexEntrySize];
    private ulong _watermark;
    private ulong _lastIndexedWatermark;
    private bool _anyIndexed;

    private SessionLogWriter(string basePath, FileStream log, FileStream index, long indexIntervalTicks)
    {
        BasePath = basePath;
        _log = log;
        _index = index;
        _indexIntervalTicks = indexIntervalTicks;
    }

    /// <summary>Path without extension; the log and index sit beside each other.</summary>
    public string BasePath { get; }

    public long RecordCount { get; private set; }

    /// <param name="directory">Folder to create the log in.</param>
    /// <param name="sessionStart">An envelope whose payload is <c>SessionStarted</c>.</param>
    /// <param name="indexInterval">Roughly how often (in session time) to add an index entry.</param>
    /// <param name="cancellationToken">Cancels the header write.</param>
    /// <exception cref="RetentionViolationException">The session may not be persisted.</exception>
    public static async Task<SessionLogWriter> CreateAsync(
        string directory, Envelope sessionStart, TimeSpan? indexInterval = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(sessionStart);

        SessionStarted started = sessionStart.SessionStarted
            ?? throw new ArgumentException("The first record must carry SessionStarted.", nameof(sessionStart));

        string? problem = RetentionRules.Validate(started);
        if (problem is not null)
        {
            throw new RetentionViolationException(problem);
        }

        if (!RetentionRules.CanPersist(started))
        {
            throw new RetentionViolationException($"Session {sessionStart.SessionId} is ephemeral; nothing may be written to disk.");
        }

        if (string.IsNullOrEmpty(sessionStart.SessionId) || sessionStart.SessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Session id must be a valid file name.", nameof(sessionStart));
        }

        Directory.CreateDirectory(directory);
        string basePath = Path.Combine(directory, sessionStart.SessionId);
        long frequency = (long)started.QpcFrequency;
        long interval = frequency > 0
            ? (long)((indexInterval ?? TimeSpan.FromSeconds(1)).TotalSeconds * frequency)
            : 0;

        // FileShare.Read lets the app tail a live session while it is recorded.
        var log = new FileStream(basePath + SessionLogFormat.LogExtension, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
        var index = new FileStream(basePath + SessionLogFormat.IndexExtension, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);

        var writer = new SessionLogWriter(basePath, log, index, interval);
        await log.WriteAsync(SessionLogFormat.Magic.ToArray(), cancellationToken).ConfigureAwait(false);
        await writer.AppendAsync(sessionStart, cancellationToken).ConfigureAwait(false);
        return writer;
    }

    public async ValueTask AppendAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!_anyIndexed || (long)(_watermark - _lastIndexedWatermark) >= _indexIntervalTicks)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_indexEntry, _watermark);
            BinaryPrimitives.WriteUInt64LittleEndian(_indexEntry.AsSpan(8), (ulong)_log.Position);
            await _index.WriteAsync(_indexEntry, cancellationToken).ConfigureAwait(false);
            _lastIndexedWatermark = _watermark;
            _anyIndexed = true;
        }

        await EnvelopeFraming.WriteAsync(_log, envelope, cancellationToken).ConfigureAwait(false);
        _watermark = Math.Max(_watermark, envelope.Qpc);
        RecordCount++;
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _log.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _index.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _log.DisposeAsync().ConfigureAwait(false);
        await _index.DisposeAsync().ConfigureAwait(false);
    }
}
