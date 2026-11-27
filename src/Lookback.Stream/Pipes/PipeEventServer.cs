using System.IO.Pipes;
using Lookback.Core.Bus;

namespace Lookback.Stream.Pipes;

/// <summary>
/// Serves the bus over a named pipe to the other Lookback processes. The pipe
/// is restricted to the current user (<see cref="PipeOptions.CurrentUserOnly"/>),
/// so another account on the machine cannot read gameplay events or inject
/// clip requests.
/// </summary>
public sealed class PipeEventServer : IAsyncDisposable
{
    private readonly IEventBus _bus;
    private readonly string _outboundPattern;
    private readonly bool _acceptInbound;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = [];
    private readonly Lock _connectionsLock = new();
    private Task? _acceptLoop;

    public PipeEventServer(IEventBus bus, string pipeName, string outboundPattern = "*", bool acceptInbound = true)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        _bus = bus;
        PipeName = pipeName;
        _outboundPattern = outboundPattern;
        _acceptInbound = acceptInbound;
    }

    public string PipeName { get; }

    public int ActiveConnections
    {
        get
        {
            lock (_connectionsLock)
            {
                return _connections.Count(t => !t.IsCompleted);
            }
        }
    }

    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("Server already started.");
        }

        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            Task connection = ServeAsync(pipe, ct);
            lock (_connectionsLock)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(connection);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe.ConfigureAwait(false))
        {
            var connection = new PipeConnection(pipe, _bus, _outboundPattern, _acceptInbound);
            try
            {
                await connection.RunAsync(ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Peer vanished mid-frame; the connection is simply over.
            }
            catch (InvalidDataException)
            {
                // Peer sent a malformed frame; drop it.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (_connectionsLock)
        {
            pending = [.. _connections];
        }

        if (_acceptLoop is not null)
        {
            pending = [.. pending, _acceptLoop];
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        _cts.Dispose();
    }
}
