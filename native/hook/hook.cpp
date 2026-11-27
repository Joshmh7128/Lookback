// Lookback present hook: loaded only into Lookback's own test apps (see
// docs/wiki/Present-Hook.md). It patches the IDXGISwapChain vtable (Present,
// Present1, ResizeBuffers, ResizeBuffers1) found on dummy swap chains, copies
// each presented back buffer into a shared texture and publishes the frame's
// QPC, size and number in the receiver's shared-memory ring.
//
// It refuses to do anything unless (1) the receiver created a channel for this
// process id before loading it, and (2) the host executable exports the
// LookbackTestAppMarker string. The present thread never waits: when nothing
// is free the frame is dropped and counted.
#include "hook_internal.h"

#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdio>
#include <cstring>

using Microsoft::WRL::ComPtr;

namespace lbh
{
namespace
{

HMODULE g_module = nullptr;
Channel g_channel;
D3D11Capture g_d3d11;
D3D12Capture g_d3d12;
LARGE_INTEGER g_freq{};
std::atomic<bool> g_installed{false};
std::atomic<bool> g_exiting{false};

// vtable slots (dxgi.h declaration order; IUnknown 0-2, IDXGIObject 3-6,
// IDXGIDeviceSubObject 7, IDXGISwapChain 8-17, IDXGISwapChain1 18-28,
// IDXGISwapChain2 29-35, IDXGISwapChain3 36-39).
constexpr int kPresent = 8;
constexpr int kResizeBuffers = 13;
constexpr int kPresent1 = 22;
constexpr int kResizeBuffers1 = 39;

using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using ResizeBuffersFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using ResizeBuffers1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, UINT, UINT, UINT, DXGI_FORMAT, UINT, const UINT*, IUnknown* const*);

// D3D11 and D3D12 swap chains may or may not share a vtable; keep the
// originals per patched vtable and look them up by the object's vtable.
struct PatchedVtable
{
    void** vtbl = nullptr;
    PresentFn present = nullptr;
    Present1Fn present1 = nullptr;
    ResizeBuffersFn resize = nullptr;
    ResizeBuffers1Fn resize1 = nullptr; // null when the vtable is not an IDXGISwapChain3
};

constexpr int kMaxVtables = 4;
PatchedVtable g_vtables[kMaxVtables];
std::atomic<int> g_vtableCount{0};

const PatchedVtable* Find(void* object)
{
    void** vtbl = *static_cast<void***>(object);
    int n = g_vtableCount.load(std::memory_order_acquire);
    for (int i = 0; i < n; i++)
    {
        if (g_vtables[i].vtbl == vtbl)
        {
            return &g_vtables[i];
        }
    }
    return nullptr;
}

thread_local bool t_inPresent = false;

// ---- ID3D12CommandQueue::ExecuteCommandLists (vtable slot 10: IUnknown 0-2,
// ID3D12Object 3-6, ID3D12DeviceChild 7, UpdateTileMappings 8, CopyTileMappings 9).
constexpr int kExecuteCommandLists = 10;
using ExecuteCommandListsFn = void(STDMETHODCALLTYPE*)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);
std::atomic<ExecuteCommandListsFn> g_ecl{nullptr};
void** g_queueVtbl = nullptr;
SRWLOCK g_queueLock = SRWLOCK_INIT;
ID3D12CommandQueue* g_recentQueue = nullptr; // one reference, guarded by g_queueLock

void STDMETHODCALLTYPE HookExecuteCommandLists(ID3D12CommandQueue* queue, UINT count, ID3D12CommandList* const* lists)
{
    // Remember the queue the game renders with. Only on a change, and only if
    // the lock is free: this runs on the game's submission threads.
    if (queue != g_recentQueue && TryAcquireSRWLockExclusive(&g_queueLock))
    {
        if (queue != g_recentQueue && queue->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT)
        {
            queue->AddRef();
            if (g_recentQueue != nullptr)
            {
                g_recentQueue->Release();
            }
            g_recentQueue = queue;
        }
        ReleaseSRWLockExclusive(&g_queueLock);
    }
    g_ecl.load(std::memory_order_acquire)(queue, count, lists);
}

