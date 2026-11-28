using System.Runtime.InteropServices;
using Lookback.Media.Frames;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using SharpGen.Runtime;
using DxgiFormat = Vortice.DXGI.Format;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Lookback.Capture.Gpu;

/// <summary>How a captured texture's values are encoded.</summary>
public enum TransferFunction
{
    /// <summary>SDR, gamma-encoded as stored (BGRA8, RGBA8, SDR 10-bit).</summary>
    Sdr = 0,

    /// <summary>scRGB linear FP16 (1.0 = 80 nits), as WGC and DXGI give for HDR desktops.</summary>
    ScRgb = 1,

    /// <summary>HDR10: BT.2020 primaries, PQ, in R10G10B10A2.</summary>
    Pq = 2,
}

/// <summary>
/// The analysis tap: a compute shader shrinks each captured texture to the
/// analysis format on the GPU, and a ring of staging textures reads the small
/// result back a frame or two later without stalling the GPU. At the default
/// 384x216 only about 330 KB per frame crosses to the CPU; the size follows
/// <see cref="AnalysisFormat"/>. Not thread-safe: call from the
/// capture thread, under the device lock (it takes it itself).
/// </summary>
public sealed class GpuDownscaler : IDisposable
{
    private const int StagingDepth = 3;

    private readonly GpuDevice _gpu;
    private readonly AnalysisFormat _format;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _params;
    private readonly ID3D11Texture2D _output;
    private readonly ID3D11UnorderedAccessView _outputView;
    private readonly ID3D11Texture2D[] _staging = new ID3D11Texture2D[StagingDepth];
    private readonly long[] _stagingQpc = new long[StagingDepth];
    private readonly bool[] _pending = new bool[StagingDepth];
    private int _next;
    private long _busyTicks;
    private long _readbackTicks;
    private ID3D11ShaderResourceView? _srcView;
    private ID3D11Texture2D? _srcViewTexture;

    public GpuDownscaler(GpuDevice gpu, AnalysisFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;
        _format = format ?? AnalysisFormat.Default;
        ID3D11Device d = gpu.Device;

        ReadOnlyMemory<byte> bytecode = Compiler.Compile(ShaderSource.Value, "main", "Downscale.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        _shader = d.CreateComputeShader(bytecode.Span);
        _sampler = d.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        _params = d.CreateBuffer(new BufferDescription(64, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        _output = d.CreateTexture2D(new Texture2DDescription(DxgiFormat.R8G8B8A8_UNorm, (uint)_format.Width, (uint)_format.Height, 1, 1, BindFlags.UnorderedAccess));
        _outputView = d.CreateUnorderedAccessView(_output, null);
        for (int i = 0; i < StagingDepth; i++)
        {
            _staging[i] = d.CreateTexture2D(new Texture2DDescription(DxgiFormat.R8G8B8A8_UNorm, (uint)_format.Width, (uint)_format.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        }
    }

    public AnalysisFormat Analysis => _format;

    /// <summary>Frames whose readback completed.</summary>
    public long Completed { get; private set; }

    /// <summary>Capture-thread time in <see cref="Process"/> and <see cref="Flush"/>: dispatch, readback waits, the copy out and the delivery callback.</summary>
    public TimeSpan Busy => System.Diagnostics.Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _busyTicks));

    /// <summary>The part of <see cref="Busy"/> spent mapping staging textures and in the delivery callback (the CPU side of the readback).</summary>
    public TimeSpan ReadbackBusy => System.Diagnostics.Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _readbackTicks));

    /// <summary>
    /// Times a slot was still pending when the ring came round, so it was mapped with a blocking wait.
    /// Usually the GPU finished long before; any real wait shows up in <see cref="ReadbackBusy"/>.
    /// </summary>
    public long ReadbackWaits { get; private set; }

    /// <summary>
    /// Queues a downscale of <paramref name="source"/> (captured at
    /// <paramref name="qpc"/>) and delivers any earlier frames whose readback
    /// has finished to <paramref name="deliver"/> as BGRA rows with a pitch.
    /// The span is valid only during the callback.
    /// </summary>
    public void Process(ID3D11Texture2D source, long qpc, TransferFunction transfer, FrameReady deliver, float sdrWhiteNits = 200f)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(deliver);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using (_gpu.Enter())
        {
            ID3D11DeviceContext ctx = _gpu.Context;
            int slot = _next;
            if (_pending[slot])
            {
                // The ring is full: this readback must finish now (it was queued StagingDepth frames ago).
                ReadbackWaits++;
                Deliver(slot, deliver, wait: true);
            }

            Texture2DDescription sd = source.Description;
            if (!ReferenceEquals(_srcViewTexture, source))
            {
                _srcView?.Dispose();
                _srcView = _gpu.Device.CreateShaderResourceView(source, new ShaderResourceViewDescription(source, Vortice.Direct3D.ShaderResourceViewDimension.Texture2D, ViewFormat(sd.Format)));
                _srcViewTexture = source;
            }

            WriteParams(ctx, sd.Width, sd.Height, transfer, sdrWhiteNits);
            ctx.CSSetShader(_shader);
            ctx.CSSetShaderResource(0, _srcView);
            ctx.CSSetSampler(0, _sampler);
            ctx.CSSetConstantBuffer(0, _params);
            ctx.CSSetUnorderedAccessView(0, _outputView, 0);
            ctx.Dispatch((uint)((_format.Width + 7) / 8), (uint)((_format.Height + 7) / 8), 1);
            ctx.CSSetUnorderedAccessView(0, null!, 0);
            ctx.CSSetShaderResource(0, null!);
            ctx.CopyResource(_staging[slot], _output);
            _stagingQpc[slot] = qpc;
            _pending[slot] = true;
            _next = (slot + 1) % StagingDepth;

            // Deliver whatever older readbacks are already done, oldest first.
            for (int k = 1; k < StagingDepth; k++)
            {
                int s = (slot + k) % StagingDepth;
                if (_pending[s] && !Deliver(s, deliver, wait: false))
                {
                    break;
                }
            }
        }

        Interlocked.Add(ref _busyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
    }

    /// <summary>Waits for and delivers every queued frame (end of session, tests).</summary>
    public void Flush(FrameReady deliver)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        using (_gpu.Enter())
        {
            for (int k = 0; k < StagingDepth; k++)
            {
                int s = (_next + k) % StagingDepth;
                if (_pending[s])
                {
                    Deliver(s, deliver, wait: true);
                }
            }
        }
    }

