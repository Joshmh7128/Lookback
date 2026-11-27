using Lookback.Contracts.V1;

namespace Lookback.Audio.Tests;

/// <summary>Which audio a session may capture. Platform-neutral.</summary>
public class PolicyTests
{
    private const int Win10 = 19045;
    private const int Server2022 = 20348;

    private static SessionStarted Session(SourceKind kind, Retention retention) => new() { SourceKind = kind, Retention = retention };

    [Fact]
    public void Official_player_never_gets_system_loopback_encoder_or_mic()
    {
        SessionStarted s = Session(SourceKind.OfficialPlayer, Retention.Ephemeral);

        AudioPlan old = AudioCapturePolicy.Decide(s, Win10, targetProcessId: 1234, microphone: true);
        Assert.Null(old.Game);
        Assert.False(old.Encode);
        Assert.False(old.Microphone);
        Assert.Contains("20348", old.Reason, StringComparison.Ordinal);

        AudioPlan noPid = AudioCapturePolicy.Decide(s, Server2022, targetProcessId: null, microphone: false);
        Assert.Null(noPid.Game);

        AudioPlan isolated = AudioCapturePolicy.Decide(s, Server2022, targetProcessId: 1234, microphone: true);
        Assert.Equal(AudioSourceKind.ProcessLoopback, isolated.Game);
        Assert.False(isolated.AllowEndpointFallback);
        Assert.False(isolated.Encode);
        Assert.False(isolated.Microphone);

        Assert.Throws<InvalidOperationException>(() => AudioSourceOptions.ForGame(old));
        Assert.False(AudioSourceOptions.ForGame(isolated, 1234).AllowEndpointFallback);
    }

    [Fact]
    public void Live_game_falls_back_to_system_loopback_before_20348_and_says_so()
    {
        SessionStarted s = Session(SourceKind.LiveGame, Retention.Owned);

        AudioPlan old = AudioCapturePolicy.Decide(s, Win10, targetProcessId: 1234, microphone: true);
        Assert.Equal(AudioSourceKind.SystemLoopback, old.Game);
        Assert.True(old.Encode);
        Assert.True(old.Microphone);
        Assert.Contains("needs Windows build 20348", old.Reason, StringComparison.Ordinal);

        AudioPlan isolated = AudioCapturePolicy.Decide(s, Server2022, targetProcessId: 1234, microphone: false);
        Assert.Equal(AudioSourceKind.ProcessLoopback, isolated.Game);
        Assert.True(isolated.AllowEndpointFallback);
        Assert.False(isolated.Microphone);
    }

    [Fact]
    public void Ephemeral_live_game_is_analysis_only()
    {
        AudioPlan plan = AudioCapturePolicy.Decide(Session(SourceKind.LiveGame, Retention.Ephemeral), Win10, null, microphone: true);
        Assert.Equal(AudioSourceKind.SystemLoopback, plan.Game);
        Assert.False(plan.Encode);
        Assert.False(plan.Microphone);
    }

    [Fact]
    public void Invalid_retention_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => AudioCapturePolicy.Decide(Session(SourceKind.OfficialPlayer, Retention.Owned), Win10, null, false));
        Assert.Throws<InvalidOperationException>(() => AudioCapturePolicy.Decide(Session(SourceKind.LiveGame, Retention.Unspecified), Win10, null, false));
    }
}
