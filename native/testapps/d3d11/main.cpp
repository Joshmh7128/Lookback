// lookback-testapp-d3d11: a D3D11 flip-model swap chain that renders the
// frame-numbered test pattern at a fixed rate. One of the only two processes
// the Lookback present hook may be injected into (see docs/wiki/Present-Hook.md).
#include "testapp.h"

#include <d3d11_1.h>
#include <dxgi1_5.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

namespace
{

struct Renderer
{
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext1> context;
    ComPtr<IDXGISwapChain1> swap;
    ComPtr<ID3D11RenderTargetView> rtv;
    bool tearing = false;
    int width = 0;
    int height = 0;

    bool CreateTarget()
    {
        rtv.Reset();
        ComPtr<ID3D11Texture2D> back;
        if (FAILED(swap->GetBuffer(0, IID_PPV_ARGS(&back))))
        {
            return false;
        }
        return SUCCEEDED(device->CreateRenderTargetView(back.Get(), nullptr, &rtv));
    }

    bool Create(HWND hwnd, bool warp, int w, int h)
    {
        width = w;
        height = h;
        D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        ComPtr<ID3D11DeviceContext> ctx;
        HRESULT hr = D3D11CreateDevice(nullptr, warp ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                       D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION, &device, nullptr, &ctx);
        if (FAILED(hr) || FAILED(ctx.As(&context)))
        {
            return false;
        }
        ComPtr<IDXGIDevice> dxgiDevice;
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory2> factory;
        if (FAILED(device.As(&dxgiDevice)) || FAILED(dxgiDevice->GetAdapter(&adapter)) || FAILED(adapter->GetParent(IID_PPV_ARGS(&factory))))
        {
            return false;
        }
        ComPtr<IDXGIFactory5> factory5;
        BOOL allow = FALSE;
        if (SUCCEEDED(factory.As(&factory5)) && SUCCEEDED(factory5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allow, sizeof(allow))))
        {
            tearing = allow != FALSE;
        }
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = static_cast<UINT>(w);
        desc.Height = static_cast<UINT>(h);
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.Flags = tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;
        if (FAILED(factory->CreateSwapChainForHwnd(device.Get(), hwnd, &desc, nullptr, nullptr, &swap)))
        {
            return false;
        }
        factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);
        return CreateTarget();
    }

    void Destroy()
    {
        // Release, then ClearState + Flush: D3D11 destroys a swap chain lazily,
        // so the HWND is only free for a new one after the flush (and the
        // device may outlive us: the hook holds textures on it).
        rtv.Reset();
        swap.Reset();
        if (context)
        {
            context->ClearState();
            context->Flush();
        }
        context.Reset();
        device.Reset();
    }

    bool Resize(int w, int h)
    {
        width = w;
        height = h;
        rtv.Reset();
        context->ClearState();
        if (FAILED(swap->ResizeBuffers(0, static_cast<UINT>(w), static_cast<UINT>(h), DXGI_FORMAT_UNKNOWN,
                                       tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0)))
        {
            return false;
        }
        return CreateTarget();
    }

    HRESULT Draw(int64_t frame, bool present1, testapp::Pattern& p)
    {
        testapp::BuildPattern(frame, width, height, p);
        context->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
        context->ClearRenderTargetView(rtv.Get(), p.background);
        const float black[4] = {0, 0, 0, 1};
        const float white[4] = {1, 1, 1, 1};
        context->ClearView(rtv.Get(), black, &p.strip, 1);
        if (!p.ones.empty())
        {
            context->ClearView(rtv.Get(), white, p.ones.data(), static_cast<UINT>(p.ones.size()));
        }
        UINT flags = tearing ? DXGI_PRESENT_ALLOW_TEARING : 0;
        if (present1)
        {
            DXGI_PRESENT_PARAMETERS params{};
            return swap->Present1(0, flags, &params);
        }
        return swap->Present(0, flags);
    }
};

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    testapp::Options o;
    if (!testapp::ParseOptions(o))
    {
        testapp::Usage(L"lookback-testapp-d3d11");
        return 2;
    }
    testapp::Window window;
    if (!testapp::CreateAppWindow(window, L"Lookback test app (D3D11)", o.width, o.height))
    {
        return 3;
    }
    Renderer r;
    if (!r.Create(window.hwnd, o.warp, o.width, o.height))
    {
        return 4;
    }
    testapp::ShowAppWindow(window);
    testapp::Pacer pacer(o.fps);
    testapp::Pattern pattern;
    for (int64_t frame = 0; o.frames == 0 || frame < o.frames; frame++)
    {
        if (!testapp::Pump(window))
        {
            break;
        }
        if (frame == o.resize_at)
        {
            testapp::ResizeClient(window, o.width + 64, o.height + 36);
            testapp::Pump(window);
        }
        if (frame == o.recreate_at)
        {
            r.Destroy();
            if (!r.Create(window.hwnd, o.warp, r.width, r.height))
            {
                return 5;
            }
        }
        if (window.resized)
        {
            window.resized = false;
            int w = 0, h = 0;
            testapp::ClientSize(window, w, h);
            if ((w != r.width || h != r.height) && !r.Resize(w, h))
            {
                return 6;
            }
        }
        pacer.Wait(frame);
        HRESULT hr = r.Draw(frame, o.present1, pattern);
        if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET)
        {
            r.Destroy();
            if (!r.Create(window.hwnd, o.warp, r.width, r.height))
            {
                return 7;
            }
        }
    }
    r.Destroy();
    return 0;
}
