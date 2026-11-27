using Lookback.Contracts.V1;
using Lookback.Scoring.Features;
using Lookback.Scoring.Model;
using Microsoft.ML.OnnxRuntime;

namespace Lookback.Scoring;

/// <summary>Which score a candidate ended up with.</summary>
public enum ScoreSource
{
    /// <summary>No model applies: the clipper's heuristic stands.</summary>
    Heuristic = 0,

    /// <summary>The learned model scored it and the game's calibration set the probability.</summary>
    Model = 1,
}

/// <summary>The result of <see cref="LearnedClipScorer.Score"/>.</summary>
/// <param name="Source">Model or heuristic.</param>
/// <param name="Reason">Why the heuristic was kept, when it was.</param>
public readonly record struct ScoreOutcome(ScoreSource Source, string? Reason);

/// <summary>
/// Sets <see cref="ClipCandidate.ModelScore"/> (the model's raw probability)
/// and <see cref="ClipCandidate.HighlightProbability"/> (that probability
/// after the game's Platt calibration) from a model trained on your ratings.
/// When no model covers the candidate's game, or the candidate has no usable
/// features, the heuristic stands: <c>model_score</c> stays unset and the
/// clipper's highlight probability is left as it was. A broken or missing
/// model never stops clipping. Thread-safe.
/// </summary>
public sealed class LearnedClipScorer : IDisposable
{
    public const string ModelFileName = "model.onnx";
    public const string ManifestFileName = "model.json";

    private readonly OnnxClipModel? _model;

    private LearnedClipScorer(ScorerManifest? manifest, OnnxClipModel? model)
    {
        Manifest = manifest;
        _model = model;
    }

    /// <summary>A scorer with no model: every candidate keeps its heuristic score.</summary>
    public static LearnedClipScorer HeuristicOnly() => new(null, null);

    /// <summary>The loaded model's manifest; null for <see cref="HeuristicOnly"/>.</summary>
    public ScorerManifest? Manifest { get; }

    public bool HasModel => _model is not null;

    /// <summary>Loads <c>model.json</c> and <c>model.onnx</c> from <paramref name="directory"/>. Throws when they are missing or do not match.</summary>
    public static LearnedClipScorer Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string manifestPath = Path.Combine(directory, ManifestFileName);
        string onnxPath = Path.Combine(directory, ModelFileName);
        if (!File.Exists(manifestPath) || !File.Exists(onnxPath))
        {
            throw new FileNotFoundException($"{directory} needs {ManifestFileName} and {ModelFileName}.");
        }

        ScorerManifest manifest = ScorerManifest.Load(manifestPath);
        if (manifest.FeatureSchema == ClipFeatureSchema.Id && !manifest.FeatureNames.SequenceEqual(ClipFeatureSchema.Names, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{manifestPath}: feature names do not match {ClipFeatureSchema.Id}.");
        }

        try
        {
            return new LearnedClipScorer(manifest, new OnnxClipModel(onnxPath, manifest));
        }
        catch (OnnxRuntimeException ex)
        {
            throw new InvalidDataException($"{onnxPath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Loads the model in <paramref name="directory"/> when there is one, and
    /// otherwise returns <see cref="HeuristicOnly"/>. A model that fails to
    /// load is reported through <paramref name="warn"/> and skipped.
    /// </summary>
    public static LearnedClipScorer LoadOrHeuristic(string? directory, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, ManifestFileName)))
        {
            return HeuristicOnly();
        }

        try
        {
            return Load(directory);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"Learned clip scorer not loaded, keeping heuristic scores: {ex.Message}");
            return HeuristicOnly();
        }
    }

    /// <summary>
    /// Scores <paramref name="candidate"/> in place. Uses its
    /// <see cref="ClipCandidate.Features"/> when they match the model's
    /// schema; otherwise, given <paramref name="qpcFrequency"/>, the features
    /// any candidate carries on its own (see <see cref="ClipFeatureSchema.FromCandidate"/>).
    /// </summary>
    public ScoreOutcome Score(ClipCandidate candidate, long qpcFrequency = 0)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (_model is null || Manifest is null)
        {
            return Keep(candidate, "no learned model");
        }

        PlattCalibration? calibration = Manifest.CalibrationFor(candidate.GameProfile);
        if (calibration is null)
        {
            return Keep(candidate, $"the model was not trained on '{candidate.GameProfile}'");
        }

        float[]? features = FeaturesFor(candidate, qpcFrequency);
        if (features is null)
        {
            return Keep(candidate, $"no {Manifest.FeatureSchema} features");
        }

        double raw;
        try
        {
            raw = _model.Predict(features);
        }
        catch (OnnxRuntimeException ex)
        {
            return Keep(candidate, $"inference failed: {ex.Message}");
        }

        if (!double.IsFinite(raw))
        {
            return Keep(candidate, "the model returned a non-finite score");
        }

        candidate.ModelScore = (float)raw;
        candidate.HighlightProbability = (float)calibration.Apply(raw);
        return new ScoreOutcome(ScoreSource.Model, null);
    }

    /// <summary>Copies the scored candidate's highlight probability onto its QA report.</summary>
    public static void Annotate(QaReport report, ClipCandidate scored)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(scored);
        report.HighlightProbability = scored.HighlightProbability;
    }

    /// <summary>
    /// The model's raw and calibrated probability for a feature vector, for
    /// tools and tests. Missing values are imputed as in <see cref="Score"/>.
    /// Null when no model or no calibration for <paramref name="profileId"/>.
    /// </summary>
    public (double Raw, double Calibrated)? Predict(ReadOnlySpan<float> features, string profileId)
    {
        if (_model is null || Manifest is null || features.Length != Manifest.FeatureNames.Count)
        {
            return null;
        }

        PlattCalibration? calibration = Manifest.CalibrationFor(profileId);
        if (calibration is null)
        {
            return null;
        }

        double raw = _model.Predict(Impute(features));
        return (raw, calibration.Apply(raw));
    }

    private float[]? FeaturesFor(ClipCandidate candidate, long qpcFrequency)
    {
        int n = Manifest!.FeatureNames.Count;
        if (candidate.FeatureSchema == Manifest.FeatureSchema && candidate.Features.Count == n)
        {
            float[] v = new float[n];
            candidate.Features.CopyTo(v, 0);
            return Impute(v);
        }

        if (candidate.Features.Count == 0 && qpcFrequency > 0 && Manifest.FeatureSchema == ClipFeatureSchema.Id)
        {
            return Impute(ClipFeatureSchema.FromCandidate(candidate, qpcFrequency));
        }

        return null;
    }

    private float[] Impute(ReadOnlySpan<float> features)
    {
        float[] v = features.ToArray();
        for (int i = 0; i < v.Length; i++)
        {
            if (!float.IsFinite(v[i]))
            {
                v[i] = (float)Manifest!.FeatureMeans[i];
            }
        }

        return v;
    }

    private static ScoreOutcome Keep(ClipCandidate candidate, string reason)
    {
        candidate.ClearModelScore();
        return new ScoreOutcome(ScoreSource.Heuristic, reason);
    }

    public void Dispose() => _model?.Dispose();
}
