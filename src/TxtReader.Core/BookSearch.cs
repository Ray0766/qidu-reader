namespace TxtReader.Core;

public sealed record SearchHit(int Offset, int Length, string Chapter, string Prefix, string Match, string Suffix);
public sealed record SearchPage(IReadOnlyList<SearchHit> Hits, bool HasMore, int NextOffset);

public static class BookSearch
{
    public const int PageSize = 200;
    private const int ScanBlockSize = 65536;

    public static Task<SearchPage> FindPageAsync(BookDocument book, string query, int startOffset = 0,
        CancellationToken cancellationToken = default, int pageSize = PageSize) =>
        Task.Run(() => FindPage(book, query, startOffset, cancellationToken, pageSize), cancellationToken);

    private static SearchPage FindPage(BookDocument book, string query, int startOffset, CancellationToken token, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        if (string.IsNullOrEmpty(query)) return new([], false, 0);
        if (query.Length > 256) throw new ArgumentException("搜索内容最多 256 个字符。", nameof(query));
        var hits = new List<SearchHit>(pageSize);
        string text = book.Text;
        int limit = text.Length - query.Length + 1;
        for (int block = Math.Clamp(startOffset, 0, text.Length); block < limit;)
        {
            token.ThrowIfCancellationRequested();
            int nextBlock = Math.Min(limit, block + ScanBlockSize);
            int scanEnd = Math.Min(text.Length, nextBlock + query.Length - 1);
            for (int position = block; position < nextBlock;)
            {
                token.ThrowIfCancellationRequested();
                int found = text.IndexOf(query, position, scanEnd - position, StringComparison.OrdinalIgnoreCase);
                if (found < 0 || found >= nextBlock) break;
                // One lookahead match proves another page exists without retaining all matches.
                if (hits.Count == pageSize) return new(hits, true, hits[^1].Offset + 1);
                int from = book.ClampOffset(Math.Max(0, found - 24));
                int to = Math.Min(text.Length, found + query.Length + 40);
                if (to < text.Length && char.IsLowSurrogate(text[to])) to++;
                string Display(string value) => value.Replace('\n', ' ');
                hits.Add(new(found, query.Length, book.ChapterAt(found).Title,
                    (from > 0 ? "…" : "") + Display(text[from..found]),
                    Display(text.Substring(found, query.Length)),
                    Display(text[(found + query.Length)..to]) + (to < text.Length ? "…" : "")));
                position = found + 1; // Include overlapping literal matches, e.g. 哈哈 in 哈哈哈.
            }
            block = nextBlock;
        }
        token.ThrowIfCancellationRequested();
        return new(hits, false, text.Length);
    }
}
