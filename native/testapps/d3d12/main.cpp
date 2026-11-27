// lookback-testapp-d3d12: a D3D12 flip-model swap chain that renders the
// frame-numbered test pattern at a fixed rate, with clears only (no shaders).
// One of the only two processes the Lookback present hook may be injected
// into (see docs/wiki/Present-Hook.md). --recreate-at N removes the device
// (ID3D12Device5::RemoveDevice) and recovers the way a game does after a
// device loss.
#include "testapp.h"

#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

namespace
{

constexpr UINT kBuffers = 3;

struct Renderer
{
    ComPtr<IDXGIFactory4> factory;
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<IDXGISwapChain3> swap;
    ComPtr<ID3D12DescriptorHeap> rtvHeap;
    ComPtr<ID3D12Resource> buffers[kBuffers];
    ComPtr<ID3D12CommandAllocator> allocators[kBuffers];
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> fence;
    HANDLE fenceEvent = nullptr;
    UINT64 fenceValue = 0;
    UINT64 frameFence[kBuffers] = {};
    UINT rtvSize = 0;
    bool tearing = false;
    int width = 0;
    int height = 0;

    bool CreateTargets()
    {
        D3D12_CPU_DESCRIPTOR_HANDLE h = rtvHeap->GetCPUDescriptorHandleForHeapStart();
        for (UINT i = 0; i < kBuffers; i++)
        {
            if (FAILED(swap->GetBuffer(i, IID_PPV_ARGS(&buffers[i]))))
            {
                return false;
            }
            device->CreateRenderTargetView(buffers[i].Get(), nullptr, h);
            h.ptr += rtvSize;
        }
        return true;
    }

