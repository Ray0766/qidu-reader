using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TxtReader.Core;

public sealed record BookImage(string Name, string MediaType, byte[] Data);
public sealed record TextBlockInfo(int Start, int Length, bool Continuation, bool Heading, BookImage? Image = null, bool PageBreakBefore = false);
public sealed record Chapter(string Title, int Offset);
public sealed record TextChunk(int FirstBlock, int BlockCount, int Start, int End);

public sealed class BookDocument
{
    public string Title { get; }
    public string Text { get; }
    public string ContentId { get; }
    public string EncodingName { get; }
    public string? SourcePath { get; }
    public IReadOnlyList<TextBlockInfo> Blocks { get; }
    public IReadOnlyList<TextChunk> Chunks { get; }
    public IReadOnlyList<Chapter> Chapters { get; }
    public IReadOnlyDictionary<int, BookImage> Images { get; }
    public BookImage? Cover { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsEpub => EncodingName == "EPUB";

    private static readonly Regex ChapterPattern = new(
        @"^(?:第[零〇一二三四五六七八九十百千万两壹贰叁肆伍陆柒捌玖拾\d]+[章回卷部节集]|序章|序言|前言|楔子|引子|尾声|后记|番外|终章|Chapter\s+\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public BookDocument(string title, string text, string encodingName = "UTF-8", string? sourcePath = null,
        IReadOnlyList<Chapter>? navigation = null, IReadOnlyDictionary<int, BookImage>? images = null,
        ISet<int>? headings = null, ISet<int>? pageBreaks = null, BookImage? cover = null, IReadOnlyList<string>? warnings = null)
    {
        Title = title;
        Text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');
        EncodingName = encodingName;
        SourcePath = sourcePath;
        Images = images ?? new Dictionary<int, BookImage>();
        Cover = cover;
        Warnings = warnings ?? Array.Empty<string>();
        // Keep all existing TXT IDs unchanged. EPUB IDs include illustration bytes so
        // two illustrated editions cannot accidentally share positions or covers.
        string identityText = IsEpub ? "EPUB-v1\n" + Text + "\n" + string.Join("\n",
            Images.OrderBy(x => x.Key).Select(x => x.Key + ":" + RuntimeCompat.Sha256Hex(x.Value.Data))) +
            (Cover is null ? "" : "\ncover:" + RuntimeCompat.Sha256Hex(Cover.Data)) : Text;
        ContentId = (IsEpub ? "EPUB-" : "") + RuntimeCompat.Sha256Hex(Encoding.UTF8.GetBytes(identityText));
        var blocks = new List<TextBlockInfo>();
        var chapters = new List<Chapter>();
        for (int start = 0; start < Text.Length;)
        {
            int end = Text.IndexOf('\n', start);
            if (end < 0) end = Text.Length;
            int length = end - start;
            string? heading = length is > 0 and <= 90 ? Text.Substring(start, length).Trim() : null;
            bool isHeading = IsEpub ? headings?.Contains(start) == true : heading is not null && ChapterPattern.IsMatch(heading);
            if (isHeading) chapters.Add(new Chapter(heading!, start));
            if (length == 0) blocks.Add(new(start, 0, false, false));
            for (int part = start; part < end;)
            {
                int next = Math.Min(end, part + 4096);
                // Keep surrogate pairs and combining sequences together at artificial boundaries.
                while (next < end && (char.IsLowSurrogate(Text[next]) ||
                    System.Globalization.CharUnicodeInfo.GetUnicodeCategory(Text, next) is
                    System.Globalization.UnicodeCategory.NonSpacingMark or
                    System.Globalization.UnicodeCategory.SpacingCombiningMark or
                    System.Globalization.UnicodeCategory.EnclosingMark)) next++;
                Images.TryGetValue(part, out var image);
                blocks.Add(new(part, next - part, part != start, isHeading, image, pageBreaks?.Contains(part) == true));
                part = next;
            }
            start = end + 1;
        }
        if (blocks.Count == 0) blocks.Add(new(0, 0, false, false));
        if (chapters.Count == 0 || chapters[0].Offset != 0) chapters.Insert(0, new("开篇", 0));
        var chunks = new List<TextChunk>();
        int first = 0;
        int imageCount = blocks[0].Image is null ? 0 : 1;
        for (int i = 1; i <= blocks.Count; i++)
        {
            if (i == blocks.Count || blocks[i].Start - blocks[first].Start >= 12000 || imageCount >= 4)
            {
                int end = i == blocks.Count ? Text.Length : blocks[i].Start;
                chunks.Add(new(first, i - first, blocks[first].Start, end));
                first = i;
                imageCount = 0;
            }
            if (i < blocks.Count && blocks[i].Image is not null) imageCount++;
        }
        Blocks = blocks;
        Chapters = navigation is { Count: > 0 } ? navigation.OrderBy(x => x.Offset).ToArray() : chapters;
        Chunks = chunks;
    }

    public int ClampOffset(int offset)
    {
        offset = RuntimeCompat.Clamp(offset, 0, Math.Max(0, Text.Length - 1));
        if (offset > 0 && char.IsLowSurrogate(Text[offset])) offset--;
        return offset;
    }

    public int ChunkAt(int offset)
    {
        int lo = 0, hi = Chunks.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Chunks[mid].Start <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public Chapter ChapterAt(int offset)
    {
        int lo = 0, hi = Chapters.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Chapters[mid].Offset <= offset) lo = mid; else hi = mid - 1;
        }
        return Chapters[lo];
    }

    public string Excerpt(int offset, int count = 40) => Text.Length == 0 ? "" :
        Text.Substring(ClampOffset(offset), Math.Min(count, Text.Length - ClampOffset(offset))).Replace('\n', ' ').Replace("\uFFFC", "[插图]");
}

public static class BookLoader
{
    static BookLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<BookDocument> LoadAsync(string path, string encoding = "自动", CancellationToken token = default)
    {
        if (Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase))
            return await Task.Run(() => EpubLoader.Load(path, token), token);
        var info = new FileInfo(path);
        if (info.Length > 150L * 1024 * 1024) throw new InvalidOperationException("此预览版支持最大 150 MiB 的 TXT，请先拆分文件。");
        byte[] bytes = await RuntimeCompat.ReadAllBytesAsync(path, token);
        return await Task.Run(() =>
        {
            var (text, name) = Decode(bytes, encoding);
            return new BookDocument(Path.GetFileNameWithoutExtension(path), text, name, Path.GetFullPath(path));
        }, token);
    }

    public static bool IsSupportedPath(string path) => new[] { ".txt", ".epub" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static (string Text, string Name) Decode(byte[] bytes, string name = "自动")
    {
        int skip = 0;
        Encoding chosen;
        if (name != "自动")
        {
            chosen = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var preamble = chosen.GetPreamble();
            if (preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble)) skip = preamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { chosen = new UTF8Encoding(false, true); skip = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { chosen = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { chosen = new UnicodeEncoding(true, false, true); skip = 2; }
        else
        {
            try { return (new UTF8Encoding(false, true).GetString(bytes), "UTF-8"); }
            catch (DecoderFallbackException)
            {
                // GB18030 is the default legacy candidate for Chinese novels. The UI always
                // offers an explicit Big5/GBK override: legacy detection cannot be certain.
                chosen = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
        }
        try { return (chosen.GetString(bytes, skip, bytes.Length - skip), chosen.WebName.ToUpperInvariant()); }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("无法按当前编码读取。请在编码菜单选择 GB18030、Big5 或 UTF-16 后重新打开。", ex);
        }
    }
}
