using System.IO.Pipes;
using Lookback.Contracts.V1;
using Lookback.Stream.Framing;

namespace Lookback.Stream.Pipes;

/// <summary>
/// Client end of the pipe transport, used by the out-of-process producers and
/// consumers (Signals, Clipper, tools). Writes are serialised; reads are for a
/// single reader.
/// </summary>
public sealed class PipeEventClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private PipeEventClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public static async Task<PipeEventClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            return new PipeEventClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends one envelope. Cancellation applies only while waiting for the
    /// write lock; once a frame starts it is always finished, so a cancelled
    /// publish can never leave half a frame on the pipe.
    /// </summary>
    public async ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnvelopeFraming.WriteAsync(_pipe, envelope, CancellationToken.None).ConfigureAwait(false);
            await _pipe.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads envelopes until the server closes the pipe.</summary>
    public async IAsyncEnumerable<Envelope> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Envelope? envelope = await EnvelopeFraming.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                yield break;
            }

            yield return envelope;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
