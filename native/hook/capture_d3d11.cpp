// D3D11 back end: a ring of shared keyed-mutex textures on the game's device.
// Key 0 means the writer may take a slot, key 1 means it holds a frame for
// the reader. The writer takes a slot with a zero timeout on either key (key
// 1 = overwrite a frame the reader skipped), so the present thread never
// waits; when the reader holds every slot the frame is dropped.
#include "hook_internal.h"

#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <new>

using Microsoft::WRL::ComPtr;

namespace lbh
{

struct D3D11Capture::State
{
    IDXGISwapChain* swap = nullptr; // identity only, no reference
    bool isD3D11 = false;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    struct Slot
    {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<IDXGIKeyedMutex> mutex;
        HANDLE handle = nullptr;
    } slots[LBH_MAX_SLOTS];
    UINT width = 0;
    UINT height = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    UINT next = 0;
    bool ready = false;
    bool deviceLost = false;
};

void D3D11Capture::ReleaseLocked()
{
    Channel& ch = TheChannel();
    lbh_header* h = ch.Header();
    if (s_->ready)
    {
        ch.BeginConfig();
        h->generation++;
        h->slot_count = 0;
        for (auto& th : h->texture_handles)
        {
            th = 0;
        }
        ch.EndConfig();
    }
    for (auto& slot : s_->slots)
    {
        slot.mutex.Reset();
        slot.texture.Reset();
        if (slot.handle != nullptr)
        {
            CloseHandle(slot.handle);
            slot.handle = nullptr;
        }
    }
    s_->ready = false;
    s_->width = s_->height = 0;
    s_->format = DXGI_FORMAT_UNKNOWN;
}

bool D3D11Capture::CreateLocked(UINT width, UINT height, DXGI_FORMAT format)
{
    ReleaseLocked();
    D3D11_TEXTURE2D_DESC d{};
    d.Width = width;
    d.Height = height;
    d.MipLevels = 1;
    d.ArraySize = 1;
    d.Format = format;
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    d.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    for (auto& slot : s_->slots)
    {
        HRESULT hr = s_->device->CreateTexture2D(&d, nullptr, &slot.texture);
        ComPtr<IDXGIResource1> res;
        if (SUCCEEDED(hr))
        {
            hr = slot.texture.As(&res);
        }
        if (SUCCEEDED(hr))
        {
            hr = res->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &slot.handle);
        }
        if (SUCCEEDED(hr))
        {
            hr = slot.texture.As(&slot.mutex);
        }
        if (FAILED(hr))
        {
            ReportError("creating the D3D11 shared texture", hr);
            ReleaseLocked();
            return false;
        }
    }
    int64_t luid = 0;
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC ad{};
    if (SUCCEEDED(s_->device.As(&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&ad)))
    {
        luid = static_cast<int64_t>(ad.AdapterLuid.LowPart) | (static_cast<int64_t>(ad.AdapterLuid.HighPart) << 32);
    }
    Channel& ch = TheChannel();
    lbh_header* h = ch.Header();
    ch.BeginConfig();
    h->generation++;
    h->api = LBH_API_D3D11;
    h->sync = LBH_SYNC_KEYED_MUTEX;
    h->slot_count = LBH_MAX_SLOTS;
    h->width = width;
    h->height = height;
    h->format = static_cast<uint32_t>(format);
    h->queue_source = 0;
    h->adapter_luid = luid;
    for (UINT i = 0; i < LBH_MAX_SLOTS; i++)
    {
        h->texture_handles[i] = reinterpret_cast<uint64_t>(s_->slots[i].handle);
        h->slots[i].generation = h->generation;
        h->slots[i].record = -1;
    }
    h->fence_handle = 0;
    ch.EndConfig();
    s_->width = width;
    s_->height = height;
    s_->format = format;
    s_->next = 0;
    s_->ready = true;
    return true;
}

