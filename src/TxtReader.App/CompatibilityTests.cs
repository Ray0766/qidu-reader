using System.IO;
using System.Text;
using TxtReader.Core;

namespace TxtReader.App;

internal static class CompatibilityTests
{
    internal static async Task RunAsync(string folder, Action<bool, string, object?> check)
    {
        void Check(bool value, string name) => check(value, "runtime compatibility: " + name, null);
        // This independent SHA-256 vector fixes the persisted book identity across runtimes.
        const string original = "\uFEFF第一章 春水\r\n林舟与🙂。\r第二章 风声\n续读";
        const string identity = "0446834EEDCB0DD62623A1A2BB8BB811DA1B927CB37A2258D09DBDEB569069AB";
        var book = new BookDocument("书名", original);
        Check(book.ContentId == identity, "normalized Chinese and surrogate text retains existing book ID");
        string path = Path.Combine(folder, "编码 #100% 🙂.txt");
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, true) })
        {
            File.WriteAllText(path, original, encoding);
            Check((await BookLoader.LoadAsync(path)).ContentId == identity, "same content ID through " + encoding.WebName + " BOM " + encoding.GetPreamble().Length);
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool observed = false;
        try { await BookLoader.LoadAsync(path, token: canceled.Token); }
        catch (OperationCanceledException) { observed = true; }
        Check(observed, "canceled import cannot return a loaded book");
        Check(WindowsCompat.GetRelativePath(folder, path) == Path.GetFileName(path), "relative paths preserve literal Chinese percent hash and surrogate characters");
        Check(!WindowsCompat.IsPathFullyQualified(@"C:relative.txt") && !WindowsCompat.IsPathFullyQualified(@"\rooted.txt") &&
            WindowsCompat.IsPathFullyQualified(@"C:\books\a.txt") && WindowsCompat.IsPathFullyQualified(@"\\server\share\a.txt"),
            "drive relative paths cannot be mistaken for absolute library paths");
    }
}
