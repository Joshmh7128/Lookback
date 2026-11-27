using Microsoft.Extensions.Logging.Abstractions;

namespace Lookback.Recorder.Tests;

/// <summary>The official player's audio hook: only with a desktop shell to isolate, and only on Windows.</summary>
public sealed class PlayerAudioSourceTests
{
    [Fact]
    public void Without_a_shell_process_there_is_no_audio_hook()
    {
        Assert.Null(PlayerAudioSource.For(null, NullLogger.Instance));
        Assert.Null(PlayerAudioSource.For(0, NullLogger.Instance));
    }

    [Fact]
    public void With_a_shell_process_on_windows_there_is_one()
    {
        var hook = PlayerAudioSource.For(Environment.ProcessId, NullLogger.Instance);
        Assert.Equal(OperatingSystem.IsWindows(), hook is not null);
    }
}
