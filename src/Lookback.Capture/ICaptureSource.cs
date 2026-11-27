using Lookback.Capture.Gpu;
using Vortice.Direct3D11;

namespace Lookback.Capture;

/// <summary>
/// Receives a captured frame on the capture thread. The texture is owned by
/// the source and valid only during the call: copy or process it before
/// returning. <paramref name="qpc"/> is the raw QPC time the frame was
/// presented (or composed), on the session clock.
/// </summary>
public delegate void CapturedFrameHandler(ID3D11Texture2D texture, long qpc, TransferFunction transfer);

/// <summary>A live capture backend. Each produces D3D11 textures on the shared <see cref="GpuDevice"/>.</summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>"wgc", "dxgi" or "hook".</summary>
    string Backend { get; }

    /// <summary>Human-readable target, such as the window title or the monitor name.</summary>
    string Target { get; }

    long FramesCaptured { get; }

    /// <summary>Raised when the source stops by itself (window closed, device lost beyond recovery).</summary>
    event EventHandler<string>? Stopped;

    void Start(CapturedFrameHandler onFrame);
}
