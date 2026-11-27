using System.Buffers.Binary;
using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lookback.Capture.Hook;

/// <summary>Our own test apps: the only processes the present hook may be injected into.</summary>
public enum TestApp
{
    D3D11,
    D3D12,
}

/// <summary>
/// The hard allowlist for the present hook. A target must be one of the two
/// test-app executables, by file name, inside the native build output folder
/// (checked on the final path, after symlinks and junctions), and must export
/// the <c>LookbackTestAppMarker</c> string. Games, launchers and anything with
/// anti-cheat can never pass: they are not in that folder and do not carry the
/// marker. The hook re-checks the marker from inside the process.
/// </summary>
public static class TestAppAllowlist
{
    public const string HookDllName = "lookback-hook.dll";

    private static readonly string[] AppNames = ["lookback-testapp-d3d11.exe", "lookback-testapp-d3d12.exe"];

    public static string FileName(TestApp app) => AppNames[(int)app];

    /// <summary>
    /// The native build output (<c>native/out/bin/Release</c>, found by walking up
    /// from the application folder), or null when the native parts are not built.
    /// </summary>
    public static string? FindNativeDirectory(string? startDirectory = null)
    {
        for (var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "native", "out", "bin", "Release");
            if (File.Exists(Path.Combine(candidate, HookDllName)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Null when <paramref name="executable"/> may be hooked; otherwise why not.</summary>
    public static string? Check(string executable, string nativeDirectory, out string finalPath)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(nativeDirectory);
        finalPath = "";
        string full;
        try
        {
            full = Path.GetFullPath(executable);
        }
        catch (ArgumentException e)
        {
            return $"\"{executable}\" is not a valid path: {e.Message}";
        }

        if (!File.Exists(full))
        {
            return $"\"{full}\" does not exist.";
        }

        string name = Path.GetFileName(full);
        if (!AppNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return $"\"{name}\" is not a Lookback test app; the present hook only runs on {string.Join(" and ", AppNames)}.";
        }

        finalPath = FinalPath(full);
        string hookDll = Path.Combine(nativeDirectory, HookDllName);
        if (!File.Exists(hookDll))
        {
            return $"\"{nativeDirectory}\" is not the native build output (no {HookDllName}).";
        }

        // Resolve the folder through a file in it, so junctions count the same on both sides.
        string folder = Path.GetDirectoryName(FinalPath(hookDll))!;
        if (!string.Equals(Path.GetDirectoryName(finalPath), folder, StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{finalPath}\" is not in the native test-app build output \"{folder}\".";
        }

        return HasMarker(finalPath) ? null : $"\"{finalPath}\" does not export {HookProtocol.MarkerExport} = \"{HookProtocol.MarkerValue}\".";
    }

    /// <summary>The path with every symlink and junction resolved, without the \\?\ prefix.</summary>
    internal static unsafe string FinalPath(string path)
    {
        using SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        char* buf = stackalloc char[1024];
        uint n = HookNative.GetFinalPathNameByHandle(h, buf, 1024, 0);
        if (n == 0 || n >= 1024)
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), $"Could not resolve \"{path}\".");
        }

        string s = new(buf, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + s[8..];
        }

        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// <summary>True when the PE file exports the marker string with the expected value.</summary>
    internal static bool HasMarker(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            PEHeader? header = pe.PEHeaders.PEHeader;
            if (header is null || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
            {
                return false;
            }

            DirectoryEntry exports = header.ExportTableDirectory;
            if (exports.Size < 40)
            {
                return false;
            }

            PEMemoryBlock dir = pe.GetSectionData(exports.RelativeVirtualAddress);
            ReadOnlySpan<byte> d = Span(dir, 40);
            int count = BinaryPrimitives.ReadInt32LittleEndian(d[24..]);
            int functions = BinaryPrimitives.ReadInt32LittleEndian(d[28..]);
            int names = BinaryPrimitives.ReadInt32LittleEndian(d[32..]);
            int ordinals = BinaryPrimitives.ReadInt32LittleEndian(d[36..]);
            for (int i = 0; i < count && i < 4096; i++)
            {
                int nameRva = BinaryPrimitives.ReadInt32LittleEndian(Span(pe.GetSectionData(names + (4 * i)), 4));
                if (CString(pe, nameRva, 64) != HookProtocol.MarkerExport)
                {
                    continue;
                }

                ushort ordinal = BinaryPrimitives.ReadUInt16LittleEndian(Span(pe.GetSectionData(ordinals + (2 * i)), 2));
                int valueRva = BinaryPrimitives.ReadInt32LittleEndian(Span(pe.GetSectionData(functions + (4 * ordinal)), 4));
                return CString(pe, valueRva, 64) == HookProtocol.MarkerValue;
            }

            return false;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return false;
        }
    }

    private static unsafe ReadOnlySpan<byte> Span(PEMemoryBlock block, int length)
    {
        if (block.Length < length)
        {
            throw new BadImageFormatException("Truncated export table.");
        }

        return new ReadOnlySpan<byte>(block.Pointer, length);
    }

    private static unsafe string? CString(PEReader pe, int rva, int max)
    {
        PEMemoryBlock b = pe.GetSectionData(rva);
        var s = new ReadOnlySpan<byte>(b.Pointer, Math.Min(b.Length, max));
        int n = s.IndexOf((byte)0);
        return n < 0 ? null : Encoding.ASCII.GetString(s[..n]);
    }
}
