using System.Globalization;
using Lookback.Signals.Video;

namespace Lookback.Profiles.Model;

/// <summary>
/// The screens of a game that are not play, as data. Each state is the
/// average colour of every cell of the game-agnostic colour grid
/// (<see cref="ColorGridSignal"/>, 6 × 4 cells) while that screen is shown,
/// plus how close a frame must be to count as that screen. A detector
/// (<see cref="Detection.MenuStateDetector"/>) reports the closest matching
/// state with hysteresis, or play when none matches. A profile with no states
/// changes nothing.
/// </summary>
public sealed record MenuSpec
{
    public const int DefaultEnterMs = 400;
    public const int DefaultExitMs = 400;

    public IReadOnlyList<MenuState> States { get; init; } = [];

    /// <summary>A screen must match this long before the state changes to it. Inherited when unset.</summary>
    public int? EnterMs { get; init; }

    /// <summary>Play must hold this long (no state matching) before a menu state ends. Inherited when unset.</summary>
    public int? ExitMs { get; init; }

    /// <summary>This spec over <paramref name="parent"/>: own states replace the parent's of the same name; unset numbers inherit.</summary>
    public MenuSpec Over(MenuSpec parent, IReadOnlySet<string>? disabled = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var own = States.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        return new MenuSpec
        {
            States = [.. parent.States.Where(s => !own.Contains(s.Name) && disabled?.Contains(s.Name) != true), .. States],
            EnterMs = EnterMs ?? parent.EnterMs,
            ExitMs = ExitMs ?? parent.ExitMs,
        };
    }
}

/// <summary>One screen that is not play, such as <c>pause</c>, <c>main_menu</c>, <c>loading</c> or <c>post_game</c>.</summary>
public sealed record MenuState
{
    public const float DefaultMaxDistance = 0.08f;

    /// <summary>Lower-case letters, digits and underscores; <c>play</c> is reserved.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// One <c>#rrggbb</c> per grid cell, row-major from the top left (24:
    /// 6 columns × 4 rows), or null for a cell that is not compared (one that
    /// shows the game behind the menu, an animated hero, a timer).
    /// </summary>
    public required IReadOnlyList<string?> Colors { get; init; }

    /// <summary>
    /// A frame matches when the mean absolute difference of its compared cells
    /// from <see cref="Colors"/>, over red, green and blue in 0..1, is at most this.
    /// </summary>
    public float MaxDistance { get; init; } = DefaultMaxDistance;

    public string Notes { get; init; } = "";
}

/// <summary>Turns a <see cref="MenuState"/>'s colours into numbers and back, and measures frames against them.</summary>
public static class MenuSignature
{
    /// <summary>Reads <paramref name="colors"/> into one value per grid channel (as <see cref="ColorGridSignal.Names"/>) and a per-cell mask.</summary>
    public static bool TryParse(IReadOnlyList<string?> colors, float[] values, bool[] compared, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(compared);
        problem = null;
        if (colors.Count != ColorGridSignal.Cells)
        {
            problem = $"colors needs {ColorGridSignal.Cells} entries ({ColorGridSignal.Columns} columns × {ColorGridSignal.Rows} rows, row-major), has {colors.Count}.";
            return false;
        }

        int used = 0;
        for (int c = 0; c < ColorGridSignal.Cells; c++)
        {
            string? hex = colors[c];
            if (hex is null)
            {
                compared[c] = false;
                continue;
            }

            if (hex.Length != 7 || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
            {
                problem = $"colors[{c}] '{hex}' is not #rrggbb or null.";
                return false;
            }

            compared[c] = true;
            used++;
            int s = c * ColorGridSignal.Channels;
            values[s] = ((rgb >> 16) & 0xff) / 255f;
            values[s + 1] = ((rgb >> 8) & 0xff) / 255f;
            values[s + 2] = (rgb & 0xff) / 255f;
        }

        if (used == 0)
        {
            problem = "colors compares no cell; give at least one #rrggbb.";
            return false;
        }

        return true;
    }

    /// <summary>The colour of every cell of <paramref name="grid"/> (as <see cref="ColorGridSignal.Names"/>), or null where <paramref name="compared"/> is false.</summary>
    public static IReadOnlyList<string?> Format(ReadOnlySpan<float> grid, ReadOnlySpan<bool> compared)
    {
        if (grid.Length != ColorGridSignal.Length || compared.Length != ColorGridSignal.Cells)
        {
            throw new ArgumentException("Need one value per grid channel and one flag per cell.");
        }

        var colors = new string?[ColorGridSignal.Cells];
        for (int c = 0; c < colors.Length; c++)
        {
            int s = c * ColorGridSignal.Channels;
            colors[c] = compared[c]
                ? string.Create(CultureInfo.InvariantCulture, $"#{Byte(grid[s]):x2}{Byte(grid[s + 1]):x2}{Byte(grid[s + 2]):x2}")
                : null;
        }

        return colors;

        static int Byte(float v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    /// <summary>Mean absolute difference over the compared cells' channels, in 0..1.</summary>
    public static float Distance(ReadOnlySpan<float> grid, ReadOnlySpan<float> reference, ReadOnlySpan<bool> compared)
    {
        float sum = 0;
        int n = 0;
        for (int c = 0; c < compared.Length; c++)
        {
            if (!compared[c])
            {
                continue;
            }

            int s = c * ColorGridSignal.Channels;
            sum += Math.Abs(grid[s] - reference[s]) + Math.Abs(grid[s + 1] - reference[s + 1]) + Math.Abs(grid[s + 2] - reference[s + 2]);
            n += ColorGridSignal.Channels;
        }

        return n == 0 ? float.PositiveInfinity : sum / n;
    }
}
