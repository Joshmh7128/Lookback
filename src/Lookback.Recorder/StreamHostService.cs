using System.Text.Json;
using Lookback.Calibration;
using Lookback.Core.Bus;
using Lookback.Profiler;
using Lookback.Stream.Pipes;
using Lookback.Stream.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lookback.Recorder;

/// <summary>
/// Starts the pipe server and the WebSocket bridge, then announces the bridge
/// on stdout as a single <c>LOOKBACK_READY {json}</c> line. Electron spawns the
/// recorder and reads that line to learn the endpoint and token, so the token
/// never touches disk or the command line.
/// </summary>
internal sealed partial class StreamHostService(
    IEventBus bus,
    IOptions<RecorderOptions> options,
    ILogger<StreamHostService> logger) : IHostedService, IAsyncDisposable
{
    private PipeEventServer? _pipes;
    private WebSocketBridge? _bridge;
    private CalibrationHost? _calibration;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RecorderOptions o = options.Value;

        _pipes = new PipeEventServer(bus, o.PipeName);
        _pipes.Start();

        // Official-player sessions are ephemeral: the calibration host keeps
        // frames and features in memory and writes only numbers the user confirms.
        _calibration = o.OfficialPlayer ? new CalibrationHost(bus, new CalibrationHostOptions { ProfilesDirectory = o.ProfilesDirectory }) : null;
        _bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions
        {
            Port = o.WebSocketPort,
            Pages = [.. o.Profiler ? [ProfilerPage.Page] : Array.Empty<BridgePage>(), .. _calibration is null ? [] : new[] { CalibrationHost.Page }],
            Sockets = _calibration?.Sockets ?? [],
        });
        await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);

        LogStarted(logger, o.PipeName, _bridge.Endpoint!);
        string ready = JsonSerializer.Serialize(new
        {
            pipe = o.PipeName,
            ws = _bridge.Endpoint!.ToString(),
            token = _bridge.Token,
            ingest = _calibration is null ? null : _bridge.SocketUrl(CalibrationHost.IngestPath).ToString(),
            calibrate = _calibration is null ? null : $"http://{_bridge.Endpoint.Authority}{CalibrationHost.PanelPath}",
        });
        await Console.Out.WriteLineAsync($"LOOKBACK_READY {ready}").ConfigureAwait(false);
        if (o.Profiler)
        {
            // Same channel as the token above: stdout only, never a log.
            await Console.Out.WriteLineAsync($"LOOKBACK_PROFILER {_bridge.PageUrl(ProfilerPage.Path)}").ConfigureAwait(false);
        }

        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null)
        {
            await _bridge.DisposeAsync().ConfigureAwait(false);
            _bridge = null;
        }

        if (_calibration is not null)
        {
            await _calibration.DisposeAsync().ConfigureAwait(false);
            _calibration = null;
        }

        if (_pipes is not null)
        {
            await _pipes.DisposeAsync().ConfigureAwait(false);
            _pipes = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event stream up: pipe {Pipe}, WebSocket {Endpoint}")]
    private static partial void LogStarted(ILogger logger, string pipe, Uri endpoint);
}