    /// <summary>Forgets the cached view (call when the capture source recreates its textures).</summary>
    public void ResetSource()
    {
        using (_gpu.Enter())
        {
            _srcView?.Dispose();
            _srcView = null;
            _srcViewTexture = null;
        }
    }

    private bool Deliver(int slot, FrameReady deliver, bool wait)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return DeliverCore(slot, deliver, wait);
        }
        finally
        {
            Interlocked.Add(ref _readbackTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }
    }

    private bool DeliverCore(int slot, FrameReady deliver, bool wait)
    {
        Result r = _gpu.Context.Map(_staging[slot], 0, 0, MapMode.Read, wait ? MapFlags.None : MapFlags.DoNotWait, out _, out MappedSubresource mapped);
        if (r == Vortice.DXGI.ResultCode.WasStillDrawing)
        {
            return false;
        }

        r.CheckError();
        try
        {
            unsafe
            {
                var span = new ReadOnlySpan<byte>((void*)mapped.DataPointer, (int)(mapped.RowPitch * (_format.Height - 1)) + (_format.Width * 4));
                deliver(span, (int)mapped.RowPitch, _stagingQpc[slot]);
            }
        }
        finally
        {
            _gpu.Context.Unmap(_staging[slot], 0);
            _pending[slot] = false;
            Completed++;
        }

        return true;
    }

    private void WriteParams(ID3D11DeviceContext ctx, uint srcW, uint srcH, TransferFunction transfer, float sdrWhite)
    {
        float dw = _format.Width, dh = _format.Height;
        float s = Math.Min(dw / srcW, dh / srcH);
        // Round the content size to whole pixels like ffmpeg's scale+pad does.
        float cw = Math.Min(dw, MathF.Round(srcW * s / 2) * 2), ch = Math.Min(dh, MathF.Round(srcH * s / 2) * 2);
        Span<float> p = stackalloc float[16];
        p[0] = srcW;
        p[1] = srcH;
        p[2] = dw;
        p[3] = dh;
        p[4] = MathF.Floor((dw - cw) / 2);
        p[5] = MathF.Floor((dh - ch) / 2);
        p[6] = cw;
        p[7] = ch;
        p[8] = BitConverter.Int32BitsToSingle((int)transfer);
        p[9] = sdrWhite;
        MappedSubresource m = ctx.Map(_params, MapMode.WriteDiscard, MapFlags.None);
        unsafe
        {
            MemoryMarshal.AsBytes(p).CopyTo(new Span<byte>((void*)m.DataPointer, 64));
        }

        ctx.Unmap(_params, 0);
    }

    /// <summary>Typeless and sRGB formats need a concrete view format; values stay as stored (no sRGB decode).</summary>
    private static DxgiFormat ViewFormat(DxgiFormat f) => f switch
    {
        DxgiFormat.B8G8R8A8_Typeless or DxgiFormat.B8G8R8A8_UNorm_SRgb => DxgiFormat.B8G8R8A8_UNorm,
        DxgiFormat.R8G8B8A8_Typeless or DxgiFormat.R8G8B8A8_UNorm_SRgb => DxgiFormat.R8G8B8A8_UNorm,
        DxgiFormat.R10G10B10A2_Typeless => DxgiFormat.R10G10B10A2_UNorm,
        DxgiFormat.R16G16B16A16_Typeless => DxgiFormat.R16G16B16A16_Float,
        _ => f,
    };

    public void Dispose()
    {
        _srcView?.Dispose();
        foreach (ID3D11Texture2D t in _staging)
        {
            t.Dispose();
        }

        _outputView.Dispose();
        _output.Dispose();
        _params.Dispose();
        _sampler.Dispose();
        _shader.Dispose();
    }

    private static class ShaderSource
    {
        public static readonly string Value = Load();

        private static string Load()
        {
            using Stream s = typeof(GpuDownscaler).Assembly.GetManifestResourceStream("Lookback.Capture.Gpu.Downscale.hlsl")
                ?? throw new InvalidOperationException("Downscale shader resource missing.");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
    }
}

/// <summary>Receives a read-back analysis frame: BGRA rows with <paramref name="rowPitch"/> bytes per row.</summary>
public delegate void FrameReady(ReadOnlySpan<byte> bgra, int rowPitch, long qpc);
