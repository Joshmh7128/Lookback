// Shared-memory protocol between the Lookback present hook (inside one of our
// test apps) and the recorder (HookCaptureSource). Mirrored field for field in
// src/Lookback.Capture/Hook/HookProtocol.cs; change both together and bump
// LBH_VERSION. Every field is naturally aligned; the receiver writes
// sizeof(lbh_header) into header_size and the hook refuses on a mismatch.
//
// Who writes what:
//   receiver: magic, version, header_size, ring_capacity, target_pid, receiver_pid
//             (before the hook loads), slots[].state 1 -> 0/2 transitions on D3D12
//   hook:     everything else
//
// Names (Local\ namespace, per target process id):
//   Local\Lookback.Hook.<pid>.Channel   file mapping holding one lbh_header
//   Local\Lookback.Hook.<pid>.Frame     auto-reset event, set after each published frame
// The receiver creates both before injecting; the hook refuses to run without them.
#pragma once

#include <stdint.h>

#define LBH_MAGIC 0x4B48424Cu /* "LBHK" little-endian */
#define LBH_VERSION 1u
#define LBH_RING_CAPACITY 64u
#define LBH_MAX_SLOTS 3u
#define LBH_STATUS_BYTES 256u

#define LBH_CHANNEL_FORMAT L"Local\\Lookback.Hook.%lu.Channel"
#define LBH_FRAME_EVENT_FORMAT L"Local\\Lookback.Hook.%lu.Frame"

// The export every test app carries and the hook and injector both check.
#define LBH_TESTAPP_MARKER_EXPORT "LookbackTestAppMarker"
#define LBH_TESTAPP_MARKER_VALUE "lookback-testapp/1"

enum lbh_state
{
    LBH_STATE_WAITING = 0,   // channel created, hook not loaded yet
    LBH_STATE_INSTALLED = 1, // vtables patched, no frame yet
    LBH_STATE_CAPTURING = 2, // at least one frame published
    LBH_STATE_REFUSED = 3,   // the hook declined to install (see status)
    LBH_STATE_ERROR = 4,     // installation failed (see status)
    LBH_STATE_EXITING = 5,   // the target process is shutting down
};

enum lbh_api
{
    LBH_API_NONE = 0,
    LBH_API_D3D11 = 1,
    LBH_API_D3D12 = 2,
};

enum lbh_sync
{
    LBH_SYNC_NONE = 0,
    LBH_SYNC_KEYED_MUTEX = 1, // D3D11: key 0 = writer may take, key 1 = reader may take
    LBH_SYNC_FENCE = 2,       // D3D12: shared fence + slots[].state ownership
};

// D3D12 slot ownership (slots[].state). D3D11 slots use the keyed mutex instead.
enum lbh_slot_state
{
    LBH_SLOT_FREE = 0,
    LBH_SLOT_WRITING = 1, // hook is recording a copy into the slot
    LBH_SLOT_READING = 2, // receiver is copying out of the slot
};

// What is in one shared texture. Written by the hook while it owns the slot
// (keyed mutex key 0/1 acquired by the hook, or state == WRITING), so a
// reader that owns the slot sees a consistent copy.
typedef struct lbh_slot
{
    volatile int32_t state; // lbh_slot_state (D3D12 only)
    uint32_t generation;    // config generation the texture belongs to
    int64_t record;         // ring record index of the frame in the texture, -1 if none
    int64_t frame;          // present number
    int64_t qpc;            // QPC at present
    int64_t dropped;        // hook drop counter when the frame was published
    uint64_t fence_value;   // D3D12: shared fence value that marks the copy complete
} lbh_slot; // 48 bytes

// One published frame. Seqlock: seq is 2*record+1 while written, 2*record+2 when complete.
typedef struct lbh_frame
{
    volatile int64_t seq;
    int64_t record;       // publish index, 0-based
    int64_t frame;        // present number, 0-based, counts every hooked present
    int64_t qpc;          // QueryPerformanceCounter at the start of Present
    int64_t dropped;      // presents not published so far (cumulative)
    uint64_t fence_value; // D3D12 only
    uint32_t slot;
    uint32_t generation;
    uint32_t width;
    uint32_t height;
    uint32_t format; // DXGI_FORMAT of the shared texture
    uint32_t reserved;
} lbh_frame; // 72 bytes

typedef struct lbh_header
{
    // Set by the receiver before injection.
    uint32_t magic;
    uint32_t version;
    uint32_t header_size;
    uint32_t ring_capacity;
    uint32_t target_pid;
    uint32_t receiver_pid;

    // Hook status.
    volatile int32_t state; // lbh_state
    uint32_t hook_version;
    int64_t qpc_frequency;
    volatile int64_t heartbeat_qpc; // QPC of the last hooked present
    volatile int64_t presents;      // hooked presents
    volatile int64_t dropped;       // presents not published (sum of the next three)
    volatile int64_t dropped_busy;  // every slot owned by the reader or still in flight
    volatile int64_t dropped_not_ready; // initialising, resized, unsupported format, no queue yet
    volatile int64_t dropped_device;    // device removed or a D3D call failed
    volatile int64_t write_index;       // records published

    // Shared-texture configuration. Seqlock: odd while the hook changes it.
    volatile int64_t config_seq;
    uint32_t generation; // bumps whenever the textures are recreated
    uint32_t api;        // lbh_api
    uint32_t sync;       // lbh_sync
    uint32_t slot_count;
    uint32_t width;
    uint32_t height;
    uint32_t format; // DXGI_FORMAT
    uint32_t queue_source; // D3D12: 1 = swap chain GetDevice, 2 = ExecuteCommandLists hook
    int64_t adapter_luid;  // LowPart | HighPart << 32
    uint64_t texture_handles[LBH_MAX_SLOTS + 1]; // NT handle values in the target process
    uint64_t fence_handle;                       // D3D12 shared fence NT handle

    char status[LBH_STATUS_BYTES]; // UTF-8, NUL-terminated: last refusal or error

    lbh_slot slots[LBH_MAX_SLOTS + 1];
    lbh_frame ring[LBH_RING_CAPACITY];
} lbh_header;

#ifdef __cplusplus
static_assert(sizeof(lbh_slot) == 48, "lbh_slot layout");
static_assert(sizeof(lbh_frame) == 72, "lbh_frame layout");
#endif

#ifdef __cplusplus
#include <cstddef>
// HookProtocol.cs asserts the same numbers (HookProtocolTests).
static_assert(sizeof(lbh_header) == 5240, "lbh_header layout");
static_assert(offsetof(lbh_header, config_seq) == 96, "lbh_header layout");
static_assert(offsetof(lbh_header, status) == 184, "lbh_header layout");
static_assert(offsetof(lbh_header, slots) == 440, "lbh_header layout");
static_assert(offsetof(lbh_header, ring) == 632, "lbh_header layout");
#endif
