using System.Diagnostics;
using System.IO;
using System.Text;

namespace TxtReader.App;

internal static class WindowsCompat
{
    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    internal static int ProcessId
    {
        get { using var process = Process.GetCurrentProcess(); return process.Id; }
    }
    internal static string? ProcessPath
    {
        get { using var process = Process.GetCurrentProcess(); return process.MainModule?.FileName; }
    }

    internal static bool IsPathFullyQualified(string path)
    {
#if NETFRAMEWORK
        return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/') || path.StartsWith(@"\\", StringComparison.Ordinal);
#else
        return Path.IsPathFullyQualified(path);
#endif
    }

    internal static string GetRelativePath(string relativeTo, string path)
    {
#if NETFRAMEWORK
        string from = Path.GetFullPath(relativeTo), to = Path.GetFullPath(path);
        if (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) return to;
        // Work on literal path segments: URI conversion corrupts filenames containing '%' or '#'.
        string[] first = from.TrimEnd('\\', '/').Split('\\', '/');
        string[] second = to.TrimEnd('\\', '/').Split('\\', '/');
        int common = 0;
        while (common < first.Length && common < second.Length &&
            string.Equals(first[common], second[common], StringComparison.OrdinalIgnoreCase)) common++;
        var parts = Enumerable.Repeat("..", first.Length - common).Concat(second.Skip(common)).ToArray();
        return parts.Length == 0 ? "." : string.Join(Path.DirectorySeparatorChar.ToString(), parts);
#else
        return Path.GetRelativePath(relativeTo, path);
#endif
    }
}

#if NETFRAMEWORK
internal static class FrameworkExtensions
{
    internal static bool Contains(this string text, string value, StringComparison comparison) =>
        text.IndexOf(value, comparison) >= 0;

    internal static string Replace(this string text, string oldValue, string newValue, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(oldValue)) throw new ArgumentException("The old value must not be empty.", nameof(oldValue));
        var result = new StringBuilder();
        int start = 0, next;
        while ((next = text.IndexOf(oldValue, start, comparison)) >= 0)
        {
            result.Append(text, start, next - start).Append(newValue);
            start = next + oldValue.Length;
        }
        return result.Append(text, start, text.Length - start).ToString();
    }

    internal static async Task WaitAsync(this Task task, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource();
        if (await Task.WhenAny(task, Task.Delay(timeout, cancellation.Token)) != task) throw new TimeoutException();
        cancellation.Cancel();
        await task;
    }
}
#endif
