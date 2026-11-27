// D3D12 back end: a ring of shared textures (HEAP_FLAG_SHARED, simultaneous
// access) and one shared fence on the game's device, filled by our own copy
// queue. Per present:
//
//   game queue  ── Signal(sync, n) ──▶ copy queue Wait(sync, n) → CopyResource(slot ← back buffer)
//                                       → Signal(shared fence, v)
//   game queue  ── Wait(shared fence, v) → Present
//
// The game's queue waits on the GPU for our copy (which depends only on work
// it already submitted) so the copy reads the back buffer before the flip;
// the CPU present thread never waits. Slot ownership is a CAS on
// slots[i].state in shared memory (FREE → WRITING by us, FREE → READING by the
// receiver); a slot whose previous copy is still in flight, or that the reader
// owns, is skipped, and when none is left the frame is dropped. Everything on
// the present thread is non-blocking. A watcher thread releases every
// reference as soon as the device is removed: D3D12 devices are per-adapter
// singletons, so a game can only recreate its device once ours are gone too.
#include "hook_internal.h"

#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <new>

using Microsoft::WRL::ComPtr;

namespace lbh
{

struct D3D12Capture::State
{
    IDXGISwapChain* swap = nullptr; // identity only
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> gameQueue;
    ComPtr<ID3D12CommandQueue> copyQueue;
    ComPtr<ID3D12Fence> syncFence; // private: game queue → copy queue
    UINT64 syncValue = 0;
    ComPtr<ID3D12Fence> sharedFence; // shared with the receiver
    HANDLE sharedFenceHandle = nullptr;
    UINT64 fenceValue = 0;
    HANDLE waitEvent = nullptr; // BeforeResize only
    struct Slot
    {
        ComPtr<ID3D12Resource> texture;
        HANDLE handle = nullptr;
        ComPtr<ID3D12CommandAllocator> allocator;
        ComPtr<ID3D12GraphicsCommandList> list;
        UINT64 lastValue = 0;
    } slots[LBH_MAX_SLOTS];
    UINT width = 0;
    UINT height = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    UINT next = 0;
    bool ready = false;     // textures exist
};

namespace
{
bool InFlight(const ComPtr<ID3D12Fence>& fence, UINT64 value)
{
    return fence && value > fence->GetCompletedValue();
}
} // namespace

// Releases the textures; with force also the device objects. Returns false
// (and keeps everything) when copies are still in flight on a live device.
bool D3D12Capture::ReleaseLocked(bool force)
{
    bool removed = s_->device && s_->device->GetDeviceRemovedReason() != S_OK;
    if (!force && !removed && InFlight(s_->sharedFence, s_->fenceValue))
    {
        return false;
    }
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
        h->fence_handle = 0;
        ch.EndConfig();
    }
    for (auto& slot : s_->slots)
    {
        slot.list.Reset();
        slot.allocator.Reset();
        slot.texture.Reset();
        if (slot.handle != nullptr)
        {
            CloseHandle(slot.handle);
            slot.handle = nullptr;
        }
        slot.lastValue = 0;
    }
    s_->ready = false;
    s_->width = s_->height = 0;
    s_->format = DXGI_FORMAT_UNKNOWN;
    if (force || removed)
    {
        s_->copyQueue.Reset();
        s_->syncFence.Reset();
        s_->sharedFence.Reset();
        if (s_->sharedFenceHandle != nullptr)
        {
            CloseHandle(s_->sharedFenceHandle);
            s_->sharedFenceHandle = nullptr;
        }
        s_->gameQueue.Reset();
        s_->device.Reset();
        s_->syncValue = 0;
        s_->fenceValue = 0;
    }
    return true;
}

