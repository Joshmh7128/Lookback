// Internals shared by the hook's translation units: the channel (shared
// memory + frame event) and the two capture back ends.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <d3d12.h>
#include <dxgi1_4.h>

#include <cstdint>

#include "lookback_hook_protocol.h"

namespace lbh
{

enum class DropKind
{
    Busy,
    NotReady,
    Device,
};

// The receiver-created channel for this process. Every method is lock-free
// and never waits: they run on the game's present thread.
class Channel
{
public:
    bool Open(DWORD pid);
    void Close();
    lbh_header* Header() const { return header_; }

    void SetState(lbh_state state, const char* status);
    void SetStatus(const char* status);

    // Counts a present of the tracked swap chain and returns its frame number.
    int64_t BeginPresent(int64_t qpc);
    void Drop(DropKind kind);
    int64_t Dropped() const;
    int64_t NextRecord() const;

    // Seqlock around shared-texture configuration changes.
    void BeginConfig();
    void EndConfig();

    // Appends a ring record and wakes the receiver. Call after the slot's
    // contents and metadata are written and the slot has been handed over.
    void Publish(uint32_t slot, int64_t frame, int64_t qpc, int64_t dropped, uint64_t fenceValue);

private:
    HANDLE mapping_ = nullptr;
    HANDLE frameEvent_ = nullptr;
    lbh_header* header_ = nullptr;
};

Channel& TheChannel();

// Formats the hook can hand over, mapped to the typed format of the shared
// texture (same type group, so CopyResource accepts the pair). UNKNOWN when
// unsupported.
DXGI_FORMAT SharedFormat(DXGI_FORMAT backBuffer);

// One back end per API. OnPresent runs on the present thread and must not
// block; Watch runs on the hook's watcher thread and may wait briefly.
class D3D11Capture
{
public:
    // True if this swap chain is a D3D11 one (then the present was handled).
    bool OnPresent(IDXGISwapChain* swap, int64_t qpc);
    void Watch();

private:
    void ReleaseLocked();
    bool CreateLocked(UINT width, UINT height, DXGI_FORMAT format);
    struct State;
    State* s_ = nullptr;
    SRWLOCK lock_ = SRWLOCK_INIT;
};

class D3D12Capture
{
public:
    bool OnPresent(IDXGISwapChain* swap, int64_t qpc);
    // Before ResizeBuffers: waits (at most timeoutMs) for copies that still read the back buffers.
    void BeforeResize(IDXGISwapChain* swap, DWORD timeoutMs);
    void Watch();

private:
    bool ReleaseLocked(bool force);
    bool CreateLocked(UINT width, UINT height, DXGI_FORMAT format);
    struct State;
    State* s_ = nullptr;
    SRWLOCK lock_ = SRWLOCK_INIT;
};

// The most recent DIRECT queue that called ExecuteCommandLists (a D3D12 swap
// chain does not hand out its queue). Returns an AddRef'd queue on the given
// device, or null; never blocks.
ID3D12CommandQueue* RecentDirectQueue(ID3D12Device* device);
// Drops the cached queue (watcher thread, after a device removal); with
// onlyIfRemoved, only when its device has been removed.
void ForgetRecentQueue(bool onlyIfRemoved = false);

D3D11Capture& TheD3D11();
D3D12Capture& TheD3D12();

// Tracks which swap chain is captured: the first one that presents, until it
// has been silent for a while (a game that recreated its swap chain).
bool IsTrackedSwapChain(IDXGISwapChain* swap, int64_t qpc, bool& switched);

int64_t QpcFrequency();

void ReportError(const char* what, HRESULT hr);

} // namespace lbh
