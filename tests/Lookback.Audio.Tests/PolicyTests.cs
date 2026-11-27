using Lookback.Contracts.V1;

namespace Lookback.Audio.Tests;

/// <summary>Which audio a session may capture. Platform-neutral.</summary>
public class PolicyTests
{
    private const int Win10_1909 = 18363;
    private const int Win10_2004 = 19041;
    private const int Win10_22H2 = 19045; // AETHER
    private const int Server2022 = 20348;

    private static SessionStarted Session(SourceKind kind, Retention retention) => new() { SourceKind = kind, Retention = retention };

    [Fact]
    public void Process_loopback_is_tried_from_windows_10_2004()
    {
        Assert.False(AudioCapturePolicy.ProcessLoopbackSupported(Win10_1909));
        Assert.True(AudioCapturePolicy.ProcessLoopbackSupported(Win10_2004));
        Assert.True(AudioCapturePolicy.ProcessLoopbackSupported(Win10_22H2));
        Assert.True(AudioCapturePolicy.ProcessLoopbackSupported(Server2022));
        Assert.Equal(20348, AudioCapturePolicy.ProcessLoopbackDocumentedBuild);
    }

    [Fact]
    public void Official_player_never_gets_system_loopback_encoder_or_mic()
    {
        SessionStarted s = Session(SourceKind.OfficialPlayer, Retention.Ephemeral);

        AudioPlan old = AudioCapturePolicy.Decide(s, Win10_1909, targetProcessId: 1234, microphone: true);
        Assert.Null(old.Game);
        Assert.False(old.Encode);
        Assert.False(old.Microphone);
        Assert.Contains("19041", old.Reason, StringComparison.Ordinal);

        AudioPlan noPid = AudioCapturePolicy.Decide(s, Win10_22H2, targetProcessId: null, microphone: false);
        Assert.Null(noPid.Game);

        foreach (int build in (int[])[Win10_22H2, Server2022])
        {
            AudioPlan isolated = AudioCapturePolicy.Decide(s, build, targetProcessId: 1234, microphone: true);
            Assert.Equal(AudioSourceKind.ProcessLoopback, isolated.Game);
            Assert.False(isolated.AllowEndpointFallback);
            Assert.False(isolated.Encode);
            Assert.False(isolated.Microphone);
            Assert.False(AudioSourceOptions.ForGame(isolated, 1234).AllowEndpointFallback);
        }

        Assert.Throws<InvalidOperationException>(() => AudioSourceOptions.ForGame(old));
    }

    [Fact]
    public void Live_game_gets_process_loopback_on_windows_10_with_a_fallback()
    {
        SessionStarted s = Session(SourceKind.LiveGame, Retention.Owned);

        AudioPlan win10 = AudioCapturePolicy.Decide(s, Win10_22H2, targetProcessId: 1234, microphone: true);
        Assert.Equal(AudioSourceKind.ProcessLoopback, win10.Game);
        Assert.True(win10.AllowEndpointFallback);
        Assert.True(win10.Encode);
        Assert.True(win10.Microphone);
        Assert.Contains("system loopback if it fails", win10.Reason, StringComparison.Ordinal);

        AudioPlan old = AudioCapturePolicy.Decide(s, Win10_1909, targetProcessId: 1234, microphone: false);
        Assert.Equal(AudioSourceKind.SystemLoopback, old.Game);
        Assert.False(old.Microphone);
        Assert.Contains("needs Windows build 19041", old.Reason, StringComparison.Ordinal);

        AudioPlan noPid = AudioCapturePolicy.Decide(s, Win10_22H2, targetProcessId: null, microphone: false);
        Assert.Equal(AudioSourceKind.SystemLoopback, noPid.Game);
    }

    [Fact]
    public void Ephemeral_live_game_is_analysis_only()
    {
        AudioPlan plan = AudioCapturePolicy.Decide(Session(SourceKind.LiveGame, Retention.Ephemeral), Win10_22H2, null, microphone: true);
        Assert.Equal(AudioSourceKind.SystemLoopback, plan.Game);
        Assert.False(plan.Encode);
        Assert.False(plan.Microphone);
    }

    [Fact]
    public void Invalid_retention_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => AudioCapturePolicy.Decide(Session(SourceKind.OfficialPlayer, Retention.Owned), Win10_22H2, null, false));
        Assert.Throws<InvalidOperationException>(() => AudioCapturePolicy.Decide(Session(SourceKind.LiveGame, Retention.Unspecified), Win10_22H2, null, false));
    }
}
