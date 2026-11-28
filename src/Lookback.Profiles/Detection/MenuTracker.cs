using Lookback.Contracts.V1;
using Lookback.Profiles.Model;
using Lookback.Signals.Video;

namespace Lookback.Profiles.Detection;

/// <summary>
/// Menu states across a whole game tree: one <see cref="MenuStateDetector"/>
/// per profile that has menu states (each over its resolved, inherited
/// <see cref="GameProfile.Menus"/>), stepped for whichever profile is active.
/// Every state name in the tree is known up front, so the values published
/// for viewers (<see cref="ValueNames"/>) never change shape. A tree with no
/// menu states makes a tracker that reports play on every frame. Allocation-free per frame.
/// </summary>
public sealed class MenuTracker
{
    private readonly Dictionary<string, (MenuStateDetector Detector, int[] ToUnion)> _byProfile = new(StringComparer.Ordinal);
    private readonly ColorGridReader _reader = new();
    private readonly float[] _grid = new float[ColorGridSignal.Length];
    private readonly string[] _union;
    private MenuStateDetector? _active;
    private int[] _activeMap = [];
    private string? _activeId;

    public MenuTracker(ProfileCatalog catalog, string rootId, long frequency)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var union = new List<string>();
        foreach (GameProfile own in catalog.Subtree(rootId))
        {
            MenuSpec menus = catalog.Resolve(own.Id).Menus;
            if (menus.States.Count == 0)
            {
                continue;
            }

            var detector = new MenuStateDetector(menus, frequency);
            int[] map = new int[menus.States.Count];
            for (int i = 0; i < map.Length; i++)
            {
                string name = menus.States[i].Name;
                int u = union.IndexOf(name);
                if (u < 0)
                {
                    u = union.Count;
                    union.Add(name);
                }

                map[i] = u;
            }

            _byProfile[own.Id] = (detector, map);
            MaxHysteresisMs = Math.Max(MaxHysteresisMs, Math.Max(menus.EnterMs ?? MenuSpec.DefaultEnterMs, menus.ExitMs ?? MenuSpec.DefaultExitMs));
        }

        _union = [.. union];
        ValueNames = [.. _union.SelectMany(n => new[] { $"menu.{n}", $"menu.{n}.margin" }), .. _union.Length > 0 ? new[] { "menu.any" } : []];
    }

    /// <summary>The longest <c>enterMs</c> or <c>exitMs</c> in the tree: how long after a screen changes the held state can still follow.</summary>
    public int MaxHysteresisMs { get; }

    /// <summary>True when some profile in the tree has menu states.</summary>
    public bool HasMenus => _union.Length > 0;

    /// <summary>Every state name in the tree, in tree order.</summary>
    public IReadOnlyList<string> StateNames => _union;

    /// <summary>
    /// Names of <see cref="WriteValues"/>: per state <c>menu.&lt;state&gt;</c> (1 while held, else 0)
    /// and <c>menu.&lt;state&gt;.margin</c> (its <c>maxDistance</c> minus the frame's distance:
    /// positive when the screen matches; -1 when the active profile has no such state), then
    /// <c>menu.any</c> (1 while any menu state is held). Empty when the tree has no menus.
    /// </summary>
    public IReadOnlyList<string> ValueNames { get; }

    /// <summary>The active profile's detector, or null when that profile has no menu states.</summary>
    public MenuStateDetector? Active => _active;

    /// <summary>The state held now, or <c>play</c>.</summary>
    public string HeldName => _active?.HeldName ?? "play";

    /// <summary>True while a menu state is held (after hysteresis).</summary>
    public bool InMenu => _active is { Held: >= 0 };

    /// <summary>True when the last frame itself matched a menu state (before hysteresis).</summary>
    public bool RawInMenu => _active is { Raw: >= 0 };

    /// <summary>True when the held state changed on the last frame (including a change of profile out of a menu).</summary>
    public bool Changed { get; private set; }

    public void OnSchema(SignalSchema schema)
    {
        if (HasMenus)
        {
            _reader.OnSchema(schema);
        }
    }

    /// <summary>
    /// One video frame while <paramref name="activeProfileId"/> is active.
    /// Returns false when the frame carries no colour grid (or the tree has no menus).
    /// </summary>
    public bool OnVideoFrame(long t, SignalFrame frame, string activeProfileId)
    {
        Changed = false;
        if (!HasMenus)
        {
            return false;
        }

        if (!string.Equals(activeProfileId, _activeId, StringComparison.Ordinal))
        {
            bool wasInMenu = InMenu;
            _activeId = activeProfileId;
            if (_byProfile.TryGetValue(activeProfileId, out var entry))
            {
                _active = entry.Detector;
                _activeMap = entry.ToUnion;
                _active.Reset();
            }
            else
            {
                _active = null;
                _activeMap = [];
            }

            Changed = wasInMenu;
        }

        if (_active is null || !_reader.TryRead(frame, _grid))
        {
            return false;
        }

        Changed |= _active.Step(t, _grid);
        return true;
    }

    /// <summary>Writes the values named by <see cref="ValueNames"/>.</summary>
    public void WriteValues(Span<float> output)
    {
        if (!HasMenus)
        {
            return;
        }

        for (int u = 0; u < _union.Length; u++)
        {
            output[2 * u] = 0;
            output[(2 * u) + 1] = -1;
        }

        if (_active is not null)
        {
            ReadOnlySpan<float> d = _active.Distances;
            for (int i = 0; i < _activeMap.Length; i++)
            {
                int u = _activeMap[i];
                output[2 * u] = _active.Held == i ? 1 : 0;
                output[(2 * u) + 1] = float.IsFinite(d[i]) ? _active.MaxDistance(i) - d[i] : -1;
            }
        }

        output[2 * _union.Length] = InMenu ? 1 : 0;
    }
}
