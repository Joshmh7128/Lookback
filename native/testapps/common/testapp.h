// Shared plumbing for the Lookback test apps: arguments, the window, frame
// pacing, the frame-numbered pattern and the allowlist marker. These apps are
// the only processes the present hook may ever be injected into.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shellapi.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <string>
#include <vector>

#include "lookback_hook_protocol.h"

// The marker the injector reads from the PE export table on disk and the
// hook checks with GetProcAddress inside the process. One definition per
// executable: include this header from exactly one .cpp (main.cpp).
extern "C" __declspec(dllexport) const char LookbackTestAppMarker[] = LBH_TESTAPP_MARKER_VALUE;

namespace testapp
{

struct Options
{
    int width = 640;
    int height = 360;
    double fps = 120.0;
    int64_t frames = 0;      // 0 = until the window closes
    int64_t resize_at = -1;  // resize the window at this frame
    int64_t recreate_at = -1; // tear down and recreate the device + swap chain (D3D12: RemoveDevice first)
    bool present1 = false;   // present with IDXGISwapChain1::Present1
    bool warp = false;       // software adapter
};

inline bool ParseInt(const wchar_t* s, int64_t& out)
{
    wchar_t* end = nullptr;
    long long v = std::wcstoll(s, &end, 10);
    if (end == s || *end != 0)
    {
        return false;
    }
    out = v;
    return true;
}

inline bool ParseOptions(Options& o)
{
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv == nullptr)
    {
        return false;
    }
    bool ok = true;
    for (int i = 1; i < argc && ok; i++)
    {
        std::wstring a = argv[i];
        auto next = [&](int64_t& v) {
            if (i + 1 >= argc || !ParseInt(argv[++i], v))
            {
                ok = false;
            }
        };
        int64_t v = 0;
        if (a == L"--width") { next(v); o.width = static_cast<int>(v); }
        else if (a == L"--height") { next(v); o.height = static_cast<int>(v); }
        else if (a == L"--fps") { next(v); o.fps = static_cast<double>(v); }
        else if (a == L"--frames") { next(o.frames); }
        else if (a == L"--resize-at") { next(o.resize_at); }
        else if (a == L"--recreate-at") { next(o.recreate_at); }
        else if (a == L"--present1") { o.present1 = true; }
        else if (a == L"--warp") { o.warp = true; }
        else { ok = false; }
    }
    LocalFree(argv);
    if (o.width < 64 || o.height < 64 || o.width > 8192 || o.height > 8192 || o.fps <= 0 || o.fps > 1000)
    {
        ok = false;
    }
    return ok;
}

inline void Usage(const wchar_t* name)
{
    wchar_t text[512];
    swprintf_s(text, L"%s [--width 640] [--height 360] [--fps 120] [--frames N] [--resize-at N] [--recreate-at N] [--present1] [--warp]", name);
    OutputDebugStringW(text);
    fwprintf(stderr, L"%s\n", text);
}

// ---- Window ---------------------------------------------------------------

struct Window
{
    HWND hwnd = nullptr;
    bool closed = false;
    bool resized = false;
};

inline Window*& CurrentWindow()
{
    static Window* w = nullptr;
    return w;
}

inline LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    Window* w = CurrentWindow();
    switch (msg)
    {
    case WM_SIZE:
        if (w != nullptr)
        {
            w->resized = true;
        }
        return 0;
    case WM_CLOSE:
        if (w != nullptr)
        {
            w->closed = true;
        }
        DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProcW(hwnd, msg, wp, lp);
    }
}

inline bool CreateAppWindow(Window& w, const wchar_t* title, int width, int height)
{
    WNDCLASSEXW wc{sizeof(wc)};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.lpszClassName = L"LookbackTestApp";
    RegisterClassExW(&wc);
    RECT r{0, 0, width, height};
    AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW, FALSE);
    CurrentWindow() = &w;
    w.hwnd = CreateWindowExW(0, wc.lpszClassName, title, WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
                             r.right - r.left, r.bottom - r.top, nullptr, nullptr, wc.hInstance, nullptr);
    return w.hwnd != nullptr;
}

inline void ShowAppWindow(Window& w)
{
    ShowWindow(w.hwnd, SW_SHOWNOACTIVATE);
    w.resized = false;
}

