using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;

namespace Lookback.Recorder;

/// <summary>
/// A footage file's fingerprint: SHA-256 over its length and its first and
/// last MiB, as lower-case hex. Quick to take on a file of any size, stable
/// when the file is moved or renamed, and different for different
/// recordings. It goes on a footage session's header and on its ratings, so
/// the rating backfill can tell whether a file is the footage a rating came
/// from. It names no path and holds nothing of the video.
/// </summary>
internal static class FootageDigest
{
    public const int ChunkBytes = 1 << 20;

    /// <summary>The fingerprint of the file at <paramref name="path"/>.</summary>
    public static string Of(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = file.Length;
        sha.AppendData(System.Text.Encoding.ASCII.GetBytes(length.ToString(CultureInfo.InvariantCulture) + "\n"));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            Read(file, 0, (int)Math.Min(ChunkBytes, length), buffer, sha);
            if (length > ChunkBytes)
            {
                long tail = Math.Max(ChunkBytes, length - ChunkBytes);
                Read(file, tail, (int)(length - tail), buffer, sha);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>The fingerprint, or null when the file cannot be read.</summary>
    public static string? TryOf(string path)
    {
        try
        {
            return Of(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Read(FileStream file, long at, int count, byte[] buffer, IncrementalHash sha)
    {
        file.Position = at;
        int done = 0;
        while (done < count)
        {
            int n = file.Read(buffer, done, count - done);
            if (n == 0)
            {
                break;
            }

            done += n;
        }

        sha.AppendData(buffer, 0, done);
    }
}
