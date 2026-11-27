using Lookback.Contracts.V1;
using Lookback.Detection.Matching;
using Lookback.Profiles.Model;

namespace Lookback.Detection.Tests;

public sealed class MatcherTests
{
    private static readonly ProfileCatalog Builtin = ProfileCatalog.BuiltIn;

    private static ProfileCatalog Ambiguous() => new(
    [
        GameProfileLoader.Load("generic"),
        new GameProfile { Id = "alpha", DisplayName = "Alpha", Executables = ["Shared.exe", "Alpha.exe"] },
        new GameProfile { Id = "beta", DisplayName = "Beta", Executables = ["shared.exe"], CapturePolicy = new CapturePolicy { AllowPresentHook = true, AllowMemoryReading = true } },
    ]);

    [Fact]
    public void A_listed_executable_is_its_game_ignoring_case()
    {
        var m = new GameMatcher(Builtin);
        GameMatch? hit = m.Match("overwatch.EXE", null, "Overwatch", out _);
        Assert.NotNull(hit);
        Assert.Equal("overwatch", hit.ProfileId);
        Assert.Equal("overwatch", hit.GameId);
        Assert.Equal(DetectionMethod.Executable, hit.Method);
        Assert.Equal(1f, hit.Confidence);
    }

    [Fact]
    public void Anti_cheat_policy_is_surfaced_unchanged()
    {
        var m = new GameMatcher(Builtin);
        foreach (string exe in new[] { "Overwatch.exe", "VALORANT-Win64-Shipping.exe", "RobloxPlayerBeta.exe", "cs2.exe" })
        {
            GameMatch hit = m.Match(exe, null, "x", out _)!;
            Assert.False(hit.Policy.AllowPresentHook, exe);
            Assert.False(hit.Policy.AllowMemoryReading, exe);
        }
    }

    [Fact]
    public void A_profile_that_allows_more_keeps_its_policy_only_when_matched_by_its_own_executable()
    {
        var m = new GameMatcher(Ambiguous(), new DetectionSettings().WithAllowed("shared.exe", "beta"));
        GameMatch hit = m.Match("shared.exe", null, null, out _)!;
        Assert.Equal("beta", hit.ProfileId);
        Assert.Equal(DetectionMethod.Manual, hit.Method);
        Assert.False(hit.Policy.AllowPresentHook); // an opt-in is the user's word, not a verified title
        Assert.False(hit.Policy.AllowMemoryReading);
    }

    [Fact]
    public void An_unknown_executable_is_not_a_game_until_opted_in_and_then_gets_generic()
    {
        var m = new GameMatcher(Builtin);
        Assert.Null(m.Match("MyIndieGame.exe", null, "My Indie Game", out string note));
        Assert.Contains("opt it in", note, StringComparison.Ordinal);
        Assert.False(m.IsCandidate("MyIndieGame.exe"));

        var opted = new GameMatcher(Builtin, new DetectionSettings().WithAllowed("myindiegame.exe"));
        Assert.True(opted.IsCandidate("MyIndieGame.exe"));
        GameMatch hit = opted.Match("MyIndieGame.exe", null, "My Indie Game", out _)!;
        Assert.Equal("generic", hit.ProfileId);
        Assert.Equal(DetectionMethod.Manual, hit.Method);
        Assert.False(hit.Policy.AllowPresentHook);
        Assert.False(hit.Policy.AllowMemoryReading);
    }

    [Fact]
    public void An_executable_two_profiles_list_is_ambiguous_until_opted_in()
    {
        var m = new GameMatcher(Ambiguous());
        Assert.True(m.IsCandidate("SHARED.exe"));
        Assert.Null(m.Match("Shared.exe", null, "Shared", out string note));
        Assert.Contains("ambiguous", note, StringComparison.Ordinal);
        Assert.Equal("alpha", m.Match("Alpha.exe", null, null, out _)!.ProfileId);

        var opted = new GameMatcher(Ambiguous(), new DetectionSettings().WithAllowed("Shared.exe", "alpha"));
        Assert.Equal("alpha", opted.Match("shared.exe", null, null, out _)!.ProfileId);
    }

    [Fact]
    public void Deny_wins_over_a_profile_and_over_allow()
    {
        var denied = new GameMatcher(Builtin, new DetectionSettings().WithDenied("Overwatch.exe"));
        Assert.Null(denied.Match("Overwatch.exe", null, "Overwatch", out string note));
        Assert.Contains("denied", note, StringComparison.Ordinal);

        DetectionSettings s = new DetectionSettings().WithAllowed("x.exe").WithDenied("x.exe");
        Assert.Empty(s.Allow);
        Assert.Null(new GameMatcher(Builtin, s).Match("x.exe", null, null, out _));
    }