void OnPresent(IDXGISwapChain* swap, UINT flags)
{
    if ((flags & DXGI_PRESENT_TEST) != 0 || t_inPresent || g_exiting.load(std::memory_order_relaxed))
    {
        return;
    }
    t_inPresent = true;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if (!g_d3d11.OnPresent(swap, now.QuadPart) && !g_d3d12.OnPresent(swap, now.QuadPart))
    {
        // Neither API recognised the swap chain (for example a D3D9Ex or
        // composition swap chain): nothing to capture.
    }
    t_inPresent = false;
}

HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* swap, UINT sync, UINT flags)
{
    const PatchedVtable* v = Find(swap);
    OnPresent(swap, flags);
    return v->present(swap, sync, flags);
}

HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* swap, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
{
    const PatchedVtable* v = Find(swap);
    OnPresent(swap, flags);
    return v->present1(swap, sync, flags, params);
}

HRESULT STDMETHODCALLTYPE HookResizeBuffers(IDXGISwapChain* swap, UINT count, UINT w, UINT h, DXGI_FORMAT fmt, UINT flags)
{
    const PatchedVtable* v = Find(swap);
    g_d3d12.BeforeResize(swap, 100);
    return v->resize(swap, count, w, h, fmt, flags);
}

HRESULT STDMETHODCALLTYPE HookResizeBuffers1(IDXGISwapChain3* swap, UINT count, UINT w, UINT h, DXGI_FORMAT fmt, UINT flags,
                                             const UINT* nodeMask, IUnknown* const* queues)
{
    const PatchedVtable* v = Find(swap);
    g_d3d12.BeforeResize(swap, 100);
    return v->resize1(swap, count, w, h, fmt, flags, nodeMask, queues);
}

bool WriteSlot(void** slot, void* value)
{
    DWORD old = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &old))
    {
        return false;
    }
    InterlockedExchangePointer(slot, value);
    VirtualProtect(slot, sizeof(void*), old, &old);
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    return true;
}

// Records the originals of this swap chain's vtable and points its entries at our hooks.
bool PatchSwapChain(IDXGISwapChain1* swap)
{
    void** vtbl = *reinterpret_cast<void***>(swap);
    if (Find(swap) != nullptr)
    {
        return true;
    }
    int n = g_vtableCount.load();
    if (n >= kMaxVtables)
    {
        return false;
    }
    PatchedVtable& p = g_vtables[n];
    p.vtbl = vtbl;
    p.present = reinterpret_cast<PresentFn>(vtbl[kPresent]);
    p.present1 = reinterpret_cast<Present1Fn>(vtbl[kPresent1]);
    p.resize = reinterpret_cast<ResizeBuffersFn>(vtbl[kResizeBuffers]);
    ComPtr<IDXGISwapChain3> swap3;
    bool has3 = SUCCEEDED(swap->QueryInterface(IID_PPV_ARGS(&swap3))) && *reinterpret_cast<void***>(swap3.Get()) == vtbl;
    p.resize1 = has3 ? reinterpret_cast<ResizeBuffers1Fn>(vtbl[kResizeBuffers1]) : nullptr;
    // Publish the originals before any present can reach a hook.
    g_vtableCount.store(n + 1, std::memory_order_release);
    bool ok = WriteSlot(&vtbl[kPresent], reinterpret_cast<void*>(&HookPresent)) &&
              WriteSlot(&vtbl[kPresent1], reinterpret_cast<void*>(&HookPresent1)) &&
              WriteSlot(&vtbl[kResizeBuffers], reinterpret_cast<void*>(&HookResizeBuffers));
    if (ok && has3)
    {
        ok = WriteSlot(&vtbl[kResizeBuffers1], reinterpret_cast<void*>(&HookResizeBuffers1));
    }
    return ok;
}

HWND CreateDummyWindow()
{
    return CreateWindowExW(0, L"STATIC", L"lookback-hook-dummy", WS_POPUP, 0, 0, 8, 8, nullptr, nullptr, g_module, nullptr);
}

DXGI_SWAP_CHAIN_DESC1 DummyDesc(UINT buffers)
{
    DXGI_SWAP_CHAIN_DESC1 d{};
    d.Width = 8;
    d.Height = 8;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.SampleDesc.Count = 1;
    d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    d.BufferCount = buffers;
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    return d;
}

using CreateFactory2Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
using D3D11CreateDeviceFn = decltype(&D3D11CreateDevice);
using D3D12CreateDeviceFn = HRESULT(WINAPI*)(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);