bool D3D11Capture::OnPresent(IDXGISwapChain* swap, int64_t qpc)
{
    if (s_ == nullptr)
    {
        s_ = new (std::nothrow) State();
        if (s_ == nullptr)
        {
            return false;
        }
    }
    // Cheap identity check first; GetDevice only when the swap chain changes
    // or the device was lost (a recreated swap chain can reuse the address).
    bool switched = false;
    if (swap != s_->swap || s_->deviceLost)
    {
        ComPtr<ID3D11Device> device;
        if (FAILED(swap->GetDevice(IID_PPV_ARGS(&device))))
        {
            return false; // not a D3D11 swap chain: the D3D12 back end tries it
        }
        if (!IsTrackedSwapChain(swap, qpc, switched))
        {
            return true; // a second swap chain while the first is active: ignored
        }
        if (!TryAcquireSRWLockExclusive(&lock_))
        {
            TheChannel().BeginPresent(qpc);
            TheChannel().Drop(DropKind::Busy);
            return true;
        }
        if (swap != s_->swap || device != s_->device)
        {
            ReleaseLocked();
            s_->context.Reset();
            s_->device = device;
            device->GetImmediateContext(&s_->context);
            s_->swap = swap;
        }
        s_->deviceLost = device->GetDeviceRemovedReason() != S_OK;
        if (s_->deviceLost)
        {
            s_->context.Reset();
            s_->device.Reset();
        }
        ReleaseSRWLockExclusive(&lock_);
    }
    else if (!IsTrackedSwapChain(swap, qpc, switched))
    {
        return true;
    }

    Channel& ch = TheChannel();
    int64_t frame = ch.BeginPresent(qpc);
    if (!TryAcquireSRWLockExclusive(&lock_))
    {
        ch.Drop(DropKind::Busy); // the watcher is releasing after a device loss
        return true;
    }
    struct Unlock
    {
        SRWLOCK* l;
        ~Unlock() { ReleaseSRWLockExclusive(l); }
    } unlock{&lock_};

    if (s_->deviceLost || !s_->device || s_->device->GetDeviceRemovedReason() != S_OK)
    {
        if (!s_->deviceLost && s_->device)
        {
            ReportError("D3D11 device", s_->device->GetDeviceRemovedReason());
        }
        ReleaseLocked();
        s_->context.Reset();
        s_->device.Reset();
        s_->deviceLost = true;
        ch.Drop(DropKind::Device);
        return true;
    }
    ComPtr<ID3D11Texture2D> back;
    HRESULT hr = swap->GetBuffer(0, IID_PPV_ARGS(&back));
    if (FAILED(hr))
    {
        ch.Drop(DropKind::Device);
        return true;
    }
    D3D11_TEXTURE2D_DESC bd{};
    back->GetDesc(&bd);
    DXGI_FORMAT format = SharedFormat(bd.Format);
    if (format == DXGI_FORMAT_UNKNOWN)
    {
        ch.SetStatus("unsupported back buffer format");
        ch.Drop(DropKind::NotReady);
        return true;
    }
    if (!s_->ready || bd.Width != s_->width || bd.Height != s_->height || format != s_->format)
    {
        if (!CreateLocked(bd.Width, bd.Height, format))
        {
            ch.Drop(DropKind::NotReady);
            return true;
        }
    }

    // Find a slot: zero-timeout acquire on key 0 (free) or key 1 (an unread frame).
    int found = -1;
    for (UINT k = 0; k < LBH_MAX_SLOTS && found < 0; k++)
    {
        UINT i = (s_->next + k) % LBH_MAX_SLOTS;
        IDXGIKeyedMutex* m = s_->slots[i].mutex.Get();
        hr = m->AcquireSync(0, 0);
        if (hr == static_cast<HRESULT>(WAIT_TIMEOUT))
        {
            hr = m->AcquireSync(1, 0);
        }
        if (hr == S_OK)
        {
            found = static_cast<int>(i);
        }
        else if (hr == static_cast<HRESULT>(WAIT_ABANDONED) || FAILED(hr))
        {
            // The reader died holding it, or the device is gone: start over next present.
            ReportError("AcquireSync", hr);
            ReleaseLocked();
            ch.Drop(DropKind::Device);
            return true;
        }
    }
    if (found < 0)
    {
        ch.Drop(DropKind::Busy);
        return true;
    }
    auto& slot = s_->slots[found];
    if (bd.SampleDesc.Count > 1)
    {
        s_->context->ResolveSubresource(slot.texture.Get(), 0, back.Get(), 0, format);
    }
    else
    {
        s_->context->CopyResource(slot.texture.Get(), back.Get());
    }
    lbh_header* h = ch.Header();
    int64_t record = ch.NextRecord();
    int64_t dropped = ch.Dropped();
    lbh_slot& meta = h->slots[found];
    meta.generation = h->generation;
    meta.record = record;
    meta.frame = frame;
    meta.qpc = qpc;
    meta.dropped = dropped;
    meta.fence_value = 0;
    slot.mutex->ReleaseSync(1);
    s_->next = static_cast<UINT>(found + 1) % LBH_MAX_SLOTS;
    ch.Publish(static_cast<uint32_t>(found), frame, qpc, dropped, 0);
    return true;
}

void D3D11Capture::Watch()
{
    if (s_ == nullptr)
    {
        return;
    }
    AcquireSRWLockExclusive(&lock_); // the watcher thread may wait; the present thread only tries
    if (s_->device && !s_->deviceLost && s_->device->GetDeviceRemovedReason() != S_OK)
    {
        ReportError("D3D11 device (watcher)", s_->device->GetDeviceRemovedReason());
        ReleaseLocked();
        s_->context.Reset();
        s_->device.Reset();
        s_->deviceLost = true;
    }
    ReleaseSRWLockExclusive(&lock_);
}

} // namespace lbh