bool D3D12Capture::CreateLocked(UINT width, UINT height, DXGI_FORMAT format)
{
    if (!ReleaseLocked(false))
    {
        return false; // previous copies still running: try again next present
    }
    ID3D12Device* dev = s_->device.Get();
    HRESULT hr = S_OK;
    if (!s_->copyQueue)
    {
        D3D12_COMMAND_QUEUE_DESC qd{};
        qd.Type = D3D12_COMMAND_LIST_TYPE_COPY;
        hr = dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&s_->copyQueue));
        if (SUCCEEDED(hr))
        {
            hr = dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&s_->syncFence));
        }
        if (SUCCEEDED(hr))
        {
            hr = dev->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&s_->sharedFence));
        }
        if (SUCCEEDED(hr))
        {
            hr = dev->CreateSharedHandle(s_->sharedFence.Get(), nullptr, GENERIC_ALL, nullptr, &s_->sharedFenceHandle);
        }
        if (FAILED(hr))
        {
            ReportError("creating the D3D12 copy queue and fences", hr);
            ReleaseLocked(true);
            return false;
        }
        s_->syncValue = 0;
        s_->fenceValue = 0;
    }

    D3D12_HEAP_PROPERTIES hp{};
    hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC rd{};
    rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    rd.Width = width;
    rd.Height = height;
    rd.DepthOrArraySize = 1;
    rd.MipLevels = 1;
    rd.Format = format;
    rd.SampleDesc.Count = 1;
    rd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
    for (auto& slot : s_->slots)
    {
        hr = dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_SHARED, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&slot.texture));
        if (SUCCEEDED(hr))
        {
            hr = dev->CreateSharedHandle(slot.texture.Get(), nullptr, GENERIC_ALL, nullptr, &slot.handle);
        }
        if (SUCCEEDED(hr))
        {
            hr = dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COPY, IID_PPV_ARGS(&slot.allocator));
        }
        if (SUCCEEDED(hr))
        {
            hr = dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COPY, slot.allocator.Get(), nullptr, IID_PPV_ARGS(&slot.list));
        }
        if (SUCCEEDED(hr))
        {
            hr = slot.list->Close();
        }
        if (FAILED(hr))
        {
            ReportError("creating the D3D12 shared texture", hr);
            ReleaseLocked(false);
            return false;
        }
    }
    LUID l = dev->GetAdapterLuid();
    Channel& ch = TheChannel();
    lbh_header* h = ch.Header();
    ch.BeginConfig();
    h->generation++;
    h->api = LBH_API_D3D12;
    h->sync = LBH_SYNC_FENCE;
    h->slot_count = LBH_MAX_SLOTS;
    h->width = width;
    h->height = height;
    h->format = static_cast<uint32_t>(format);
    h->queue_source = 2; // ExecuteCommandLists
    h->adapter_luid = static_cast<int64_t>(l.LowPart) | (static_cast<int64_t>(l.HighPart) << 32);
    for (UINT i = 0; i < LBH_MAX_SLOTS; i++)
    {
        h->texture_handles[i] = reinterpret_cast<uint64_t>(s_->slots[i].handle);
        h->slots[i].generation = h->generation;
        h->slots[i].record = -1;
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&h->slots[i].state), LBH_SLOT_FREE);
    }
    h->fence_handle = reinterpret_cast<uint64_t>(s_->sharedFenceHandle);
    ch.EndConfig();
    s_->width = width;
    s_->height = height;
    s_->format = format;
    s_->next = 0;
    s_->ready = true;
    return true;
}