bool PatchFromD3D11(HWND hwnd, IDXGIFactory2* factory)
{
    HMODULE d3d11 = LoadLibraryW(L"d3d11.dll");
    auto create = d3d11 ? reinterpret_cast<D3D11CreateDeviceFn>(GetProcAddress(d3d11, "D3D11CreateDevice")) : nullptr;
    if (create == nullptr)
    {
        return false;
    }
    ComPtr<ID3D11Device> device;
    HRESULT hr = create(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, nullptr);
    if (FAILED(hr))
    {
        hr = create(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, nullptr);
    }
    if (FAILED(hr))
    {
        return false;
    }
    DXGI_SWAP_CHAIN_DESC1 d = DummyDesc(2);
    ComPtr<IDXGISwapChain1> swap;
    if (FAILED(factory->CreateSwapChainForHwnd(device.Get(), hwnd, &d, nullptr, nullptr, &swap)))
    {
        return false;
    }
    return PatchSwapChain(swap.Get());
}

bool PatchFromD3D12(HWND hwnd, IDXGIFactory2* factory)
{
    HMODULE d3d12 = GetModuleHandleW(L"d3d12.dll"); // only when the app already uses D3D12
    auto create = d3d12 ? reinterpret_cast<D3D12CreateDeviceFn>(GetProcAddress(d3d12, "D3D12CreateDevice")) : nullptr;
    if (create == nullptr)
    {
        return false;
    }
    ComPtr<ID3D12Device> device;
    if (FAILED(create(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device))))
    {
        return false;
    }
    D3D12_COMMAND_QUEUE_DESC qd{};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ComPtr<ID3D12CommandQueue> queue;
    if (FAILED(device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue))))
    {
        return false;
    }
    void** qv = *reinterpret_cast<void***>(queue.Get());
    if (g_queueVtbl == nullptr)
    {
        g_ecl.store(reinterpret_cast<ExecuteCommandListsFn>(qv[kExecuteCommandLists]), std::memory_order_release);
        if (WriteSlot(&qv[kExecuteCommandLists], reinterpret_cast<void*>(&HookExecuteCommandLists)))
        {
            g_queueVtbl = qv;
        }
    }
    DXGI_SWAP_CHAIN_DESC1 d = DummyDesc(2);
    ComPtr<IDXGISwapChain1> swap;
    if (FAILED(factory->CreateSwapChainForHwnd(queue.Get(), hwnd, &d, nullptr, nullptr, &swap)))
    {
        return false;
    }
    return PatchSwapChain(swap.Get());
}

