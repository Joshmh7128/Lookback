using System.Reflection;

namespace Lookback.Profiles.Model;

/// <summary>
/// All profiles as a tree: games at the roots, sub-games (such as Roblox
/// experiences) beneath them, and game modes beneath either. A child inherits
/// everything from its parent and adds to it:
/// <list type="bullet">
/// <item>Regions are added. A child may not move a parent's region; it names a new one.</item>
/// <item>Events and composites are added, or replaced by name. <see cref="GameProfile.Disable"/> removes inherited ones.</item>
/// <item>The capture policy can only get stricter: a child can never allow what its game forbids.</item>
/// </list>
/// </summary>
public sealed class ProfileCatalog
{
    private readonly Dictionary<string, GameProfile> _byId;
    private readonly Dictionary<string, List<GameProfile>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GameProfile> _resolved = new(StringComparer.Ordinal);

    public ProfileCatalog(IEnumerable<GameProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _byId = new Dictionary<string, GameProfile>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (GameProfile p in profiles)
        {
            if (!_byId.TryAdd(p.Id, p))
            {
                problems.Add($"'{p.Id}' is defined twice.");
            }
        }

        foreach (GameProfile p in _byId.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(p.Parent))
            {
                continue;
            }

            if (!_byId.TryGetValue(p.Parent, out GameProfile? parent))
            {
                problems.Add($"'{p.Id}': parent '{p.Parent}' does not exist.");
                continue;
            }

            if (p.Level == ProfileLevelKind.Subgame && parent.Level != ProfileLevelKind.Game)
            {
                problems.Add($"'{p.Id}': a subgame's parent must be a game.");
            }

            if (p.Level == ProfileLevelKind.Mode && parent.Level == ProfileLevelKind.Mode)
            {
                problems.Add($"'{p.Id}': a mode's parent must be a game or a subgame.");
            }

            if (!_children.TryGetValue(parent.Id, out List<GameProfile>? list))
            {
                list = [];
                _children[parent.Id] = list;
            }

            list.Add(p);
        }

        if (problems.Count == 0)
        {
            foreach (GameProfile p in _byId.Values)
            {
                problems.AddRange(CheckResolved(Resolve(p.Id)).Select(x => $"'{p.Id}': {x}"));
            }

            // One RegionSignal measures a whole game's tree, so a region name means one rectangle across it.
            foreach (GameProfile root in _byId.Values.Where(p => string.IsNullOrEmpty(p.Parent)))
            {
                foreach (var clash in Subtree(root.Id).SelectMany(p => p.Regions).GroupBy(r => r.Name, StringComparer.Ordinal)
                    .Where(g => g.Select(r => (r.X, r.Y, r.W, r.H)).Distinct().Count() > 1))
                {
                    problems.Add($"'{root.Id}': region '{clash.Key}' has different rectangles in different profiles; give each its own name.");
                }
            }
        }

