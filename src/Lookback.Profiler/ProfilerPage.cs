using Lookback.Stream.WebSockets;

namespace Lookback.Profiler;

/// <summary>
/// The live event profiler page. Served by <see cref="WebSocketBridge"/> at
/// <see cref="Path"/>; it reads the token from its URL fragment and opens
/// <c>/events</c> on its own origin.
/// </summary>
public static class ProfilerPage
{
    public const string Path = "/profiler";

    private static readonly Lazy<string> Content = new(Load);

    /// <summary>The page's HTML.</summary>
    public static string Html => Content.Value;

    /// <summary>A <see cref="BridgePage"/> for <see cref="WebSocketBridgeOptions.Pages"/>.</summary>
    public static BridgePage Page => new(Path, Html);

    private static string Load()
    {
        using System.IO.Stream stream = typeof(ProfilerPage).Assembly.GetManifestResourceStream("profiler.html")
            ?? throw new InvalidOperationException("The profiler page is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
