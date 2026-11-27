using Lookback.Clipper;
using Lookback.Contracts.V1;
using Lookback.Scoring;
using Lookback.Scoring.Features;
using Microsoft.Extensions.Logging;

namespace Lookback.Recorder;

/// <summary>
/// What every clipper in the recorder does to a candidate just before it is
/// published (<see cref="ClipperOptions.Annotate"/>): it attaches the
/// candidate's <c>clip-features/1</c> vector, so a rating of it is stored with
/// everything the learned scorer trains on, and scores it with the learned
/// model when one is loaded (<c>model_score</c>, and the calibrated
/// <c>highlight_probability</c> on the candidate and its QA report). Without a
/// model, or for a game the model was not trained on, the heuristic stands.
/// One per recorder, shared by live recording and footage analysis; thread-safe.
/// </summary>
internal sealed partial class ClipScoring : IDisposable
{
    private readonly LearnedClipScorer _scorer;

    private ClipScoring(LearnedClipScorer scorer) => _scorer = scorer;

    /// <summary>
    /// Loads the model in <paramref name="modelDirectory"/> (<c>model.json</c> and
    /// <c>model.onnx</c>, as <c>ml/</c> writes them). With none there, or one that
    /// does not load, candidates still get their features and keep heuristic scores.
    /// </summary>
    public static ClipScoring Load(string? modelDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        LearnedClipScorer scorer = LearnedClipScorer.LoadOrHeuristic(modelDirectory, m => LogNotLoaded(logger, m));
        if (scorer.HasModel)
        {
            string games = string.Join(", ", scorer.Manifest!.Games); // once, at start
            LogLoaded(logger, modelDirectory!, games);
        }
        else
        {
            LogHeuristic(logger, modelDirectory ?? "(none)");
        }

        return new ClipScoring(scorer);
    }

    /// <summary>Features on every candidate, heuristic scores (no model).</summary>
    public static ClipScoring HeuristicOnly() => new(LearnedClipScorer.HeuristicOnly());

    public bool HasModel => _scorer.HasModel;

    /// <summary>Clipper options that annotate each candidate as above, for a session at <paramref name="qpcFrequency"/>.</summary>
    public ClipperOptions Options(long qpcFrequency) => new()
    {
        Annotate = (candidate, qa, features) => Annotate(candidate, qa, features, qpcFrequency),
    };

    /// <summary>Attaches the features and scores the candidate. Takes the clipper's features by name, so tests can call it.</summary>
    public void Annotate(ClipCandidate candidate, QaReport qa, IReadOnlyDictionary<string, float> clipperFeatures, long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(qa);
        ArgumentNullException.ThrowIfNull(clipperFeatures);
        ClipFeatureSchema.Attach(candidate, ClipperFeatureMap.FromClipper(clipperFeatures));
        _scorer.Score(candidate, qpcFrequency);
        LearnedClipScorer.Annotate(qa, candidate);
    }

    public void Dispose() => _scorer.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Learned clip scorer loaded from {Directory} (games: {Games}); candidates get a model score")]
    private static partial void LogLoaded(ILogger logger, string directory, string games);

    [LoggerMessage(Level = LogLevel.Information, Message = "No learned clip scorer in {Directory}: candidates keep heuristic scores (their features are still attached for training)")]
    private static partial void LogHeuristic(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void LogNotLoaded(ILogger logger, string message);
}
