using System.Runtime.InteropServices;

namespace Lookback.Detection.Windows;

/// <summary>
/// The Win32 calls detection needs. Every process call uses a
/// <see cref="ProcessQueryLimitedInformation"/> handle, the right Task
/// Manager uses: no <c>PROCESS_VM_READ</c>, no <c>PROCESS_VM_WRITE</c>,
/// no thread or injection rights.
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Th32csSnapProcess = 0x2;
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const uint GwOwner = 4;
    public const long WsCaption = 0x00C00000;
    public const long WsThickFrame = 0x00040000;
    public const long WsExToolWindow = 0x00000080;
    public const uint MonitorDefaultToNearest = 2;
    public const uint DwmwaCloaked = 14;
    public const int ProcessCommandLineInformation = 60;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    public const int QunsRunningD3dFullScreen = 3;

    public static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessId;
        public nint Th32DefaultHeapId;
        public uint Th32ModuleId;
        public uint CntThreads;
        public uint Th32ParentProcessId;
        public int PcPriClassBase;
        public uint DwFlags;
        public fixed char SzExeFile[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public uint CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint flags, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32First(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32Next(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>
    /// With <see cref="ProcessCommandLineInformation"/> (Windows 8.1+) the
    /// kernel returns the launch arguments to a query-limited handle; the
    /// caller never reads the target's memory.
    /// </summary>
    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(nint process, int infoClass, void* info, uint length, out uint returned);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(nint hwnd, char* text, int max);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);

    public const uint QdcOnlyActivePaths = 2;
    public const int DisplayConfigDeviceInfoGetSourceName = 1;
    public const int PathInfoSize = 72;
    public const int ModeInfoSize = 64;

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfoEx
    {
        public uint CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
        public fixed char SzDevice[32];
    }

    /// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME: header (type, size, adapter LUID, source id), then the GDI device name.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SourceDeviceName
    {
        public int Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
        public fixed char ViewGdiDeviceName[32];
    }

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoEx(nint monitor, MonitorInfoEx* info);

    /// <summary>
    /// The display-config (CCD) API. Unlike creating a DXGI factory, it loads
    /// no graphics drivers into this process, so it does not stall an encoder
    /// running on the same GPU.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [LibraryImport("user32.dll")]
    public static partial int QueryDisplayConfig(uint flags, ref uint paths, byte* pathInfo, ref uint modes, byte* modeInfo, nint topology);

    [LibraryImport("user32.dll")]
    public static partial int DisplayConfigGetDeviceInfo(SourceDeviceName* request);
}
