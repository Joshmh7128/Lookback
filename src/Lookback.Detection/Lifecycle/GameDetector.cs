using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Detection.Matching;
using Lookback.Detection.Probe;
using Lookback.Profiles.Detection;
using Kind = Lookback.Contracts.V1.GameLifecycle.Types.Kind;

namespace Lookback.Detection.Lifecycle;

/// <summary>
/// Turns a series of <see cref="SystemSnapshot"/>s into confirmed game
/// lifecycle changes. Pure apart from the probe calls it makes for matched
/// processes (path, launch arguments, adapter), so tests drive it with a fake
/// probe and a manual clock.
/// <list type="bullet">
/// <item><b>Started</b> (<see cref="Kind.Detected"/>): a process the matcher
/// accepts has had a capture-sized window for <see cref="DetectionOptions.StartHold"/>.
/// A launcher, a crash reporter without a window, or a splash that closes at
/// once never gets that far. If the game is in the foreground,
/// <see cref="Kind.Focused"/> follows at once; otherwise it starts unfocused.</item>
/// <item><b>Focus</b>: the game's process owns the foreground window, held for
/// <see cref="DetectionOptions.FocusHold"/>, so a quick alt-tab does not flap.</item>
/// <item><b>Window changed</b>: the largest window, its monitor or its mode
/// changed and stayed so for <see cref="DetectionOptions.WindowHold"/>.
/// Title changes alone only refresh <see cref="GameInstance.Hints"/>.</item>
/// <item><b>Stopped</b> (<see cref="Kind.Exited"/>): the process has been gone for
/// <see cref="DetectionOptions.ExitGrace"/>. A new process of the same profile
/// in that time continues the run as a window change.</item>
/// </list>
/// Not thread-safe: one caller (the runner's poll loop).
/// </summary>
public sealed class GameDetector
{
    private readonly ISystemProbe _probe;
    private readonly DetectionOptions _options;
    private readonly Func<string?, string> _engine;
    private readonly long _startHold;
    private readonly long _focusHold;
    private readonly long _windowHold;
    private readonly long _exitGrace;
    private readonly Dictionary<(uint Pid, string Exe), Tracked> _tracked = [];
    private readonly Dictionary<uint, WindowEntry> _windows = [];
    private readonly HashSet<(uint, string)> _seen = [];
    private readonly List<(uint, string)> _remove = [];