// Drains the message queue; false once the window has closed.
inline bool Pump(Window& w)
{
    MSG m;
    while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE))
    {
        if (m.message == WM_QUIT)
        {
            w.closed = true;
        }
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    return !w.closed;
}

inline void ResizeClient(Window& w, int width, int height)
{
    RECT r{0, 0, width, height};
    AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW, FALSE);
    SetWindowPos(w.hwnd, nullptr, 0, 0, r.right - r.left, r.bottom - r.top, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
}

inline void ClientSize(const Window& w, int& width, int& height)
{
    RECT r{};
    GetClientRect(w.hwnd, &r);
    width = r.right - r.left > 0 ? r.right - r.left : 1;
    height = r.bottom - r.top > 0 ? r.bottom - r.top : 1;
}

// ---- Pacing ---------------------------------------------------------------

class Pacer
{
public:
    explicit Pacer(double fps)
    {
        QueryPerformanceFrequency(&freq_);
        QueryPerformanceCounter(&origin_);
        period_ = static_cast<int64_t>(static_cast<double>(freq_.QuadPart) / fps);
        timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    }
    ~Pacer()
    {
        if (timer_ != nullptr)
        {
            CloseHandle(timer_);
        }
    }
    Pacer(const Pacer&) = delete;
    Pacer& operator=(const Pacer&) = delete;

    // Waits until frame n's deadline (origin + n periods); skips ahead if far behind.
    void Wait(int64_t n)
    {
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        int64_t deadline = origin_.QuadPart + (n - skipped_) * period_;
        if (now.QuadPart - deadline > 4 * period_)
        {
            skipped_ = n - (now.QuadPart - origin_.QuadPart) / period_;
            return;
        }
        int64_t wait = deadline - now.QuadPart;
        if (wait <= 0)
        {
            return;
        }
        if (timer_ != nullptr)
        {
            LARGE_INTEGER due;
            due.QuadPart = -(wait * 10'000'000 / freq_.QuadPart); // relative, 100 ns units
            if (due.QuadPart < 0 && SetWaitableTimer(timer_, &due, 0, nullptr, nullptr, FALSE))
            {
                WaitForSingleObject(timer_, 1000);
                return;
            }
        }
        Sleep(static_cast<DWORD>(wait * 1000 / freq_.QuadPart));
    }

private:
    LARGE_INTEGER freq_{};
    LARGE_INTEGER origin_{};
    int64_t period_ = 1;
    int64_t skipped_ = 0;
    HANDLE timer_ = nullptr;
};

// ---- Pattern ---------------------------------------------------------------
//
// Each frame shows: a background that slowly cycles, a white vertical bar that
// moves 8 px per frame (below the bit strip), and the frame number as 32 bits
// on a black strip in the top-left corner: bit i (LSB first) is the 8x8 block
// at x = 8 + 8i, y = 8, white for 1 and black for 0. HookTests decodes these
// blocks from the captured texture and checks them against the hook's frame
// number.

constexpr int kBitBlock = 8;
constexpr int kBitOrigin = 8;
constexpr int kBits = 32;


struct Pattern
{
    float background[4];
    std::vector<RECT> ones;   // white bit blocks plus the moving bar
    RECT strip;               // black strip behind the bits
};

inline void BuildPattern(int64_t frame, int width, int height, Pattern& p)
{
    float t = static_cast<float>(frame % 600) / 600.0f;
    p.background[0] = 0.15f + 0.25f * t;
    p.background[1] = 0.10f;
    p.background[2] = 0.35f - 0.25f * t;
    p.background[3] = 1.0f;
    p.strip = RECT{0, 0, kBitOrigin * 2 + kBits * kBitBlock, kBitOrigin * 2 + kBitBlock};
    p.ones.clear();
    auto n = static_cast<uint32_t>(frame);
    for (int i = 0; i < kBits; i++)
    {
        if ((n >> i) & 1u)
        {
            LONG x = kBitOrigin + i * kBitBlock;
            p.ones.push_back(RECT{x, kBitOrigin, x + kBitBlock, kBitOrigin + kBitBlock});
        }
    }
    LONG top = p.strip.bottom + 8;
    if (top < height)
    {
        LONG x = static_cast<LONG>((frame * 8) % (width > 16 ? width - 16 : 1));
        p.ones.push_back(RECT{x, top, x + 16, static_cast<LONG>(height)});
    }
}

} // namespace testapp
