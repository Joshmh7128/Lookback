using Microsoft.ML.OnnxRuntime;

namespace Lookback.Scoring.Model;

/// <summary>
/// The exported scikit-learn pipeline (standardise, then logistic regression)
/// run with ONNX Runtime on the CPU execution provider. The model is tiny, so
/// it runs on one thread and never competes with capture or encode for the
/// GPU. Thread-safe.
/// </summary>
internal sealed class OnnxClipModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly RunOptions _run = new();
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly int _features;
    private readonly int _positiveColumn;

    public OnnxClipModel(string onnxPath, ScorerManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(onnxPath, options);
        try
        {
            if (!_session.InputMetadata.TryGetValue(manifest.Input, out NodeMetadata? input))
            {
                throw new InvalidDataException($"{onnxPath}: no input named '{manifest.Input}'.");
            }

            if (!_session.OutputMetadata.ContainsKey(manifest.Output))
            {
                throw new InvalidDataException($"{onnxPath}: no output named '{manifest.Output}'.");
            }

            int[] dims = input.Dimensions;
            if (dims.Length != 2 || (dims[1] > 0 && dims[1] != manifest.FeatureNames.Count))
            {
                throw new InvalidDataException($"{onnxPath}: input shape [{string.Join(", ", dims)}] does not take {manifest.FeatureNames.Count} features.");
            }
        }
        catch
        {
            _session.Dispose();
            _run.Dispose();
            throw;
        }

        _inputNames = [manifest.Input];
        _outputNames = [manifest.Output];
        _features = manifest.FeatureNames.Count;
        _positiveColumn = manifest.PositiveColumn;
    }

    /// <summary>The model's probability of a highlight for one feature vector (already imputed).</summary>
    public double Predict(float[] features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (features.Length != _features)
        {
            throw new ArgumentException($"Expected {_features} features, got {features.Length}.", nameof(features));
        }

        using OrtValue input = OrtValue.CreateTensorValueFromMemory(features, [1, _features]);
        using IDisposableReadOnlyCollection<OrtValue> outputs = _session.Run(_run, _inputNames, [input], _outputNames);
        ReadOnlySpan<float> probabilities = outputs[0].GetTensorDataAsSpan<float>();
        return probabilities[_positiveColumn];
    }

    public void Dispose()
    {
        _session.Dispose();
        _run.Dispose();
    }
}