        // An id names one profile.
        foreach (var clash in _byId.Values.SelectMany(p => p.Ids.SelectMany(kv => kv.Value.Select(v => (Key: (kv.Key, v), p.Id))))
            .GroupBy(x => x.Key).Where(g => g.Count() > 1))
        {
            problems.Add($"{clash.Key.Item1} {clash.Key.Item2} is listed by {string.Join(" and ", clash.Select(x => x.Id))}.");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("Profile catalog is invalid:\n  " + string.Join("\n  ", problems));
        }
    }

    /// <summary>Every profile compiled into the assembly.</summary>
    public static ProfileCatalog BuiltIn { get; } = new(GameProfileLoader.BuiltInIds.Select(GameProfileLoader.Load));

    /// <summary>The built-in profiles plus extra profile files (which may replace built-ins of the same id).</summary>
    public static ProfileCatalog WithFiles(IEnumerable<string> paths)
    {
        var all = GameProfileLoader.BuiltInIds.ToDictionary(id => id, GameProfileLoader.Load, StringComparer.Ordinal);
        foreach (string path in paths)
        {
            GameProfile p = GameProfileLoader.Load(path);
            all[p.Id] = p;
        }

        return new ProfileCatalog(all.Values);
    }

    public IEnumerable<GameProfile> All => _byId.Values;

    public bool Contains(string id) => _byId.ContainsKey(id);

    public GameProfile Get(string id) =>
        _byId.TryGetValue(id, out GameProfile? p) ? p : throw new KeyNotFoundException($"No profile '{id}'.");

    public IReadOnlyList<GameProfile> Children(string id) =>
        _children.TryGetValue(id, out List<GameProfile>? list) ? list : [];

    /// <summary>Root first: <c>roblox</c>, <c>roblox.arsenal</c>.</summary>
    public IReadOnlyList<GameProfile> PathTo(string id)
    {
        var path = new List<GameProfile>();
        for (GameProfile? p = Get(id); p is not null; p = string.IsNullOrEmpty(p.Parent) ? null : Get(p.Parent))
        {
            path.Add(p);
        }

        path.Reverse();
        return path;
    }

    /// <summary>The profile and everything beneath it, parents before children.</summary>
    public IReadOnlyList<GameProfile> Subtree(string id)
    {
        var list = new List<GameProfile>();
        var stack = new Stack<GameProfile>();
        stack.Push(Get(id));
        while (stack.Count > 0)
        {
            GameProfile p = stack.Pop();
            list.Add(p);
            foreach (GameProfile c in Children(p.Id).Reverse())
            {
                stack.Push(c);
            }
        }

        return list;
    }

    /// <summary>The game whose executable this is, if any (case-insensitive).</summary>
    public GameProfile? FindByExecutable(string exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        string name = exe[(exe.LastIndexOfAny(['\\', '/']) + 1)..]; // Windows paths, even when this runs elsewhere
        return _byId.Values.FirstOrDefault(p => p.Level == ProfileLevelKind.Game
            && p.Executables.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The profile that lists this platform id, such as a Roblox universe id.</summary>
    public GameProfile? FindById(string kind, string value) =>
        _byId.Values.FirstOrDefault(p => p.Ids.TryGetValue(kind, out IReadOnlyList<string>? v) && v.Contains(value, StringComparer.Ordinal));

    /// <summary>Profiles carrying a tag, in id order.</summary>
    public IEnumerable<GameProfile> Tagged(string tag) =>
        _byId.Values.Where(p => p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).OrderBy(p => p.Id, StringComparer.Ordinal);

    /// <summary>The profile with everything inherited from its ancestors applied.</summary>
    public GameProfile Resolve(string id)
    {
        if (_resolved.TryGetValue(id, out GameProfile? cached))
        {
            return cached;
        }

        GameProfile own = Get(id);
        if (string.IsNullOrEmpty(own.Parent))
        {
            _resolved[id] = own;
            return own;
        }

        GameProfile parent = Resolve(own.Parent);
        var disabled = own.Disable.ToHashSet(StringComparer.Ordinal);
        var ownEvents = own.Events.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var ownComposites = own.Composites.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        GameProfile resolved = own with
        {
            Executables = parent.Executables,
            CapturePolicy = new CapturePolicy
            {
                AllowPresentHook = parent.CapturePolicy.AllowPresentHook && own.CapturePolicy.AllowPresentHook,
                AllowMemoryReading = parent.CapturePolicy.AllowMemoryReading && own.CapturePolicy.AllowMemoryReading,
                Notes = string.IsNullOrEmpty(own.CapturePolicy.Notes) ? parent.CapturePolicy.Notes : own.CapturePolicy.Notes,
            },
            Regions = [.. parent.Regions, .. own.Regions],
            Events = [.. parent.Events.Where(e => !disabled.Contains(e.Name) && !ownEvents.Contains(e.Name)), .. own.Events],
            Composites = [.. parent.Composites.Where(c => !disabled.Contains(c.Name) && !ownComposites.Contains(c.Name)), .. own.Composites],
        };

        // A composite whose source event was disabled goes too.
        var names = resolved.Events.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        resolved = resolved with { Composites = [.. resolved.Composites.Where(c => names.Contains(c.Of))] };
        _resolved[id] = resolved;
        return resolved;
    }

    /// <summary>Every region used anywhere in the tree under <paramref name="rootId"/>, once each.</summary>
    public IReadOnlyList<HudRegion> SubtreeRegions(string rootId) =>
        [.. Subtree(rootId).SelectMany(p => p.Regions).DistinctBy(r => r.Name, StringComparer.Ordinal)];

    private static IEnumerable<string> CheckResolved(GameProfile r)
    {
        if (r.Regions.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != r.Regions.Count)
        {
            yield return "a region name repeats one inherited from a parent; name new regions differently.";
        }

        var names = r.Events.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (CompositeRule c in r.Composites.Where(c => !names.Contains(c.Of)))
        {
            yield return $"composite '{c.Name}' refers to unknown event '{c.Of}'.";
        }
    }
}
