using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Lookback.Contracts;
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

    /// <summary>
    /// Static pages served next to <c>/events</c>, such as the live profiler.
    /// When any are configured, the bridge's own origin
    /// (<c>http://127.0.0.1:port</c> and <c>http://localhost:port</c>) is also
    /// allowed, so those pages can open the socket. Empty by default: the
    /// shipping recorder serves nothing but the socket.
    /// </summary>
    public IReadOnlyList<BridgePage> Pages { get; init; } = [];

    /// <summary>
    /// Send each new client the current sessions' <c>SessionStarted</c>,
    /// <c>GameLifecycle</c> and <c>SignalSchema</c> envelopes before live
    /// events, so a viewer that connects mid-session can still decode frames.
    /// A client may see one of these twice if it is published while it
    /// connects; consumers treat them as idempotent.
    /// </summary>
    public bool ReplaySnapshot { get; init; } = true;
}

/// <summary>A self-contained HTML page the bridge serves at <paramref name="Path"/>.</summary>
/// <param name="Path">Request path, starting with <c>/</c>.</param>
/// <param name="Html">The whole page. It must not embed secrets; clients carry the token in the URL fragment.</param>
public sealed record BridgePage(string Path, string Html);

/// <summary>
/// Streams bus events to the Electron shell as JSON text frames over a
/// loopback-only WebSocket at <c>/events</c>. Read-only in M0; the rating and
/// clip-request messages arrive with the clipper milestone. Optionally serves
/// static pages (the live profiler) from the same origin.
/// </summary>
public sealed class WebSocketBridge : IAsyncDisposable
{
    private static readonly JsonFormatter Json = new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));

    private readonly IEventBus _bus;
    private readonly WebSocketBridgeOptions _options;
    private WebApplication? _app;
    private StreamSnapshot? _snapshot;
    private readonly HashSet<string> _origins;
    private int _clients;
    private readonly CancellationTokenSource _shutdown = new();

    public WebSocketBridge(IEventBus bus, WebSocketBridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _options = options ?? new WebSocketBridgeOptions();
        Token = _options.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _origins = new HashSet<string>(_options.AllowedOrigins, StringComparer.OrdinalIgnoreCase);
        foreach (BridgePage page in _options.Pages)
        {
            if (!page.Path.StartsWith('/') || page.Path == "/events")
            {
                throw new ArgumentException($"Invalid page path '{page.Path}'.", nameof(options));
            }
        }
    }

    /// <summary>The token clients must present. Hand it to Electron through its launch environment.</summary>
    public string Token { get; }

    /// <summary>ws:// URL of the events endpoint, without the token. Valid after <see cref="StartAsync"/>.</summary>
    public Uri? Endpoint { get; private set; }

    /// <summary>Envelopes the late-join snapshot has processed. For tests.</summary>
    internal long SnapshotSeen => _snapshot?.Seen ?? 0;

    /// <summary>Clients currently connected to <c>/events</c>.</summary>
    public int ConnectedClients => Volatile.Read(ref _clients);

    /// <summary>
    /// http:// URL of a served page with the token in the fragment. The
    /// fragment never leaves the browser, so the token is not sent in the
    /// page request, logged, or passed on as a referrer.
    /// </summary>
    public Uri PageUrl(string path)
    {
        if (Endpoint is null)
        {
            throw new InvalidOperationException("The bridge has not started.");
        }

        if (!_options.Pages.Any(p => p.Path == path))
        {
            throw new ArgumentException($"No page is served at '{path}'.", nameof(path));
        }

        return new Uri($"http://{Endpoint.Authority}{path}#token={Token}");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, _options.Port));

        WebApplication app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Map("/events", HandleAsync);
        foreach (BridgePage page in _options.Pages)
        {
            byte[] body = Encoding.UTF8.GetBytes(page.Html);
            app.MapGet(page.Path, (HttpContext c) => ServePageAsync(c, body));
        }

        if (_options.ReplaySnapshot)
        {
            _snapshot = new StreamSnapshot(_bus);
        }

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        string address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        Endpoint = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal).TrimEnd('/') + "/events");
        if (_options.Pages.Count > 0)
        {
            // The page's own origin, under both loopback names a user might type.
            _origins.Add($"http://127.0.0.1:{Endpoint.Port}");
            _origins.Add($"http://localhost:{Endpoint.Port}");
        }

        _app = app;
    }

    private static async Task ServePageAsync(HttpContext context, byte[] body)
    {
        IHeaderDictionary h = context.Response.Headers;
        h.ContentType = "text/html; charset=utf-8";
        h.CacheControl = "no-store";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        // Inline script and style only, no network except this origin's socket, no framing.
        h.ContentSecurityPolicy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string? origin = context.Request.Headers.Origin;
        if (origin is not null && !_origins.Contains(origin))
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
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _shutdown.Token);
        CancellationToken ct = closed.Token;
        Interlocked.Increment(ref _clients);

        // Inbound messages are ignored for now; a client close ends the send loop.
        Task receive = DrainReceivesAsync(socket, ct).ContinueWith(
            _ => closed.Cancel(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            // Subscribed first, so nothing published from here on is missed.
            if (_snapshot is not null)
            {
                foreach (Envelope envelope in _snapshot.Capture())
                {
                    if (Topics.Matches(_options.TopicPattern, envelope.Topic))
                    {
                        await SendAsync(socket, envelope, ct).ConfigureAwait(false);
                    }
                }
            }

            await foreach (Envelope envelope in sub.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await SendAsync(socket, envelope, ct).ConfigureAwait(false);
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
        Interlocked.Decrement(ref _clients);
    }

    private static async Task SendAsync(WebSocket socket, Envelope envelope, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(Json.Format(envelope));
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
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
        // End open sockets first: the server otherwise waits for clients to hang up.
        if (!_shutdown.IsCancellationRequested)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        if (_snapshot is not null)
        {
            await _snapshot.DisposeAsync().ConfigureAwait(false);
            _snapshot = null;
        }

        _shutdown.Dispose();
    }
}
