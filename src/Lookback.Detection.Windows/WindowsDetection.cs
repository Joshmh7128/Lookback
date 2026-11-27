using System.Runtime.Versioning;
using Lookback.Capture.Targets;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Detection.Lifecycle;
using Lookback.Detection.Matching;
using Lookback.Profiles.Model;

namespace Lookback.Detection.Windows;

/// <summary>What the recorder needs on Windows: a runner over the real machine, and the capture target for a game.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsDetection
{
    /// <summary>
    /// A <see cref="DetectionRunner"/> over this machine with the built-in
    /// profiles (or <paramref name="catalog"/>) and the user's settings from
    /// <paramref name="settingsPath"/> (default <see cref="DetectionSettings.DefaultPath"/>).
    /// </summary>
    public static DetectionRunner CreateRunner(IEventBus bus, ProfileCatalog? catalog = null, string? settingsPath = null, DetectionOptions? options = null, bool start = true)
    {
        DetectionSettings settings = DetectionSettings.Load(settingsPath ?? DetectionSettings.DefaultPath);
        return new DetectionRunner(bus, new WindowsSystemProbe(), new GameMatcher(catalog ?? ProfileCatalog.BuiltIn, settings), SystemClock.Instance, options, start);
    }

    /// <summary>
    /// The window capture target for <paramref name="game"/>, or null when its
    /// window has gone or its monitor is not driven by any adapter (then
    /// capture its monitor, or wait for the next <c>WINDOW_CHANGED</c>).
    /// </summary>
    public static CaptureTarget? TryCaptureTarget(GameInstance game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Window.Handle == 0)
        {
            return null;
        }

        try
        {
            return CaptureTarget.ForWindow((nint)game.Window.Handle);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or SharpGen.Runtime.SharpGenException)
        {
            return null;
        }
    }
}
