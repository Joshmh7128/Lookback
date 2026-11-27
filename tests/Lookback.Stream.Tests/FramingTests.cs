using System.Buffers.Binary;
using Lookback.Contracts.V1;
using Lookback.Stream.Framing;

namespace Lookback.Stream.Tests;

public class FramingTests
{
    [Fact]
    public async Task RoundTripsSeveralFrames()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var ms = new MemoryStream();
        for (ulong i = 1; i <= 3; i++)
        {
            await EnvelopeFraming.WriteAsync(ms, TestEvents.Signal(i, values: [i, i * 2]), ct);
        }

        ms.Position = 0;
        for (ulong i = 1; i <= 3; i++)
        {
            Envelope? e = await EnvelopeFraming.ReadAsync(ms, ct);
            Assert.NotNull(e);
            Assert.Equal(i, e.Qpc);
            Assert.Equal([i, i * 2f], e.SignalFrame.Values);
        }

        Assert.Null(await EnvelopeFraming.ReadAsync(ms, ct));
    }

    [Fact]
    public async Task TornFrameThrowsEndOfStream()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var ms = new MemoryStream();
        await EnvelopeFraming.WriteAsync(ms, TestEvents.Signal(1, values: [1, 2, 3]), ct);
        using var torn = new MemoryStream(ms.ToArray()[..^2]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await EnvelopeFraming.ReadAsync(torn, ct));
    }

    [Fact]
    public async Task HostileLengthIsRejectedBeforeAllocating()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        using var ms = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await EnvelopeFraming.ReadAsync(ms, TestContext.Current.CancellationToken));
    }
}
