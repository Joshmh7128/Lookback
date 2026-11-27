namespace Lookback.Capture.Tests;

/// <summary>
/// Tests that drive real GPU encoders run one at a time: creating a DXGI
/// factory (as <c>CaptureTarget.Outputs</c> does) while NVENC is encoding
/// stalls it briefly, so a parallel class enumerating outputs made the
/// constant-rate tests miss ticks.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuEncoderCollection
{
    public const string Name = "GPU encoders";
}
