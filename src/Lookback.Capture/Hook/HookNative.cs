using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lookback.Capture.Hook;

/// <summary>The kernel32 calls the injector and the hook channel need.</summary>
internal static unsafe partial class HookNative
{
    public const uint CreateSuspended = 0x00000004;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint PageReadWrite = 0x04;
    public const uint FileMapAllAccess = 0x000F001F;
    public const uint MemCommit = 0x1000;
    public const uint MemReserve = 0x2000;
    public const uint MemRelease = 0x8000;
    public const uint DuplicateSameAccess = 0x2;
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 0x102;
    public const uint StillActive = 259;
    public const int ErrorAlreadyExists = 183;
    public const uint WmClose = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public uint Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow;
        public ushort Reserved2Count;
        public nint Reserved2;
        public nint StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcess(char* applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, char* currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocationType, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteProcessMemory(SafeProcessHandle process, nint address, void* buffer, nuint size, out nuint written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateRemoteThread(SafeProcessHandle process, nint attributes, nuint stackSize, nint startAddress, nint parameter, uint flags, out uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeThread(nint thread, out uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint GetModuleHandle(string name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial nint GetProcAddress(nint module, string name);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateFileMapping(nint file, nint attributes, uint protect, uint maxSizeHigh, uint maxSizeLow, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(nint address);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateEvent(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(SafeProcessHandle sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint size, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
