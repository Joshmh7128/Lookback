using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using Lookback.Contracts.V1;

namespace Lookback.Stream.Framing;

/// <summary>
/// Length-prefixed protobuf framing: a 4-byte little-endian length, then the
/// encoded envelope. Used by the pipe transport and the session log.
/// </summary>
public static class EnvelopeFraming
{
    public const int HeaderSize = 4;

    /// <summary>Guards against a corrupt or hostile length prefix allocating gigabytes.</summary>
    public const int MaxFrameSize = 16 * 1024 * 1024;

    public static async ValueTask WriteAsync(System.IO.Stream stream, Envelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);

        int size = envelope.CalculateSize();
        if (size > MaxFrameSize)
        {
            throw new InvalidDataException($"Envelope of {size} bytes exceeds the {MaxFrameSize}-byte frame limit.");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + size);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, size);
            envelope.WriteTo(buffer.AsSpan(HeaderSize, size));
            await stream.WriteAsync(buffer.AsMemory(0, HeaderSize + size), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads one envelope. Returns null at a clean end of stream (no bytes of a
    /// new frame read). Throws <see cref="EndOfStreamException"/> on a frame
    /// cut off part-way.
    /// </summary>
    public static async ValueTask<Envelope?> ReadAsync(System.IO.Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            int read = await stream.ReadAtLeastAsync(header.AsMemory(0, HeaderSize), HeaderSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            if (read < HeaderSize)
            {
                throw new EndOfStreamException("Stream ended inside a frame header.");
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size < 0 || size > MaxFrameSize)
            {
                throw new InvalidDataException($"Frame length {size} is outside 0..{MaxFrameSize}.");
            }

            byte[] body = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
            try
            {
                await stream.ReadExactlyAsync(body.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
                return Envelope.Parser.ParseFrom(body, 0, size);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(body);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }
}
