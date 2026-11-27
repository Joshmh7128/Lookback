using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lookback.Scoring.Model;

/// <summary>
/// Platt calibration: p = σ(a · logit(p_model) + b). With a = 1 and b = 0 it
/// leaves the model's probability unchanged.
/// </summary>
/// <param name="A">Slope on the model's logit.</param>
/// <param name="B">Offset.</param>
/// <param name="N">Ratings the parameters were fitted on (before shrinkage toward the parent).</param>
public sealed record PlattCalibration(double A, double B, int N = 0)
{
    /// <summary>Probabilities are clamped this far from 0 and 1 before the logit.</summary>
    public const double Epsilon = 1e-6;

    public static PlattCalibration Identity { get; } = new(1, 0);

    public double Apply(double probability)
    {
        double p = Math.Clamp(probability, Epsilon, 1 - Epsilon);
        double z = Math.Log(p / (1 - p));
        return 1 / (1 + Math.Exp(-((A * z) + B)));
    }
}

/// <summary>The calibration block of <c>model.json</c>.</summary>
public sealed record CalibrationTable
{
    public PlattCalibration Global { get; init; } = PlattCalibration.Identity;

    /// <summary>Per game and per deeper profile (<c>overwatch</c>, <c>overwatch.push</c>), each already shrunk toward its parent.</summary>
    public IReadOnlyDictionary<string, PlattCalibration> Profiles { get; init; } = new Dictionary<string, PlattCalibration>();
}

/// <summary>
/// <c>model.json</c>, written next to <c>model.onnx</c> by <c>ml/lookback_ml/train.py</c>:
/// what the model reads, how to impute, and how to calibrate per game.
/// </summary>
public sealed record ScorerManifest
{
    public const string CurrentFormat = "lookback.scorer/1";

    public required string Format { get; init; }

    public required string FeatureSchema { get; init; }

    public required IReadOnlyList<string> FeatureNames { get; init; }

    /// <summary>Training means, used in place of missing (NaN) features.</summary>
    public required IReadOnlyList<double> FeatureMeans { get; init; }

    /// <summary>Name of the ONNX input (float tensor [N, features]).</summary>
    public string Input { get; init; } = "features";

    /// <summary>Name of the ONNX output holding class probabilities [N, 2].</summary>
    public string Output { get; init; } = "probabilities";

    /// <summary>Column of <see cref="Output"/> that is the probability of a highlight.</summary>
    public int PositiveColumn { get; init; } = 1;

    /// <summary>Games the model was trained on. Other games keep the heuristic score.</summary>
    public required IReadOnlyList<string> Games { get; init; }

    public CalibrationTable Calibration { get; init; } = new();

    /// <summary>Free-form training details (rows, regularisation, metrics) for people; not read by the scorer.</summary>
    public JsonElement? Training { get; init; }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ScorerManifest Load(string path)
    {
        ScorerManifest m;
        try
        {
            m = JsonSerializer.Deserialize<ScorerManifest>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{path}: empty model manifest.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }

        if (m.Format != CurrentFormat)
        {
            throw new InvalidDataException($"{path}: format '{m.Format}' is not {CurrentFormat}.");
        }

        if (m.FeatureNames.Count == 0 || m.FeatureMeans.Count != m.FeatureNames.Count)
        {
            throw new InvalidDataException($"{path}: needs one mean per feature ({m.FeatureNames.Count} names, {m.FeatureMeans.Count} means).");
        }

        if (m.PositiveColumn is < 0 or > 1)
        {
            throw new InvalidDataException($"{path}: positiveColumn must be 0 or 1.");
        }

        return m;
    }

    /// <summary>
    /// The calibration for <paramref name="profileId"/>: the deepest profile
    /// listed (<c>a.b.c</c>, then <c>a.b</c>, then <c>a</c>). Null when the
    /// model was not trained on the profile's game.
    /// </summary>
    public PlattCalibration? CalibrationFor(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        string? game = Ratings.RatingStore.GameOf(profileId);
        if (game is null || !Games.Contains(game, StringComparer.Ordinal))
        {
            return null;
        }

        string id = profileId;
        while (true)
        {
            if (Calibration.Profiles.TryGetValue(id, out PlattCalibration? c))
            {
                return c;
            }

            int dot = id.LastIndexOf('.');
            if (dot < 0)
            {
                return Calibration.Global;
            }

            id = id[..dot];
        }
    }
}