bool D3D12Capture::OnPresent(IDXGISwapChain* swap, int64_t qpc)
{
    if (s_ == nullptr)
    {
        s_ = new (std::nothrow) State();
        if (s_ == nullptr)
        {
            return false;
        }
    }
    Channel& ch = TheChannel();
    bool switched = false;
    bool fresh = swap != s_->swap || !s_->device;
    ComPtr<ID3D12Resource> probe;
    if (fresh && FAILED(swap->GetBuffer(0, IID_PPV_ARGS(&probe))))
    {
        return false; // not a D3D12 swap chain
    }
    if (!IsTrackedSwapChain(swap, qpc, switched))
    {
        return true; // a second swap chain while the first is active: ignored
    }

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

    if (fresh)
    {
        // Let go of the old swap chain's objects once its copies are done.
        if (s_->device && !ReleaseLocked(false))
        {
            ch.Drop(DropKind::NotReady);
            return true;
        }
        ReleaseLocked(true);
        ComPtr<ID3D12Device> device;
        HRESULT hr = probe->GetDevice(IID_PPV_ARGS(&device));
        if (FAILED(hr) || device->GetDeviceRemovedReason() != S_OK)
        {
            ReportError("finding the D3D12 device", FAILED(hr) ? hr : device->GetDeviceRemovedReason());
            ch.Drop(DropKind::Device);
            return true; // retried on the next present (s_->device stays null)
        }
        // The swap chain does not hand out its queue: use the direct queue on
        // this device that last executed command lists (the one the frame was
        // rendered on, for a single-queue renderer).
        ComPtr<ID3D12CommandQueue> queue;
        queue.Attach(RecentDirectQueue(device.Get()));
        if (!queue)
        {
            ch.SetStatus("waiting for the game's direct queue (ExecuteCommandLists)");
            ch.Drop(DropKind::NotReady);
            return true;
        }
        if (queue->GetDesc().Type != D3D12_COMMAND_LIST_TYPE_DIRECT)
        {
            ch.SetStatus("the swap chain's queue is not a direct queue");
            ch.Drop(DropKind::NotReady);
            return true;
        }
        probe.Reset();
        s_->device = device;
        s_->gameQueue = queue;
        s_->swap = swap;
    }

    if (!s_->device || s_->device->GetDeviceRemovedReason() != S_OK)
    {
        if (s_->device)
        {
            ReportError("D3D12 device", s_->device->GetDeviceRemovedReason());
        }
        ReleaseLocked(true);
        ch.Drop(DropKind::Device);
        return true;
    }
    ComPtr<IDXGISwapChain3> swap3;
    ComPtr<ID3D12Resource> back;
    HRESULT hr = swap->QueryInterface(IID_PPV_ARGS(&swap3));
    if (SUCCEEDED(hr))
    {
        hr = swap->GetBuffer(swap3->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&back));
    }
    if (FAILED(hr))
    {
        ch.Drop(DropKind::Device);
        return true;
    }
    D3D12_RESOURCE_DESC bd = back->GetDesc();
    DXGI_FORMAT format = SharedFormat(bd.Format);
    if (format == DXGI_FORMAT_UNKNOWN || bd.SampleDesc.Count != 1)
    {
        ch.SetStatus("unsupported back buffer format");
        ch.Drop(DropKind::NotReady);
        return true;
    }
    auto width = static_cast<UINT>(bd.Width);
    if (!s_->ready || width != s_->width || bd.Height != s_->height || format != s_->format)
    {
        if (!CreateLocked(width, bd.Height, format))
        {
            ch.Drop(DropKind::NotReady);
            return true;
        }
    }

    lbh_header* h = ch.Header();
    int found = -1;
    for (UINT k = 0; k < LBH_MAX_SLOTS && found < 0; k++)
    {
        UINT i = (s_->next + k) % LBH_MAX_SLOTS;
        if (InFlight(s_->sharedFence, s_->slots[i].lastValue))
        {
            continue; // our previous copy into it (and so its allocator) is still running
        }
        if (InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&h->slots[i].state), LBH_SLOT_WRITING, LBH_SLOT_FREE) == LBH_SLOT_FREE)
        {
            found = static_cast<int>(i);
        }
    }
    if (found < 0)
    {
        ch.Drop(DropKind::Busy);
        return true;
    }
    auto& slot = s_->slots[found];
    lbh_slot& meta = h->slots[found];
    hr = slot.allocator->Reset();
    if (SUCCEEDED(hr))
    {
        hr = slot.list->Reset(slot.allocator.Get(), nullptr);
    }
    if (SUCCEEDED(hr))
    {
        slot.list->CopyResource(slot.texture.Get(), back.Get());
        hr = slot.list->Close();
    }
    if (SUCCEEDED(hr))
    {
        hr = s_->gameQueue->Signal(s_->syncFence.Get(), ++s_->syncValue);
    }
    if (SUCCEEDED(hr))
    {
        hr = s_->copyQueue->Wait(s_->syncFence.Get(), s_->syncValue);
    }
    if (FAILED(hr))
    {
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&meta.state), LBH_SLOT_FREE);
        ReportError("recording the D3D12 copy", hr);
        ch.Drop(DropKind::Device);
        return true;
    }
    ID3D12CommandList* lists[] = {slot.list.Get()};
    s_->copyQueue->ExecuteCommandLists(1, lists);
    UINT64 value = ++s_->fenceValue;
    hr = s_->copyQueue->Signal(s_->sharedFence.Get(), value);
    slot.lastValue = value;
    if (SUCCEEDED(hr))
    {
        // The game's queue waits (on the GPU) for our copy before it executes
        // Present: a FLIP_DISCARD back buffer's contents are undefined once
        // presented, and under load the copy engine otherwise ran after the
        // flip and read the previous frame. The copy depends only on work the
        // game has already submitted, so this cannot deadlock; it costs the
        // game's GPU timeline one copy. The CPU present thread does not wait.
        hr = s_->gameQueue->Wait(s_->sharedFence.Get(), value);
    }
    if (FAILED(hr))
    {
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&meta.state), LBH_SLOT_FREE);
        ReportError("queueing the D3D12 copy hand-over", hr);
        ch.Drop(DropKind::Device);
        return true;
    }

    int64_t record = ch.NextRecord();
    int64_t dropped = ch.Dropped();
    meta.generation = h->generation;
    meta.record = record;
    meta.frame = frame;
    meta.qpc = qpc;
    meta.dropped = dropped;
    meta.fence_value = value;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&meta.state), LBH_SLOT_FREE); // release: metadata before the hand-over
    s_->next = static_cast<UINT>(found + 1) % LBH_MAX_SLOTS;
    ch.Publish(static_cast<uint32_t>(found), frame, qpc, dropped, value);
    return true;
}

void D3D12Capture::BeforeResize(IDXGISwapChain* swap, DWORD timeoutMs)
{
    if (s_ == nullptr || swap != s_->swap)
    {
        return;
    }
    // Rare and on the game's own resize path: a bounded wait so our copy is
    // not still reading a back buffer that ResizeBuffers is about to free.
    AcquireSRWLockExclusive(&lock_);
    if (s_->sharedFence && InFlight(s_->sharedFence, s_->fenceValue))
    {
        if (s_->waitEvent == nullptr)
        {
            s_->waitEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        }
        if (s_->waitEvent != nullptr && SUCCEEDED(s_->sharedFence->SetEventOnCompletion(s_->fenceValue, s_->waitEvent)))
        {
            WaitForSingleObject(s_->waitEvent, timeoutMs);
        }
    }
    ReleaseSRWLockExclusive(&lock_);
}

void D3D12Capture::Watch()
{
    if (s_ == nullptr)
    {
        return;
    }
    AcquireSRWLockExclusive(&lock_);
    if (s_->device && s_->device->GetDeviceRemovedReason() != S_OK)
    {
        ReportError("D3D12 device (watcher)", s_->device->GetDeviceRemovedReason());
        ReleaseLocked(true);
        ForgetRecentQueue(); // the cached queue holds the removed device too
        s_->swap = nullptr; // re-identify on the next present
    }
    ReleaseSRWLockExclusive(&lock_);
}

} // namespace lbh