    [Fact]
    public void A_path_entry_covers_only_that_copy()
    {
        string here = Path.Combine("games", "mine", "Thing.exe");
        string there = Path.Combine("downloads", "Thing.exe");
        var m = new GameMatcher(Builtin, new DetectionSettings().WithAllowed(here));
        Assert.True(m.IsCandidate("Thing.exe"));
        Assert.NotNull(m.Match("Thing.exe", here, null, out _));
        Assert.Null(m.Match("Thing.exe", there, null, out _));
        Assert.Null(m.Match("Thing.exe", null, null, out _));
    }

    [Fact]
    public void A_shared_runtime_needs_the_window_title()
    {
        var m = new GameMatcher(Builtin);
        Assert.True(m.NeedsTitle("javaw.exe"));
        Assert.Null(m.Match("javaw.exe", null, null, out string waiting));
        Assert.Contains("waiting for a window title", waiting, StringComparison.Ordinal);
        Assert.Null(m.Match("javaw.exe", null, "Burp Suite Community Edition", out _));

        GameMatch hit = m.Match("javaw.exe", null, "Minecraft* 1.21.4 - Multiplayer (3rd-party Server)", out _)!;
        Assert.Equal("minecraft", hit.ProfileId);
        Assert.True(hit.Confidence < 1);
        Assert.Equal("minecraft", m.Match("Minecraft.Windows.exe", null, "Minecraft", out _)!.ProfileId);
    }

    [Fact]
    public void Launch_args_are_asked_for_only_where_a_profile_reads_them()
    {
        var m = new GameMatcher(Builtin);
        Assert.True(m.Match("RobloxPlayerBeta.exe", null, "Roblox", out _)!.WantsLaunchArgs);
        Assert.False(m.Match("Overwatch.exe", null, "Overwatch", out _)!.WantsLaunchArgs);
    }

    [Fact]
    public void An_allow_entry_naming_a_missing_profile_is_reported_and_ignored()
    {
        var m = new GameMatcher(Builtin, new DetectionSettings().WithAllowed("x.exe", "no-such-game"));
        Assert.Single(m.Warnings);
        Assert.Null(m.Match("x.exe", null, null, out _));
    }

    [Fact]
    public void Profiles_validate_executable_titles()
    {
        Assert.Contains(GameProfileLoader.Validate(new GameProfile { Id = "g", DisplayName = "G", Executables = ["a.exe"], ExecutableTitles = new Dictionary<string, string> { ["b.exe"] = "x" } }),
            p => p.Contains("not one of the executables", StringComparison.Ordinal));
        Assert.Contains(GameProfileLoader.Validate(new GameProfile { Id = "g", DisplayName = "G", Executables = ["a.exe"], ExecutableTitles = new Dictionary<string, string> { ["a.exe"] = "(" } }),
            p => p.Contains("not a valid regular expression", StringComparison.Ordinal));
        Assert.Empty(GameProfileLoader.Validate(GameProfileLoader.Load("minecraft")));
    }

    [Fact]
    public void Settings_round_trip_and_missing_file_is_empty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lookback-detect-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "detection.json");
        try
        {
            Assert.Empty(DetectionSettings.Load(path).Allow);
            DetectionSettings s = new DetectionSettings().WithAllowed("A.exe").WithAllowed("B.exe", "minecraft").WithDenied("C.exe");
            s.Save(path);
            DetectionSettings back = DetectionSettings.Load(path);
            Assert.Equal(s.Allow, back.Allow);
            Assert.Equal(s.Deny, back.Deny);

            File.WriteAllText(path, """{ "allow": [ { "exe": "a.exe", "profle": "generic" } ] }""");
            Assert.Throws<InvalidDataException>(() => DetectionSettings.Load(path));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Engine_is_guessed_from_files_beside_the_executable()
    {
        string unity = Path.Combine("g", "PEAK", "PEAK.exe");
        Assert.Equal("unity", EngineGuess.FromPath(unity, f => f.EndsWith("UnityPlayer.dll", StringComparison.Ordinal), _ => false));
        Assert.Equal("unity", EngineGuess.FromPath(unity, _ => false, d => d.EndsWith("PEAK_Data", StringComparison.Ordinal)));
        Assert.Equal("unreal", EngineGuess.FromPath(@"C:\g\Marvel\Binaries\Win64\Marvel-Win64-Shipping.exe", _ => false, _ => false));
        Assert.Equal("", EngineGuess.FromPath(Path.Combine("g", "x.exe"), _ => false, _ => false));
        Assert.Equal("", EngineGuess.FromPath(null));
    }
}