bool HostIsTestApp()
{
    auto marker = reinterpret_cast<const char*>(GetProcAddress(GetModuleHandleW(nullptr), LBH_TESTAPP_MARKER_EXPORT));
    if (marker == nullptr)
    {
        return false;
    }
    __try
    {
        return std::strncmp(marker, LBH_TESTAPP_MARKER_VALUE, sizeof(LBH_TESTAPP_MARKER_VALUE)) == 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

DWORD WINAPI WatchThread(LPVOID)
{
    while (!g_exiting.load())
    {
        Sleep(100);
        g_d3d11.Watch();
        g_d3d12.Watch();
        ForgetRecentQueue(true); // a queue cached after the device was removed would keep it alive
    }
    return 0;
}

void Unload(DWORD code)
{
    FreeLibraryAndExitThread(g_module, code);
}

DWORD WINAPI InitThread(LPVOID)
{
    QueryPerformanceFrequency(&g_freq);
    DWORD pid = GetCurrentProcessId();
    if (!g_channel.Open(pid))
    {
        Unload(1); // no receiver asked for this process: do nothing at all
    }
    lbh_header* h = g_channel.Header();
    if (h->magic != LBH_MAGIC || h->version != LBH_VERSION || h->header_size != sizeof(lbh_header) ||
        h->ring_capacity != LBH_RING_CAPACITY || h->target_pid != pid)
    {
        g_channel.SetState(LBH_STATE_REFUSED, "channel does not match this hook build (protocol version or layout)");
        g_channel.Close();
        Unload(2);
    }
    if (!HostIsTestApp())
    {
        g_channel.SetState(LBH_STATE_REFUSED, "host process is not a Lookback test app (no LookbackTestAppMarker export)");
        g_channel.Close();
        Unload(3);
    }
    h->hook_version = LBH_VERSION;
    h->qpc_frequency = g_freq.QuadPart;

    HMODULE dxgi = LoadLibraryW(L"dxgi.dll");
    auto createFactory = dxgi ? reinterpret_cast<CreateFactory2Fn>(GetProcAddress(dxgi, "CreateDXGIFactory2")) : nullptr;
    ComPtr<IDXGIFactory2> factory;
    HWND hwnd = CreateDummyWindow();
    if (createFactory == nullptr || FAILED(createFactory(0, IID_PPV_ARGS(&factory))) || hwnd == nullptr)
    {
        g_channel.SetState(LBH_STATE_ERROR, "could not create a DXGI factory or the dummy window");
        Unload(4);
    }
    bool patched = false;
    if (GetModuleHandleW(L"d3d12.dll") != nullptr)
    {
        patched |= PatchFromD3D12(hwnd, factory.Get());
    }
    if (GetModuleHandleW(L"d3d11.dll") != nullptr || !patched)
    {
        patched |= PatchFromD3D11(hwnd, factory.Get());
    }
    DestroyWindow(hwnd);
    if (!patched)
    {
        g_channel.SetState(LBH_STATE_ERROR, "could not create a dummy swap chain to find the vtable");
        if (g_vtableCount.load() > 0 || g_queueVtbl != nullptr)
        {
            return 1; // some entries already point into this DLL: it must stay loaded
        }
        Unload(5);
    }
    g_installed.store(true);
    HANDLE watch = CreateThread(nullptr, 0, WatchThread, nullptr, 0, nullptr);
    if (watch != nullptr)
    {
        CloseHandle(watch);
    }
    g_channel.SetState(LBH_STATE_INSTALLED, "");
    return 0; // the DLL stays loaded: patched vtables point into it
}

} // namespace

Channel& TheChannel() { return g_channel; }

ID3D12CommandQueue* RecentDirectQueue(ID3D12Device* device)
{
    ID3D12CommandQueue* result = nullptr;
    if (TryAcquireSRWLockShared(&g_queueLock))
    {
        if (g_recentQueue != nullptr)
        {
            ComPtr<ID3D12Device> d;
            if (SUCCEEDED(g_recentQueue->GetDevice(IID_PPV_ARGS(&d))) && d.Get() == device)
            {
                result = g_recentQueue;
                result->AddRef();
            }
        }
        ReleaseSRWLockShared(&g_queueLock);
    }
    return result;
}

void ForgetRecentQueue(bool onlyIfRemoved)
{
    AcquireSRWLockExclusive(&g_queueLock);
    bool forget = g_recentQueue != nullptr;
    if (forget && onlyIfRemoved)
    {
        ComPtr<ID3D12Device> d;
        forget = FAILED(g_recentQueue->GetDevice(IID_PPV_ARGS(&d))) || d->GetDeviceRemovedReason() != S_OK;
    }
    if (forget)
    {
        g_recentQueue->Release();
        g_recentQueue = nullptr;
    }
    ReleaseSRWLockExclusive(&g_queueLock);
}
D3D11Capture& TheD3D11() { return g_d3d11; }
D3D12Capture& TheD3D12() { return g_d3d12; }
int64_t QpcFrequency() { return g_freq.QuadPart; }

void ReportError(const char* what, HRESULT hr)
{
    char text[LBH_STATUS_BYTES];
    std::snprintf(text, sizeof(text), "%s failed (hr 0x%08lX)", what, static_cast<unsigned long>(hr));
    g_channel.SetStatus(text);
}

// ---- Swap chain tracking -----------------------------------------------------

namespace
{
std::atomic<IDXGISwapChain*> g_tracked{nullptr};
std::atomic<int64_t> g_trackedQpc{0};
}

bool IsTrackedSwapChain(IDXGISwapChain* swap, int64_t qpc, bool& switched)
{
    switched = false;
    IDXGISwapChain* cur = g_tracked.load();
    if (cur == swap)
    {
        g_trackedQpc.store(qpc);
        return true;
    }
    // Another swap chain presents: adopt it once the tracked one has been
    // quiet for 250 ms (recreated after a device loss, or a new window).
    if (cur == nullptr || qpc - g_trackedQpc.load() > QpcFrequency() / 4)
    {
        g_tracked.store(swap);
        g_trackedQpc.store(qpc);
        switched = true;
        return true;
    }
    return false;
}

// ---- Channel -------------------------------------------------------------------

bool Channel::Open(DWORD pid)
{
    wchar_t name[96];
    swprintf_s(name, LBH_CHANNEL_FORMAT, static_cast<unsigned long>(pid));
    mapping_ = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    if (mapping_ == nullptr)
    {
        return false;
    }
    header_ = static_cast<lbh_header*>(MapViewOfFile(mapping_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(lbh_header)));
    swprintf_s(name, LBH_FRAME_EVENT_FORMAT, static_cast<unsigned long>(pid));
    frameEvent_ = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
    if (header_ == nullptr || frameEvent_ == nullptr)
    {
        Close();
        return false;
    }
    return true;
}

void Channel::Close()
{
    if (header_ != nullptr)
    {
        UnmapViewOfFile(header_);
        header_ = nullptr;
    }
    if (frameEvent_ != nullptr)
    {
        CloseHandle(frameEvent_);
        frameEvent_ = nullptr;
    }
    if (mapping_ != nullptr)
    {
        CloseHandle(mapping_);
        mapping_ = nullptr;
    }
}

void Channel::SetStatus(const char* status)
{
    if (header_ == nullptr)
    {
        return;
    }
    // Racy against a concurrent reader by design: status is diagnostic text.
    strncpy_s(header_->status, status, _TRUNCATE);
}

void Channel::SetState(lbh_state state, const char* status)
{
    if (header_ == nullptr)
    {
        return;
    }
    if (status != nullptr && status[0] != 0)
    {
        SetStatus(status);
    }
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&header_->state), state);
}

