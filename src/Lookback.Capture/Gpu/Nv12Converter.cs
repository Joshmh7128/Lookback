using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Lookback.Capture.Gpu;

/// <summary>
/// Converts captured BGRA textures to NV12 at the encode resolution with the
/// D3D11 video processor (fixed-function scaling and BT.709 limited-range
/// conversion on the GPU), letterboxed to keep the aspect ratio. Holds the
/// latest converted frame so a constant-rate encode clock can repeat it when
/// the capture source delivers nothing (WGC only sends frames on change).
/// Call under <see cref="GpuDevice.Lock"/>.
/// </summary>
public sealed class Nv12Converter : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly Dictionary<nint, ID3D11VideoProcessorInputView> _inputViews = [];
    private readonly ID3D11VideoProcessorOutputView _outputView;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private uint _inW, _inH;

    public Nv12Converter(GpuDevice gpu, int width, int height, int fps)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("NV12 needs even dimensions.");
        }

        _gpu = gpu;
        Width = width;
        Height = height;
        Fps = fps;
        _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();
        Latest = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        EnsureProcessor((uint)width, (uint)height);
        _outputView = _videoDevice.CreateVideoProcessorOutputView(Latest, _enumerator!, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
    }

    public int Width { get; }

    public int Height { get; }

    public int Fps { get; }

    /// <summary>The most recent converted frame. Copy it before handing it to the encoder.</summary>
    public ID3D11Texture2D Latest { get; }

    public bool HasFrame { get; private set; }

    public void Convert(ID3D11Texture2D source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Texture2DDescription d = source.Description;
        if (d.Width != _inW || d.Height != _inH)
        {
            foreach (ID3D11VideoProcessorInputView v in _inputViews.Values)
            {
                v.Dispose();
            }

            _inputViews.Clear();
            EnsureProcessor(d.Width, d.Height);
        }

        if (!_inputViews.TryGetValue(source.NativePointer, out ID3D11VideoProcessorInputView? input))
        {
            if (_inputViews.Count >= 8)
            {
                foreach (ID3D11VideoProcessorInputView v in _inputViews.Values)
                {
                    v.Dispose();
                }

                _inputViews.Clear();
            }

            input = _videoDevice.CreateVideoProcessorInputView(source, _enumerator!, new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
            _inputViews[source.NativePointer] = input;
        }

        // Fit the source into the output, keeping its aspect ratio.
        float s = Math.Min((float)Width / d.Width, (float)Height / d.Height);
        int w = Math.Min(Width, (int)MathF.Round(d.Width * s / 2) * 2), h = Math.Min(Height, (int)MathF.Round(d.Height * s / 2) * 2);
        int x = (Width - w) / 2, y = (Height - h) / 2;
        _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, new Vortice.RawRect(x, y, x + w, y + h));
        _videoContext.VideoProcessorBlt(_processor!, _outputView, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = input }]).CheckError();
        HasFrame = true;
    }

    private void EnsureProcessor(uint inW, uint inH)
    {
        _processor?.Dispose();
        _enumerator?.Dispose();
        _inW = inW;
        _inH = inH;
        var desc = new VideoProcessorContentDescription
        {
            InputFrameRate = new Rational((uint)Fps, 1),
            InputWidth = inW,
            InputHeight = inH,
            OutputFrameRate = new Rational((uint)Fps, 1),
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(desc);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 }); // full-range RGB in
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 }); // BT.709, 16-235
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, true, new VideoColor { YCbCr = new VideoColorYCbCrA { Y = 16f / 255f, Cb = 0.5f, Cr = 0.5f, A = 1f } });
    }

    public void Dispose()
    {
        foreach (ID3D11VideoProcessorInputView v in _inputViews.Values)
        {
            v.Dispose();
        }

        _outputView.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        Latest.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
