using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <para>
/// <see cref="Reload"/> swaps in the model now in the folder (after training
/// promotes a new one, or a rollback), for every clipper at once, from its
/// next candidate. A replaced model is kept until the recorder stops, so a
/// clipper scoring with it at that moment is never left holding a disposed one.
/// </para>
/// </summary>
internal sealed partial class ClipScoring : IDisposable
{
    private readonly string? _directory;
    private readonly ILogger? _logger;
    private readonly Lock _lock = new();
    private readonly List<LearnedClipScorer> _retired = [];
    private volatile LearnedClipScorer _scorer;

    private ClipScoring(LearnedClipScorer scorer, string? directory, ILogger? logger)
    {
        _scorer = scorer;
        _directory = directory;
        _logger = logger;
    }

    /// <summary>
    /// Loads the model in <paramref name="modelDirectory"/> (<c>model.json</c> and
    /// <c>model.onnx</c>, as <c>ml/</c> writes them). With none there, or one that
    /// does not load, candidates still get their features and keep heuristic scores.
    /// </summary>
    public static ClipScoring Load(string? modelDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var scoring = new ClipScoring(Open(modelDirectory, logger), modelDirectory, logger);
        return scoring;
    }

    /// <summary>Features on every candidate, heuristic scores (no model).</summary>
    public static ClipScoring HeuristicOnly() => new(LearnedClipScorer.HeuristicOnly(), null, null);

    public bool HasModel => _scorer.HasModel;

    /// <summary>
    /// Loads the model now in the model folder and uses it from the next
    /// candidate on (no model there: heuristic scores). Returns what is in use,
    /// as <see cref="Describe"/>.
    /// </summary>
    public JsonObject Reload()
    {
        LearnedClipScorer next = _logger is null ? LearnedClipScorer.HeuristicOnly() : Open(_directory, _logger);
        lock (_lock)
        {
            _retired.Add(_scorer);
            _scorer = next;
        }

        return Describe();
    }

    /// <summary>The model in use: whether there is one, the games it covers and when it was trained.</summary>
    public JsonObject Describe()
    {
        LearnedClipScorer s = _scorer;
        string? trainedAt = null;
        if (s.Manifest?.Training is { ValueKind: JsonValueKind.Object } t && t.TryGetProperty("trainedAt", out JsonElement at) && at.ValueKind == JsonValueKind.String)
        {
            trainedAt = at.GetString();
        }

        return new JsonObject
        {
            ["model"] = s.HasModel,
            ["games"] = new JsonArray([.. (s.Manifest?.Games ?? []).Select(g => (JsonNode)g)]),
            ["trainedAt"] = trainedAt,
            ["directory"] = _directory,
        };
    }

    /// <summary>Clipper options that annotate each candidate as above, for a session at <paramref name="qpcFrequency"/>.</summary>
    public ClipperOptions Options(long qpcFrequency) => new()
    {
        Annotate = (candidate, qa, features) => Annotate(candidate, qa, features, qpcFrequency),
        // With a model, what ratings taught picks a long candidate's highlights (the heuristic otherwise).
        RankHighlight = (candidate, features) => Rank(candidate, features, qpcFrequency),
    };

    /// <summary>
    /// The model's calibrated probability for a highlight window (scored on a
    /// copy, so the window keeps its own fields), or its heuristic score when
    /// there is no model or it does not cover the window's game.
    /// </summary>
    public double Rank(ClipCandidate candidate, IReadOnlyDictionary<string, float> clipperFeatures, long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        LearnedClipScorer scorer = _scorer;
        if (!scorer.HasModel)
        {
            return candidate.HeuristicScore;
        }

        ClipCandidate probe = candidate.Clone();
        ClipFeatureSchema.Attach(probe, ClipperFeatureMap.FromClipper(clipperFeatures));
        return scorer.Score(probe, qpcFrequency).Source == ScoreSource.Model ? probe.HighlightProbability : candidate.HeuristicScore;
    }

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

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (LearnedClipScorer s in _retired)
            {
                s.Dispose();
            }

            _retired.Clear();
            _scorer.Dispose();
        }
    }

    private static LearnedClipScorer Open(string? modelDirectory, ILogger logger)
    {
        LearnedClipScorer scorer = LearnedClipScorer.LoadOrHeuristic(modelDirectory, m => LogNotLoaded(logger, m));
        if (scorer.HasModel)
        {
            string games = string.Join(", ", scorer.Manifest!.Games);
            LogLoaded(logger, modelDirectory!, games);
        }
        else
        {
            LogHeuristic(logger, modelDirectory ?? "(none)");
        }

        return scorer;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Learned clip scorer loaded from {Directory} (games: {Games}); candidates get a model score")]
    private static partial void LogLoaded(ILogger logger, string directory, string games);

    [LoggerMessage(Level = LogLevel.Information, Message = "No learned clip scorer in {Directory}: candidates keep heuristic scores (their features are still attached for training)")]
    private static partial void LogHeuristic(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void LogNotLoaded(ILogger logger, string message);
}
