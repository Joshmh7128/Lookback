using System.Text.RegularExpressions;
using Lookback.Contracts.V1;
using Lookback.Profiles.Model;

namespace Lookback.Detection.Matching;

/// <summary>A process recognised as a game.</summary>
public sealed record GameMatch
{
    /// <summary>The profile to run: a game, or whatever profile the user opted the executable into.</summary>
    public required string ProfileId { get; init; }

    /// <summary>The game at the root of <see cref="ProfileId"/>'s tree.</summary>
    public required string GameId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary><see cref="DetectionMethod.Executable"/> when a profile lists it; <see cref="DetectionMethod.Manual"/> when the user opted it in.</summary>
    public required DetectionMethod Method { get; init; }

    /// <summary>What the recorder may do to capture this game. Never more permissive than the profile's.</summary>
    public required CapturePolicy Policy { get; init; }

    /// <summary>Why it matched, in words.</summary>
    public required string Reason { get; init; }

    /// <summary>1 for an executable only this game uses or one the user opted in; lower when a shared executable matched by window title.</summary>
    public float Confidence { get; init; } = 1;

    /// <summary>The profile tree reads hints from the launch arguments, so they are worth asking the OS for.</summary>
    public bool WantsLaunchArgs { get; init; }
}

/// <summary>
/// Decides whether a process is a game, and which profile it gets, from its
/// executable name and path and its window title. In order:
/// <list type="number">
/// <item>A denied executable is never a game.</item>
/// <item>An executable the user opted in gets the profile they chose
/// (<c>generic</c> by default), with external capture only.</item>
/// <item>An executable exactly one game profile lists is that game. A
/// shared executable (<c>javaw.exe</c>) also needs its title to match the
/// profile's <c>executableTitles</c>.</item>
/// <item>Anything else, including an executable two profiles both claim, is
/// not a game until the user opts it in.</item>
/// </list>
/// Thread-safe once built.
/// </summary>
public sealed class GameMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private readonly ProfileCatalog _catalog;
    private readonly Dictionary<string, List<(GameProfile Game, Regex? Title)>> _byExe = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _wantsLaunchArgs = new(StringComparer.Ordinal);

    public GameMatcher(ProfileCatalog catalog, DetectionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        Settings = settings ?? new DetectionSettings();
        foreach (GameProfile g in catalog.All.Where(p => p.Level == ProfileLevelKind.Game).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            foreach (string exe in g.Executables)
            {
                if (!_byExe.TryGetValue(exe, out var list))
                {
                    list = [];
                    _byExe[exe] = list;
                }

                string? title = g.ExecutableTitles.FirstOrDefault(kv => string.Equals(kv.Key, exe, StringComparison.OrdinalIgnoreCase)).Value;
                list.Add((g, title is null ? null : new Regex(title, RegexOptions.CultureInvariant, RegexTimeout)));
            }

            if (g.IdHints.Any(h => h.Source == HintSource.LaunchArgs)
                || catalog.Subtree(g.Id).Any(p => p.Detect.Hints.Any(h => h.Source == HintSource.LaunchArgs)))
            {
                _wantsLaunchArgs.Add(g.Id);
            }
        }

        foreach (AllowEntry a in Settings.Allow.Where(a => !catalog.Contains(a.Profile)))
        {
            Warnings.Add($"allow: '{a.Exe}' names profile '{a.Profile}', which does not exist; it is ignored.");
        }
    }

    public DetectionSettings Settings { get; }

    public ProfileCatalog Catalog => _catalog;

    /// <summary>Problems found in the settings, in words.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// True when a process with this executable name could be a game, so its
    /// path and window are worth looking up. Cheap; called for every process.
    /// </summary>
    public bool IsCandidate(string exeName) => _byExe.ContainsKey(exeName) || Settings.MentionsName(exeName);

    /// <summary>True when the executable is a game only if its window title says so; matching waits for a titled window.</summary>
    public bool NeedsTitle(string exeName) =>
        _byExe.TryGetValue(exeName, out var list) && list.Exists(x => x.Title is not null);

    /// <summary>The game this process is, or null. <paramref name="note"/> says why not, when it is a near miss.</summary>
    public GameMatch? Match(string exeName, string? exePath, string? windowTitle, out string note)
    {
        ArgumentException.ThrowIfNullOrEmpty(exeName);
        note = "";
        if (Settings.IsDenied(exeName, exePath))
        {
            note = "denied in settings";
            return null;
        }

        AllowEntry? allowed = Settings.FindAllowed(exeName, exePath);
        if (allowed is not null && _catalog.Contains(allowed.Profile))
        {
            GameProfile resolved = _catalog.Resolve(allowed.Profile);
            string gameId = _catalog.PathTo(allowed.Profile)[0].Id;
            return new GameMatch
            {
                ProfileId = allowed.Profile,
                GameId = gameId,
                DisplayName = resolved.DisplayName,
                Method = DetectionMethod.Manual,
                // An opted-in executable is the user's word, not a verified title: external capture only.
                Policy = new CapturePolicy { AllowPresentHook = false, AllowMemoryReading = false, Notes = resolved.CapturePolicy.Notes },
                Reason = $"opted in by the user as {allowed.Profile}",
                WantsLaunchArgs = _wantsLaunchArgs.Contains(gameId),
            };
        }

        if (!_byExe.TryGetValue(exeName, out var listed))
        {
            note = "no profile lists it; opt it in to record it with the generic profile";
            return null;
        }

        var hits = new List<(GameProfile Game, string Reason, float Confidence)>();
        foreach ((GameProfile g, Regex? title) in listed)
        {
            if (title is null)
            {
                hits.Add((g, $"executable {exeName} is listed by {g.Id}", 1));
            }
            else if (windowTitle is not null && SafeMatch(title, windowTitle))
            {
                hits.Add((g, $"executable {exeName} with window title matching /{title}/ is listed by {g.Id}", 0.8f));
            }
        }

        if (hits.Count == 0)
        {
            note = windowTitle is null
                ? $"{exeName} is shared; waiting for a window title"
                : $"{exeName} is shared and the window title \"{windowTitle}\" matches no profile";
            return null;
        }

        if (hits.Count > 1)
        {
            note = $"ambiguous: {string.Join(", ", hits.Select(h => h.Game.Id))} all list {exeName}; opt it in with the profile to use";
            return null;
        }

        GameProfile game = hits[0].Game;
        return new GameMatch
        {
            ProfileId = game.Id,
            GameId = game.Id,
            DisplayName = game.DisplayName,
            Method = DetectionMethod.Executable,
            Policy = _catalog.Resolve(game.Id).CapturePolicy,
            Reason = hits[0].Reason,
            Confidence = hits[0].Confidence,
            WantsLaunchArgs = _wantsLaunchArgs.Contains(game.Id),
        };
    }

    private static bool SafeMatch(Regex re, string input)
    {
        try
        {
            return re.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