    public GameDetector(ISystemProbe probe, GameMatcher matcher, long frequency, DetectionOptions? options = null, Func<string?, string>? engineGuess = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _probe = probe;
        Matcher = matcher;
        _options = options ?? new DetectionOptions();
        IReadOnlyList<string> problems = _options.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(options));
        }

        _engine = engineGuess ?? (path => EngineGuess.FromPath(path));
        _startHold = QpcTime.FromTimeSpan(_options.StartHold, frequency);
        _focusHold = QpcTime.FromTimeSpan(_options.FocusHold, frequency);
        _windowHold = QpcTime.FromTimeSpan(_options.WindowHold, frequency);
        _exitGrace = QpcTime.FromTimeSpan(_options.ExitGrace, frequency);
    }

    /// <summary>Replaced when the user's settings change. Games already announced keep their match.</summary>
    public GameMatcher Matcher { get; set; }

    /// <summary>Games announced and not yet stopped, oldest first.</summary>
    public IReadOnlyList<GameInstance> Games =>
        [.. _tracked.Values.Where(t => t.Game is not null).Select(t => t.Game!).OrderBy(g => g.DetectedAt)];

    /// <summary>Candidate processes not (yet) announced, with why: for the bench tool.</summary>
    public IEnumerable<(uint Pid, string Exe, string Note)> Pending =>
        _tracked.Values.Where(t => t.Game is null).Select(t => (t.Pid, t.ExeName, t.Note));

    /// <summary>
    /// Takes one snapshot taken at <paramref name="now"/> (QPC ticks). Appends
    /// confirmed changes to <paramref name="changes"/> in order, and games whose
    /// window title changed to <paramref name="hintsChanged"/>.
    /// </summary>
    public void Update(SystemSnapshot snapshot, long now, List<LifecycleChange> changes, List<GameInstance>? hintsChanged = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        IndexWindows(snapshot);
        _seen.Clear();
        _remove.Clear();

        foreach (ProcessEntry p in snapshot.Processes)
        {
            if (string.IsNullOrEmpty(p.ExeName) || !Matcher.IsCandidate(p.ExeName))
            {
                continue;
            }

            var key = (p.Pid, p.ExeName.ToUpperInvariant());
            _seen.Add(key);
            if (!_tracked.TryGetValue(key, out Tracked? t))
            {
                ProcessDetails? d = _probe.Details(p.Pid);
                t = new Tracked { Pid = p.Pid, ExeName = p.ExeName, ExePath = d?.ExePath };
                _tracked[key] = t;
            }

            t.GoneSince = -1;
            _windows.TryGetValue(p.Pid, out WindowEntry? w);
            if (t.Game is null)
            {
                Pend(t, w, snapshot, now, changes);
            }
            else
            {
                Run(t, w, snapshot, now, changes, hintsChanged);
            }
        }

        foreach ((var key, Tracked t) in _tracked)
        {
            if (_seen.Contains(key) || t.HandedOff)
            {
                if (t.HandedOff)
                {
                    _remove.Add(key);
                }

                continue;
            }

            if (t.Game is null)
            {
                _remove.Add(key);
                continue;
            }

            if (t.GoneSince < 0)
            {
                t.GoneSince = now;
            }

            if (now - t.GoneSince >= _exitGrace)
            {
                changes.Add(new LifecycleChange(Kind.Exited, t.Game with { Focused = false }, now));
                _remove.Add(key);
            }
        }

        foreach (var key in _remove)
        {
            _tracked.Remove(key);
        }
    }

    private void Pend(Tracked t, WindowEntry? w, SystemSnapshot snapshot, long now, List<LifecycleChange> changes)
    {
        if (w is null || w.Minimized)
        {
            t.WindowSince = -1;
            t.Note = w is null ? "no capture-sized window yet" : "minimized";
            return;
        }

        GameMatch? match = Matcher.Match(t.ExeName, t.ExePath, w.Title, out string note);
        t.Note = note;
        if (match is null)
        {
            t.WindowSince = -1;
            return;
        }

        if (t.WindowSince < 0)
        {
            t.WindowSince = now;
        }

        // A run of the same profile whose process just ended: this one continues it.
        Tracked? predecessor = _tracked.Values.FirstOrDefault(o => !ReferenceEquals(o, t) && !o.HandedOff && o.Game is not null
            && o.GoneSince >= 0 && o.Game.Match.ProfileId == match.ProfileId);
        long hold = predecessor is null ? _startHold : _windowHold;
        if (now - t.WindowSince < hold)
        {
            t.Note = $"{match.ProfileId}: holding for {(predecessor is null ? "start" : "relaunch")}";
            return;
        }

        string launchArgs = match.WantsLaunchArgs ? _probe.CommandLine(t.Pid) ?? "" : "";
        bool focused = snapshot.ForegroundPid == t.Pid;
        var game = new GameInstance
        {
            Id = predecessor?.Game!.Id ?? $"live-{match.GameId}-{t.Pid}-{now:x}",
            Match = match,
            Pid = t.Pid,
            ExeName = t.ExeName,
            ExePath = t.ExePath,
            Engine = _engine(t.ExePath),
            Window = w,
            AdapterLuid = _probe.AdapterLuid(w.Monitor),
            Focused = focused,
            DetectedAt = predecessor?.Game!.DetectedAt ?? now,
            Hints = new DetectionContext { LaunchArgs = launchArgs, WindowTitle = w.Title },
        };
        t.Game = game;
        t.Note = "";

        if (predecessor is not null)
        {
            predecessor.HandedOff = true;
            changes.Add(new LifecycleChange(Kind.WindowChanged, game, now));
            if (focused != predecessor.Game!.Focused)
            {
                changes.Add(new LifecycleChange(focused ? Kind.Focused : Kind.Unfocused, game, now));
            }
        }
        else
        {
            changes.Add(new LifecycleChange(Kind.Detected, game with { Focused = false }, now));
            if (focused)
            {
                changes.Add(new LifecycleChange(Kind.Focused, game, now));
            }
        }
    }

    private void Run(Tracked t, WindowEntry? w, SystemSnapshot snapshot, long now, List<LifecycleChange> changes, List<GameInstance>? hintsChanged)
    {
        GameInstance g = t.Game!;
        if (w is not null && !w.Minimized)
        {
            if (SameTarget(w, g.Window))
            {
                t.PendingWindow = null;
                if (w.Title != g.Window.Title || w.Width != g.Window.Width || w.Height != g.Window.Height)
                {
                    bool retitled = w.Title != g.Window.Title;
                    g = g with { Window = w, Hints = g.Hints with { WindowTitle = w.Title } };
                    if (retitled)
                    {
                        hintsChanged?.Add(g);
                    }
                }
            }
            else if (t.PendingWindow is null || !SameTarget(t.PendingWindow, w))
            {
                t.PendingWindow = w;
                t.PendingWindowSince = now;
            }
            else if (now - t.PendingWindowSince >= _windowHold)
            {
                long luid = w.Monitor == g.Window.Monitor ? g.AdapterLuid : _probe.AdapterLuid(w.Monitor);
                g = g with { Window = w, AdapterLuid = luid, Hints = g.Hints with { WindowTitle = w.Title } };
                t.PendingWindow = null;
                changes.Add(new LifecycleChange(Kind.WindowChanged, g, now));
            }
        }
        else
        {
            t.PendingWindow = null; // minimized or hidden: keep the last target
        }

        bool fg = snapshot.ForegroundPid == t.Pid;
        if (fg == g.Focused)
        {
            t.PendingFocusSince = -1;
        }
        else
        {
            if (t.PendingFocusSince < 0)
            {
                t.PendingFocusSince = now;
            }

            if (now - t.PendingFocusSince >= _focusHold)
            {
                g = g with { Focused = fg };
                t.PendingFocusSince = -1;
                changes.Add(new LifecycleChange(fg ? Kind.Focused : Kind.Unfocused, g, now));
            }
        }

        t.Game = g;
    }

    private static bool SameTarget(WindowEntry a, WindowEntry b) => a.Handle == b.Handle && a.Monitor == b.Monitor && a.Mode == b.Mode;

    /// <summary>Each process's capture window: its largest visible window big enough to be a game's, else a minimized one.</summary>
    private void IndexWindows(SystemSnapshot snapshot)
    {
        _windows.Clear();
        foreach (WindowEntry w in snapshot.Windows)
        {
            bool big = w.Width >= _options.MinWindowWidth && w.Height >= _options.MinWindowHeight;
            if (!big && !w.Minimized)
            {
                continue;
            }

            if (!_windows.TryGetValue(w.Pid, out WindowEntry? best)
                || (best.Minimized && !w.Minimized)
                || (best.Minimized == w.Minimized && w.Area > best.Area))
            {
                _windows[w.Pid] = w;
            }
        }
    }

    private sealed class Tracked
    {
        public required uint Pid { get; init; }

        public required string ExeName { get; init; }

        public string? ExePath { get; init; }

        public GameInstance? Game { get; set; }

        public long WindowSince { get; set; } = -1;

        public WindowEntry? PendingWindow { get; set; }

        public long PendingWindowSince { get; set; }

        public long PendingFocusSince { get; set; } = -1;

        public long GoneSince { get; set; } = -1;

        public bool HandedOff { get; set; }

        public string Note { get; set; } = "";
    }
}