    bool Create(HWND hwnd, bool warp, int w, int h)
    {
        width = w;
        height = h;
        if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory))))
        {
            return false;
        }
        ComPtr<IDXGIAdapter> adapter;
        if (warp && FAILED(factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter))))
        {
            return false;
        }
        if (FAILED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device))))
        {
            return false;
        }
        D3D12_COMMAND_QUEUE_DESC qd{};
        qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        if (FAILED(device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue))))
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
        desc.BufferCount = kBuffers;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.Flags = tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;
        ComPtr<IDXGISwapChain1> swap1;
        if (FAILED(factory->CreateSwapChainForHwnd(queue.Get(), hwnd, &desc, nullptr, nullptr, &swap1)) || FAILED(swap1.As(&swap)))
        {
            return false;
        }
        factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);
        D3D12_DESCRIPTOR_HEAP_DESC hd{};
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        hd.NumDescriptors = kBuffers;
        if (FAILED(device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&rtvHeap))))
        {
            return false;
        }
        rtvSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        for (auto& a : allocators)
        {
            if (FAILED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a))))
            {
                return false;
            }
        }
        if (FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocators[0].Get(), nullptr, IID_PPV_ARGS(&list))))
        {
            return false;
        }
        list->Close();
        if (FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence))))
        {
            return false;
        }
        fenceValue = 0;
        for (auto& f : frameFence)
        {
            f = 0;
        }
        if (fenceEvent == nullptr)
        {
            fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        }
        return CreateTargets();
    }

    void WaitFor(UINT64 value)
    {
        if (fence && fence->GetCompletedValue() < value && SUCCEEDED(fence->SetEventOnCompletion(value, fenceEvent)))
        {
            WaitForSingleObject(fenceEvent, 2000); // bounded: a removed device never completes
        }
    }

    void WaitIdle()
    {
        if (queue && fence)
        {
            queue->Signal(fence.Get(), ++fenceValue);
            WaitFor(fenceValue);
        }
    }

    void Destroy()
    {
        WaitIdle();
        for (auto& b : buffers)
        {
            b.Reset();
        }
        for (auto& a : allocators)
        {
            a.Reset();
        }
        list.Reset();
        rtvHeap.Reset();
        swap.Reset();
        fence.Reset();
        queue.Reset();
        device.Reset();
        factory.Reset();
    }

    bool Resize(int w, int h)
    {
        WaitIdle();
        width = w;
        height = h;
        for (auto& b : buffers)
        {
            b.Reset();
        }
        if (FAILED(swap->ResizeBuffers(kBuffers, static_cast<UINT>(w), static_cast<UINT>(h), DXGI_FORMAT_UNKNOWN,
                                       tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0)))
        {
            return false;
        }
        return CreateTargets();
    }

    void RemoveDevice()
    {
        ComPtr<ID3D12Device5> d5;
        if (SUCCEEDED(device.As(&d5)))
        {
            d5->RemoveDevice();
        }
    }

    HRESULT Draw(int64_t frame, bool present1, testapp::Pattern& p)
    {
        UINT i = swap->GetCurrentBackBufferIndex();
        WaitFor(frameFence[i]);
        if (FAILED(allocators[i]->Reset()) || FAILED(list->Reset(allocators[i].Get(), nullptr)))
        {
            return device->GetDeviceRemovedReason();
        }
        testapp::BuildPattern(frame, width, height, p);
        D3D12_RESOURCE_BARRIER b{};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = buffers[i].Get();
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
        b.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        list->ResourceBarrier(1, &b);
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += static_cast<SIZE_T>(i) * rtvSize;
        const float black[4] = {0, 0, 0, 1};
        const float white[4] = {1, 1, 1, 1};
        list->ClearRenderTargetView(rtv, p.background, 0, nullptr);
        list->ClearRenderTargetView(rtv, black, 1, &p.strip);
        if (!p.ones.empty())
        {
            list->ClearRenderTargetView(rtv, white, static_cast<UINT>(p.ones.size()), p.ones.data());
        }
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        b.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
        list->ResourceBarrier(1, &b);
        list->Close();
        ID3D12CommandList* lists[] = {list.Get()};
        queue->ExecuteCommandLists(1, lists);
        UINT flags = tearing ? DXGI_PRESENT_ALLOW_TEARING : 0;
        HRESULT hr;
        if (present1)
        {
            DXGI_PRESENT_PARAMETERS params{};
            hr = swap->Present1(0, flags, &params);
        }
        else
        {
            hr = swap->Present(0, flags);
        }
        queue->Signal(fence.Get(), ++fenceValue);
        frameFence[i] = fenceValue;
        return hr;
    }
};

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    testapp::Options o;
    if (!testapp::ParseOptions(o))
    {
        testapp::Usage(L"lookback-testapp-d3d12");
        return 2;
    }
    testapp::Window window;
    if (!testapp::CreateAppWindow(window, L"Lookback test app (D3D12)", o.width, o.height))
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
    int recoveries = 0;
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
        if (frame == o.recreate_at)
        {
            r.RemoveDevice(); // the next Present fails with DXGI_ERROR_DEVICE_REMOVED and we recover below
        }
        pacer.Wait(frame);
        HRESULT hr = r.Draw(frame, o.present1, pattern);
        if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET || hr == DXGI_ERROR_DEVICE_HUNG)
        {
            if (++recoveries > 3)
            {
                return 7;
            }
            int w = r.width, h = r.height;
            r.Destroy();
            // D3D12 devices are per-adapter singletons: this only gets a fresh
            // device once every reference to the removed one is gone, the
            // hook's included.
            bool ok = false;
            for (int attempt = 0; attempt < 50 && !ok; attempt++)
            {
                ok = r.Create(window.hwnd, o.warp, w, h);
                if (!ok)
                {
                    r.Destroy();
                    Sleep(20);
                }
                else if (r.device->GetDeviceRemovedReason() != S_OK)
                {
                    ok = false;
                    r.Destroy();
                    Sleep(20);
                }
            }
            if (!ok)
            {
                return 8;
            }
        }
    }
    r.Destroy();
    if (r.fenceEvent != nullptr)
    {
        CloseHandle(r.fenceEvent);
    }
    return 0;
}
