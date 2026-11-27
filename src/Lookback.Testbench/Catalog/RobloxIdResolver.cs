using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lookback.Profiles.Model;

namespace Lookback.Testbench.Catalog;

/// <summary>What Roblox's public web API says about one experience profile.</summary>
internal sealed record RobloxIdResult(string Profile, string DisplayName, long? UniverseId, long? RootPlaceId, string? RobloxName, long? Playing, string Source, bool NameMatches, string? Problem);

/// <summary>
/// Curation tool, not part of detection or clipping: fills in and verifies the
/// <c>roblox.universe</c> and <c>roblox.place</c> ids of experience profiles
/// using Roblox's public, unauthenticated web endpoints.
/// <list type="number">
/// <item>A profile that lists a universe id keeps it; one that lists a place id
/// has the place resolved to its universe.</item>
/// <item>A profile without ids: the experience is searched by display name and
/// the top game result is proposed (check the name it reports).</item>
/// <item>Every universe is then fetched once to confirm its name and root place.</item>
/// </list>
/// With <c>write</c>, ids are written back only where the names match, editing
/// only the single-line <c>"ids"</c> entry so comments and layout are kept.
/// </summary>
internal static class RobloxIdResolver
{
    // [^\r\n] and \r? so files checked out with Windows line endings match too; the line ending itself is left as it was.
    private static readonly Regex IdsLine = new("""^(?<indent>[ \t]*)"ids":[ \t]*\{[^\r\n]*\},?[ \t]*(?=\r?$)""", RegexOptions.Multiline, TimeSpan.FromSeconds(1));

    public static async Task<IReadOnlyList<RobloxIdResult>> RunAsync(string profileDirectory, bool write, HttpClient http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var files = Directory.EnumerateFiles(profileDirectory, "roblox.*.json").Order(StringComparer.Ordinal)
            .Select(f => (File: f, Profile: GameProfileLoader.Load(f)))
            .Where(x => x.Profile.Level == ProfileLevelKind.Subgame)
            .ToList();

        var found = new List<(string File, GameProfile Profile, long? Universe, string Source, string? Problem)>();
        foreach ((string file, GameProfile p) in files)
        {
            try
            {
                if (p.Ids.TryGetValue("roblox.universe", out IReadOnlyList<string>? u) && u.Count > 0)
                {
                    found.Add((file, p, long.Parse(u[0], CultureInfo.InvariantCulture), "listed universe", null));
                }
                else if (p.Ids.TryGetValue("roblox.place", out IReadOnlyList<string>? places) && places.Count > 0)
                {
                    using JsonDocument doc = await GetJsonAsync(http, $"https://apis.roblox.com/universes/v1/places/{places[0]}/universe", ct).ConfigureAwait(false);
                    found.Add((file, p, doc.RootElement.GetProperty("universeId").GetInt64(), $"place {places[0]}", null));
                }
                else
                {
                    string url = $"https://apis.roblox.com/search-api/omni-search?searchQuery={Uri.EscapeDataString(p.DisplayName)}&sessionId={Guid.NewGuid()}&pageType=all";
                    using JsonDocument doc = await GetJsonAsync(http, url, ct).ConfigureAwait(false);
                    long? universe = doc.RootElement.GetProperty("searchResults").EnumerateArray()
                        .Where(r => r.TryGetProperty("contentGroupType", out JsonElement t) && t.GetString() == "Game")
                        .SelectMany(r => r.GetProperty("contents").EnumerateArray())
                        .Select(c => (long?)c.GetProperty("universeId").GetInt64())
                        .FirstOrDefault();
                    found.Add((file, p, universe, "search by name", universe is null ? "no game found by that name" : null));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                found.Add((file, p, null, "error", ex.Message));
            }
        }

        // One call per 50 universes confirms names and root places.
        var details = new Dictionary<long, (string Name, long RootPlace, long Playing)>();
        long[] universes = [.. found.Where(f => f.Universe is not null).Select(f => f.Universe!.Value).Distinct()];
        foreach (long[] chunk in universes.Chunk(50))
        {
            using JsonDocument doc = await GetJsonAsync(http, "https://games.roblox.com/v1/games?universeIds=" + string.Join(',', chunk), ct).ConfigureAwait(false);
            foreach (JsonElement g in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                details[g.GetProperty("id").GetInt64()] = (g.GetProperty("name").GetString() ?? "", g.GetProperty("rootPlaceId").GetInt64(), g.TryGetProperty("playing", out JsonElement pl) ? pl.GetInt64() : 0);
            }
        }

        var results = new List<RobloxIdResult>();
        foreach ((string file, GameProfile p, long? universe, string source, string? problem) in found)
        {
            (string Name, long RootPlace, long Playing)? d = universe is not null && details.TryGetValue(universe.Value, out var v) ? v : null;
            bool matches = d is not null && NamesMatch(p.DisplayName, d.Value.Name);
            results.Add(new RobloxIdResult(p.Id, p.DisplayName, universe, d?.RootPlace, d?.Name, d?.Playing, source, matches, problem));
            if (write && d is not null && matches)
            {
                var places = new List<string> { d.Value.RootPlace.ToString(CultureInfo.InvariantCulture) };
                if (p.Ids.TryGetValue("roblox.place", out IReadOnlyList<string>? listed))
                {
                    places.AddRange(listed.Where(x => !places.Contains(x)));
                }

                try
                {
                    string json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(file, ReplaceIdsLine(json, universe!.Value, places), ct).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    results[^1] = results[^1] with { Problem = $"not written: {ex.Message}" };
                }
            }
        }

        return results;
    }

    /// <summary>Loose comparison: case, punctuation, emoji and bracketed tags such as "[UPDATE]" are ignored.</summary>
    internal static bool NamesMatch(string ours, string theirs)
    {
        static string Norm(string s) => new(Regex.Replace(s, @"\[[^\]]*\]|\([^)]*\)", "", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        string a = Norm(ours), b = Norm(theirs);
        return a.Length > 0 && b.Length > 0 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    /// <summary>Replaces the profile's single-line <c>"ids"</c> entry, keeping everything else byte for byte.</summary>
    internal static string ReplaceIdsLine(string json, long universe, IReadOnlyList<string> places)
    {
        string ids = $"\"ids\": {{ \"roblox.universe\": [\"{universe.ToString(CultureInfo.InvariantCulture)}\"], \"roblox.place\": [{string.Join(", ", places.Select(x => $"\"{x}\""))}] }},";
        return IdsLine.IsMatch(json)
            ? IdsLine.Replace(json, m => m.Groups["indent"].Value + ids, 1)
            : throw new InvalidDataException("No single-line \"ids\" entry to replace.");
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await http.GetAsync(new Uri(url), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false))!;
    }
}
