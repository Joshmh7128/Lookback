namespace Lookback.Stream.SessionLog;

/// <summary>
/// On-disk layout of a session log.
/// <para>
/// <c>{name}.lbklog</c>: an 8-byte magic, then length-prefixed envelopes
/// (see <see cref="Framing.EnvelopeFraming"/>). The first envelope is always
/// the session's <c>SessionStarted</c>.
/// </para>
/// <para>
/// <c>{name}.lbkidx</c>: fixed 16-byte entries of (watermark, offset), both
/// little-endian u64. <c>watermark</c> is the highest qpc of every record
/// before <c>offset</c>. Producers on different threads can publish slightly
/// out of order, so record timestamps are not monotonic; the watermark makes
/// seeking correct anyway: starting at an entry whose watermark is below the
/// target can never skip a record at or after the target.
/// </para>
/// </summary>
public static class SessionLogFormat
{
    public const string LogExtension = ".lbklog";
    public const string IndexExtension = ".lbkidx";
    public const int IndexEntrySize = 16;

    public static ReadOnlySpan<byte> Magic => "LBKLOG\0\u0001"u8;
}

/// <summary>Thrown when something tries to persist a session whose retention forbids it.</summary>
public sealed class RetentionViolationException : InvalidOperationException
{
    public RetentionViolationException()
    {
    }

    public RetentionViolationException(string message)
        : base(message)
    {
    }

    public RetentionViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
