using Lookback.Contracts;
using Lookback.Contracts.V1;

namespace Lookback.Audio;

/// <summary>Where an audio source's samples come from.</summary>
public enum AudioSourceKind
{
    /// <summary>Endpoint loopback: everything the default render device plays.</summary>
    SystemLoopback,

    /// <summary>One process (and its children) only. Windows build 20348 and later.</summary>
    ProcessLoopback,

    /// <summary>The default capture device (eConsole role).</summary>
    Microphone,
}

/// <summary>What a session's audio is allowed to be. Built by <see cref="AudioCapturePolicy.Decide"/>.</summary>
/// <param name="Game">The game-audio source, or null when the session gets none.</param>
/// <param name="AllowEndpointFallback">Whether a process-loopback request may fall back to the whole endpoint.</param>
/// <param name="Microphone">Whether a mic track is captured.</param>
/// <param name="Encode">Whether audio may reach the AAC encoder, ring and store (<see cref="RetentionRules.CanPersist"/>).</param>
/// <param name="Reason">One line for the log and the bench output.</param>
public sealed record AudioPlan(AudioSourceKind? Game, bool AllowEndpointFallback, bool Microphone, bool Encode, string Reason);

/// <summary>
/// The one place that decides which audio a session captures. Official-player
/// sessions are ephemeral: they never get the encoder or a mic, and never
/// endpoint loopback (it would pick up everything else the machine plays);
/// only process loopback of the player, where the OS has it, for analysis.
/// Everything else follows <see cref="RetentionRules.CanPersist"/>.
/// </summary>
public static class AudioCapturePolicy
{
    /// <summary>First Windows build with <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>.</summary>
    public const int ProcessLoopbackMinBuild = 20348;

    /// <summary>This machine's Windows build number (0 elsewhere).</summary>
    public static int OsBuild => OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0;

    public static bool ProcessLoopbackSupported(int osBuild) => osBuild >= ProcessLoopbackMinBuild;

    public static AudioPlan Decide(SessionStarted session, int osBuild, uint? targetProcessId, bool microphone)
    {
        ArgumentNullException.ThrowIfNull(session);
        string? problem = RetentionRules.Validate(session);
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }

        bool process = targetProcessId is not null && ProcessLoopbackSupported(osBuild);
        if (session.SourceKind == SourceKind.OfficialPlayer)
        {
            return process
                ? new AudioPlan(AudioSourceKind.ProcessLoopback, AllowEndpointFallback: false, Microphone: false, Encode: false,
                    $"official player: process loopback of pid {targetProcessId} for analysis only, nothing encoded")
                : new AudioPlan(null, AllowEndpointFallback: false, Microphone: false, Encode: false,
                    targetProcessId is null
                        ? "official player: no audio (no player process to isolate, and system loopback is never used for official players)"
                        : $"official player: no audio (process loopback needs Windows build {ProcessLoopbackMinBuild}+, this is {osBuild}; system loopback is never used for official players)");
        }

        bool encode = RetentionRules.CanPersist(session);
        bool mic = microphone && encode;
        AudioSourceKind game = process ? AudioSourceKind.ProcessLoopback : AudioSourceKind.SystemLoopback;
        string source = process
            ? $"process loopback of pid {targetProcessId}"
            : targetProcessId is not null
                ? $"system loopback (process loopback needs Windows build {ProcessLoopbackMinBuild}+, this is {osBuild})"
                : "system loopback";
        string encodeNote = encode ? "encoded" : "analysis only (ephemeral)";
        return new AudioPlan(game, AllowEndpointFallback: true, mic, encode, $"{source}, {encodeNote}{(mic ? ", with mic" : "")}");
    }
}
