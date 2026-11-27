using System.Runtime.InteropServices;
using Lookback.Audio.Interop;

namespace Lookback.Audio;

/// <summary>Fills <paramref name="frames"/> interleaved float frames of render buffer.</summary>
internal delegate void RenderFill(Span<float> interleaved, int frames);

/// <summary>
/// A shared-mode, timer-serviced WASAPI render stream. The capture side uses
/// it to keep endpoint loopback alive: with nothing playing, the audio engine
/// idles and loopback delivers no packets at all, so a stream of silence
/// keeps them (flagged silent) coming and the timeline continuous. Tests use
/// it to play a tone. Allocation-free once open.
/// </summary>
internal sealed unsafe class RenderStream : IDisposable
{
    private nint _client;
    private nint _render;

    private RenderStream(nint client, nint render, PcmFormat format, uint bufferFrames)
    {
        _client = client;
        _render = render;
        Format = format;
        BufferFrames = bufferFrames;
    }

    public PcmFormat Format { get; }

    public uint BufferFrames { get; }

    /// <summary>Opens a stream on <paramref name="device"/> (an <c>IMMDevice</c>) in its mix format.</summary>
    public static RenderStream Open(nint device, long bufferHns = 2_000_000)
    {
        nint client = 0, render = 0;
        WaveFormatExtensible* mix = null;
        try
        {
            Native.Check(Wasapi.Activate(device, Wasapi.IidAudioClient, out client), "IMMDevice.Activate(render)");
            Native.Check(Wasapi.GetMixFormat(client, out mix), "IAudioClient.GetMixFormat(render)");
            PcmFormat format = WaveFormatExtensible.Describe(mix) ?? throw new WasapiException("The render mix format is not one we write.");
            Native.Check(Wasapi.Initialize(client, Wasapi.ShareModeShared, 0, bufferHns, 0, mix), "IAudioClient.Initialize(render)");
            Native.Check(Wasapi.GetBufferSize(client, out uint frames), "IAudioClient.GetBufferSize(render)");
            Native.Check(Wasapi.GetService(client, Wasapi.IidAudioRenderClient, out render), "IAudioClient.GetService(render)");
            var stream = new RenderStream(client, render, format, frames);
            client = render = 0;
            return stream;
        }
        finally
        {
            if (mix != null)
            {
                Marshal.FreeCoTaskMem((nint)mix);
            }

            Native.Release(ref render);
            Native.Release(ref client);
        }
    }

    /// <summary>Opens a stream on the default render device.</summary>
    public static RenderStream OpenDefault(long bufferHns = 2_000_000)
    {
        nint enumerator = Wasapi.CreateEnumerator();
        try
        {
            Native.Check(Wasapi.GetDefaultAudioEndpoint(enumerator, Wasapi.FlowRender, Wasapi.RoleConsole, out nint device), "GetDefaultAudioEndpoint(render)");
            try
            {
                return Open(device, bufferHns);
            }
            finally
            {
                Marshal.Release(device);
            }
        }
        finally
        {
            Marshal.Release(enumerator);
        }
    }

    public void Start() => Native.Check(Wasapi.Start(_client), "IAudioClient.Start(render)");

    /// <summary>
    /// Fills whatever room the buffer has, with <paramref name="fill"/> or
    /// silence. Returns the HRESULT (negative when the device went away).
    /// </summary>
    public int TopUp(RenderFill? fill, out int written)
    {
        written = 0;
        int hr = Wasapi.GetCurrentPadding(_client, out uint padding);
        if (hr < 0)
        {
            return hr;
        }

        uint free = BufferFrames - padding;
        if (free == 0)
        {
            return hr;
        }

        hr = Wasapi.RenderGetBuffer(_render, free, out byte* data);
        if (hr < 0)
        {
            return hr;
        }

        uint flags = Wasapi.BufferFlagsSilent;
        if (fill is not null && Format.Encoding == SampleEncoding.IeeeFloat && Format.BytesPerSample == 4)
        {
            fill(new Span<float>(data, (int)free * Format.Channels), (int)free);
            flags = 0;
        }

        written = (int)free;
        return Wasapi.RenderReleaseBuffer(_render, free, flags);
    }

    public void Dispose()
    {
        if (_client != 0)
        {
            Wasapi.Stop(_client);
        }

        Native.Release(ref _render);
        Native.Release(ref _client);
    }
}
