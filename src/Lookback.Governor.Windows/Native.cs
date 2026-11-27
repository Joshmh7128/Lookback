using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lookback.Governor.Windows;

/// <summary>The kernel32 and user32 calls the probe makes. All are readable by a normal user.</summary>
internal static unsafe partial class Native
{
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const int StorageDeviceProperty = 0;
    public const int StorageDeviceSeekPenaltyProperty = 7;
    public const int PropertyStandardQuery = 0;
    public const uint FileShareReadWrite = 0x1 | 0x2;
    public const uint OpenExisting = 3;
    public const uint SemFailCriticalErrors = 0x1;
    public const int EnumCurrentSettings = -1;

    /// <summary>sizeof(DEVMODEW); the fields read are at fixed offsets.</summary>
    public const int DevModeSize = 220;
    public const int DevModeSizeOffset = 68;
    public const int DevModePelsWidthOffset = 172;
    public const int DevModePelsHeightOffset = 176;
    public const int DevModeFrequencyOffset = 184;

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public int AdditionalParameters; // BYTE[1], padded to 12 bytes like the C struct
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string fileName, char* volumePath, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* volumeName, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadErrorMode(uint mode, out uint oldMode);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplaySettings(string deviceName, int modeNum, byte* devMode);
}
