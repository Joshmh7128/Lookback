using Lookback.Capture.Gpu;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Lookback.Capture.Tests;

/// <summary>The analysis downscale on WARP, so it runs on CI without a GPU. Windows only.</summary>
public class DownscalerTests
{
    private static ID3D11Texture2D Halves(GpuDevice gpu, int w, int h)
    {
        // Left half red, right half blue, in BGRA byte order.
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                bool left = x < (w / 2) + 2; // off the 5:1 grid, so one output pixel straddles the seam
                px[i] = left ? (byte)0 : (byte)255;     // B
                px[i + 1] = 0;                           // G
                px[i + 2] = left ? (byte)255 : (byte)0; // R
                px[i + 3] = 255;
            }
        }

        return gpu.Device.CreateTexture2D(px.AsSpan(), Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource, ResourceOptionFlags.None, ResourceUsage.Default, CpuAccessFlags.None);
    }

    private static byte[] Run(int w, int h)
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var down = new GpuDownscaler(gpu);
        using ID3D11Texture2D src = Halves(gpu, w, h);
        byte[]? got = null;
        int fw = down.Analysis.Width, fh = down.Analysis.Height;
        FrameReady take = (span, pitch, qpc) =>
        {
            Assert.Equal(1234, qpc);
            got = new byte[fw * fh * 4];
            for (int y = 0; y < fh; y++)
            {
                span.Slice(y * pitch, fw * 4).CopyTo(got.AsSpan(y * fw * 4));
            }
        };
        down.Process(src, 1234, TransferFunction.Sdr, take);
        down.Flush(take);
        return got!;
    }

    private static (byte B, byte G, byte R) At(byte[] f, int x, int y) { int i = ((y * 384) + x) * 4; return (f[i], f[i + 1], f[i + 2]); }

    [Fact]
    public void Sixteen_by_nine_fills_the_frame_in_bgra_order()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "D3D11 WARP is Windows only");
        byte[] f = Run(1920, 1080);
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(f, 10, 100));   // red
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(f, 370, 100));  // blue
        (byte b, _, byte r) = At(f, 192, 100);                            // the seam averages both
        Assert.InRange(r, 60, 200);
        Assert.InRange(b, 60, 200);
    }

    [Fact]
    public void Four_by_three_is_pillarboxed_like_file_source()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "D3D11 WARP is Windows only");
        byte[] f = Run(1440, 1080); // content 288x216, centred: bars of 48 px
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(f, 10, 100));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(f, 380, 100));
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(f, 60, 100));
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(f, 320, 100));
    }
}
