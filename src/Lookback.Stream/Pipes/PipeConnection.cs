using System.Runtime.CompilerServices;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Stream.Framing;

namespace Lookback.Stream.Pipes;

/// <summary>
/// Bridges one duplex stream onto the bus in both directions. Envelopes that
/// arrived on this connection are not echoed back to it.
/// </summary>
internal sealed class PipeConnection
{
    // Tags envelopes with the connection they came in on, without keeping them alive.
    private static readonly ConditionalWeakTable<Envelope, PipeConnection> Origins = new();

    private readonly System.IO.Stream _stream;
    private readonly IEventBus _bus;
    private readonly string _outboundPattern;
    private readonly bool _acceptInbound;

    public PipeConnection(System.IO.Stream stream, IEventBus bus, string outboundPattern, bool acceptInbound)
    {
        _stream = stream;
        _bus = bus;
        _outboundPattern = outboundPattern;
        _acceptInbound = acceptInbound;
    }

    /// <summary>Runs until the peer disconnects or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IEventSubscription sub = _bus.Subscribe(_outboundPattern);

        Task outbound = PumpOutboundAsync(sub, linked.Token);
        Task inbound = PumpInboundAsync(linked.Token);

        // Either side ending (peer closed, write failed) ends the connection.
        await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(outbound, inbound).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task PumpOutboundAsync(IEventSubscription sub, CancellationToken ct)
    {
        await foreach (Envelope envelope in sub.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (Origins.TryGetValue(envelope, out PipeConnection? origin) && ReferenceEquals(origin, this))
            {
                continue;
            }

            await EnvelopeFraming.WriteAsync(_stream, envelope, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task PumpInboundAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Envelope? envelope = await EnvelopeFraming.ReadAsync(_stream, ct).ConfigureAwait(false);
            if (envelope is null)
            {
                return; // peer closed cleanly
            }

            if (_acceptInbound)
            {
                Origins.AddOrUpdate(envelope, this);
                _bus.Publish(envelope);
            }
        }
    }
}
