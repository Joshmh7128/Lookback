using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lookback.Stream.WebSockets;

/// <summary>Options for <see cref="WebSocketBridge"/>.</summary>
public sealed class WebSocketBridgeOptions
{
    /// <summary>0 picks a free port; read it back from <see cref="WebSocketBridge.Endpoint"/>.</summary>
    public int Port { get; init; }

    /// <summary>Topics streamed to clients.</summary>
    public string TopicPattern { get; init; } = "*";

    /// <summary>
    /// Origins allowed to connect. Browsers send an Origin header on WebSocket
    /// upgrades, so this stops a web page the user happens to visit from
    /// reading their event stream. Non-browser clients send none and rely on
    /// the token alone.
    /// </summary>
    public IReadOnlyCollection<string> AllowedOrigins { get; init; } = ["app://lookback", "http://localhost:5173"];

    /// <summary>Shared secret clients pass as <c>?token=</c>. Generated when null.</summary>
    public string? Token { get; init; }
}

/// <summary>
/// Streams bus events to the Electron shell as JSON text frames over a
/// loopback-only WebSocket at <c>/events</c>. Read-only in M0; the rating and
/// clip-request messages arrive with the clipper milestone.
/// </summary>
public sealed class WebSocketBridge : IAsyncDisposable
{
    private static readonly JsonFormatter Json = new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));

    private readonly IEventBus _bus;
    private readonly WebSocketBridgeOptions _options;
    private WebApplication? _app;

    public WebSocketBridge(IEventBus bus, WebSocketBridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _options = options ?? new WebSocketBridgeOptions();
        Token = _options.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The token clients must present. Hand it to Electron through its launch environment.</summary>
    public string Token { get; }

    /// <summary>ws:// URL of the events endpoint, without the token. Valid after <see cref="StartAsync"/>.</summary>
    public Uri? Endpoint { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, _options.Port));

        WebApplication app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Map("/events", HandleAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        string address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        Endpoint = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal).TrimEnd('/') + "/events");
        _app = app;
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string? origin = context.Request.Headers.Origin;
        if (origin is not null && !_options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        string? token = context.Request.Query["token"];
        if (token is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using IEventSubscription sub = _bus.Subscribe(_options.TopicPattern);
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        CancellationToken ct = closed.Token;

        // Inbound messages are ignored for now; a client close ends the send loop.
        Task receive = DrainReceivesAsync(socket, ct).ContinueWith(
            _ => closed.Cancel(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await foreach (Envelope envelope in sub.ReadAllAsync(ct).ConfigureAwait(false))
            {
                byte[] payload = Encoding.UTF8.GetBytes(Json.Format(envelope));
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }

        await receive.ConfigureAwait(false);
    }

    private static async Task DrainReceivesAsync(WebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
