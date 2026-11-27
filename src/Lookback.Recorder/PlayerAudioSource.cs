using System.Runtime.InteropServices;
using Lookback.Audio;
using Lookback.Contracts.V1;
using Lookback.Media.Frames;
using Microsoft.Extensions.Logging;

namespace Lookback.Recorder;

/// <summary>
/// The official player's sound, for analysis only: process loopback of the
/// desktop shell that started the recorder (<c>Recorder:ParentProcessId</c>),
/// with its child processes, which is where Chromium plays the embed's audio.
/// Never the whole system: <see cref="AudioCapturePolicy"/> gives official
/// players no endpoint fallback, and nothing is encoded or stored. Anything
/// else the shell plays (a clip preview in the library) is heard as well.
/// </summary>
internal static partial class PlayerAudioSource
{
    /// <summary>The calibration host's audio hook, or null when there is no shell to isolate (no parent pid) or this is not Windows.</summary>
    public static Func<SessionStarted, Action<AudioBlock>, IDisposable?>? For(int? shellPid, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!OperatingSystem.IsWindows() || shellPid is not { } pid || pid <= 0)
        {
            return null;
        }

        return (started, onHop) => Start(started, (uint)pid, onHop, logger);
    }

    private static WasapiSource? Start(SessionStarted started, uint pid, Action<AudioBlock> onHop, ILogger logger)
    {
        AudioPlan plan = AudioCapturePolicy.Decide(started, AudioCapturePolicy.OsBuild, pid, microphone: false);
        if (plan.Game is null)
        {
            LogVideoOnly(logger, plan.Reason);
            return null;
        }

        try
        {
            WasapiSource source = WasapiSource.Start(
                AudioSourceOptions.ForGame(plan, pid) with { Log = line => LogNote(logger, line) },
                onPcm: null,
                onHop);
            LogHearing(logger, plan.Reason);
            return source;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            LogVideoOnly(logger, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Official player audio: {Plan}")]
    private static partial void LogHearing(ILogger logger, string plan);

    [LoggerMessage(Level = LogLevel.Information, Message = "Official player is video only: {Reason}")]
    private static partial void LogVideoOnly(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Official player audio: {Line}")]
    private static partial void LogNote(ILogger logger, string line);
}
