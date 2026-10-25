using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TxtReader.Core;

/// <summary>Small BCL backports shared by the current and .NET Framework readers.</summary>
public static class RuntimeCompat
{
    public static int Clamp(int value, int min, int max)
    {
        if (min > max) throw new ArgumentException("The minimum exceeds the maximum.");
        return Math.Min(Math.Max(value, min), max);
    }

    public static double Clamp(double value, double min, double max)
    {
        if (min > max) throw new ArgumentException("The minimum exceeds the maximum.");
        return Math.Min(Math.Max(value, min), max);
    }

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    public static TimeSpan ElapsedSince(long timestamp) =>
        TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency);

    public static string Sha256Hex(byte[] value)
    {
        using var algorithm = SHA256.Create();
        return Hex(algorithm.ComputeHash(value));
    }

    public static string Sha256Hex(Stream value)
    {
        using var algorithm = SHA256.Create();
        return Hex(algorithm.ComputeHash(value));
    }

    private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "");

    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var result = new byte[checked((int)stream.Length)];
        int read = 0;
        while (read < result.Length)
        {
            int count = await stream.ReadAsync(result, read, result.Length - read, token).ConfigureAwait(false);
            if (count == 0) { Array.Resize(ref result, read); break; }
            read += count;
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    public static Task<string> ReadAllTextAsync(string path) => Task.Run(() => File.ReadAllText(path));
    public static Task WriteAllTextAsync(string path, string value) =>
        WriteAllTextAsync(path, value, new UTF8Encoding(false));
    public static Task WriteAllTextAsync(string path, string value, Encoding encoding) =>
        Task.Run(() => File.WriteAllText(path, value, encoding));
}