int64_t Channel::BeginPresent(int64_t qpc)
{
    InterlockedExchange64(&header_->heartbeat_qpc, qpc);
    return InterlockedIncrement64(&header_->presents) - 1;
}

void Channel::Drop(DropKind kind)
{
    switch (kind)
    {
    case DropKind::Busy: InterlockedIncrement64(&header_->dropped_busy); break;
    case DropKind::NotReady: InterlockedIncrement64(&header_->dropped_not_ready); break;
    case DropKind::Device: InterlockedIncrement64(&header_->dropped_device); break;
    }
    InterlockedIncrement64(&header_->dropped);
}

int64_t Channel::Dropped() const { return header_->dropped; }

int64_t Channel::NextRecord() const { return header_->write_index; }

void Channel::BeginConfig()
{
    InterlockedIncrement64(&header_->config_seq); // odd: being changed
}

void Channel::EndConfig()
{
    InterlockedIncrement64(&header_->config_seq); // even: stable
}

void Channel::Publish(uint32_t slot, int64_t frame, int64_t qpc, int64_t dropped, uint64_t fenceValue)
{
    int64_t record = header_->write_index;
    lbh_frame& f = header_->ring[record % LBH_RING_CAPACITY];
    InterlockedExchange64(&f.seq, 2 * record + 1);
    f.record = record;
    f.frame = frame;
    f.qpc = qpc;
    f.dropped = dropped;
    f.fence_value = fenceValue;
    f.slot = slot;
    f.generation = header_->generation;
    f.width = header_->width;
    f.height = header_->height;
    f.format = header_->format;
    InterlockedExchange64(&f.seq, 2 * record + 2);
    InterlockedExchange64(&header_->write_index, record + 1);
    if (header_->state != LBH_STATE_CAPTURING)
    {
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&header_->state), LBH_STATE_CAPTURING);
    }
    SetEvent(frameEvent_);
}

DXGI_FORMAT SharedFormat(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
        return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
        return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_UNORM:
    case DXGI_FORMAT_R10G10B10A2_TYPELESS:
        return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_FLOAT:
    case DXGI_FORMAT_R16G16B16A16_TYPELESS:
        return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default:
        return DXGI_FORMAT_UNKNOWN;
    }
}

} // namespace lbh

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        lbh::g_module = module;
        DisableThreadLibraryCalls(module);
        // D3D work cannot run under the loader lock: initialise on a thread.
        HANDLE t = CreateThread(nullptr, 0, lbh::InitThread, nullptr, 0, nullptr);
        if (t == nullptr)
        {
            return FALSE;
        }
        CloseHandle(t);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        lbh::g_exiting.store(true);
        if (reserved != nullptr && lbh::g_installed.load())
        {
            lbh::g_channel.SetState(LBH_STATE_EXITING, "target process is exiting");
        }
    }
    return TRUE;
}
