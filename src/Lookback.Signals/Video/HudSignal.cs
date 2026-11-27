using Lookback.Media.Frames;

namespace Lookback.Signals.Video;

/// <summary>
/// Finds a game's HUD without knowing the game, then watches it.
/// <para>
/// During warm-up it tracks per-pixel luma variance (Welford's algorithm).
/// Gameplay pixels change constantly; HUD pixels (health, ammo, minimap
/// frame, kill feed area, score) stay put. Pixels whose variance stays low,
/// and which are not near-black letterbox, become the HUD mask. After
/// warm-up, <c>hud.activity</c> is the mean absolute change inside the mask:
/// a health bar dropping, a counter ticking or a kill-feed line appearing.
/// Those changes are among the strongest game-agnostic highlight cues.
/// </para>
/// </summary>
public sealed class HudSignal : IVideoSignal
{
    public const float StaticStdDev = 6f;  // luma units; below this a pixel counts as static
    public const byte LetterboxLuma = 16;  // static near-black pixels are bars, not HUD

    private readonly int _warmupFrames;
    private readonly int _pixels;
    private readonly float[] _mean;
    private readonly float[] _m2;
    private readonly bool[] _mask;
    private readonly byte[] _previous;
    private int _seen;
    private int _maskCount;

    /// <param name="pixelCount">Pixels per analysis frame.</param>
    /// <param name="warmupFrames">Frames observed before the mask is fixed (for example 10 s).</param>
    public HudSignal(int pixelCount, int warmupFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(warmupFrames, 2);
        _pixels = pixelCount;
        _warmupFrames = warmupFrames;
        _mean = new float[pixelCount];
        _m2 = new float[pixelCount];
        _mask = new bool[pixelCount];
        _previous = new byte[pixelCount];
    }

    public IReadOnlyList<string> Names { get; } = ["hud.ready", "hud.coverage", "hud.activity"];

    /// <summary>True once warm-up is over and the mask is fixed.</summary>
    public bool IsReady => _seen >= _warmupFrames;

    /// <summary>The learned mask (true = HUD pixel). All false until ready.</summary>
    public ReadOnlySpan<bool> Mask => _mask;

    public void Process(VideoFrame frame, LumaPlane luma, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(luma);
        ReadOnlySpan<byte> y = luma.Values;

        if (!IsReady)
        {
            _seen++;
            for (int i = 0; i < _pixels; i++)
            {
                float v = y[i];
                float delta = v - _mean[i];
                _mean[i] += delta / _seen;
                _m2[i] += delta * (v - _mean[i]);
            }

            if (IsReady)
            {
                BuildMask();
            }

            y.CopyTo(_previous);
            output[0] = IsReady ? 1 : 0;
            output[1] = (float)_maskCount / _pixels;
            output[2] = 0;
            return;
        }

        long change = 0;
        for (int i = 0; i < _pixels; i++)
        {
            if (_mask[i])
            {
                change += Math.Abs(y[i] - _previous[i]);
            }
        }

        y.CopyTo(_previous);
        output[0] = 1;
        output[1] = (float)_maskCount / _pixels;
        output[2] = _maskCount == 0 ? 0 : (float)(change / (255.0 * _maskCount));
    }

    private void BuildMask()
    {
        float maxVariance = StaticStdDev * StaticStdDev;
        _maskCount = 0;
        for (int i = 0; i < _pixels; i++)
        {
            float variance = _m2[i] / (_seen - 1);
            bool hud = variance < maxVariance && _mean[i] > LetterboxLuma;
            _mask[i] = hud;
            if (hud)
            {
                _maskCount++;
            }
        }
    }

    public void Reset()
    {
        _seen = 0;
        _maskCount = 0;
        Array.Clear(_mean);
        Array.Clear(_m2);
        Array.Clear(_mask);
    }
}
