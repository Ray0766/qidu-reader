using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TxtReader.Core;

/// <summary>Offline reflowable EPUB 2/3 import. No scripts, network access or archive extraction.</summary>
public static class EpubLoader
{
    private sealed record Item(string Id, string Path, string Type, string Properties);
    private sealed record Nav(string Label, string Target);
    private static string Name(XElement element) => element.Name.LocalName.ToLowerInvariant();
    private static string Attr(XElement element, string name) => element.Attributes().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "";
    private static bool Token(string list, string token) => list.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(token);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static BookDocument Load(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > 150L * 1024 * 1024) throw new InvalidDataException("EPUB 文件超过 150 MiB 的预览版限制。");
        using var stream = File.OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 5000 || zip.Entries.Sum(x => x.Length) > 256L * 1024 * 1024)
            throw new InvalidDataException("EPUB 展开内容过大，暂无法安全打开。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries.Where(x => !x.FullName.EndsWith("/", StringComparison.Ordinal)))
        {
            string name = entry.FullName;
            if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains('\\') || name.Contains(':') ||
                name.Split('/').Any(x => x is ".." or ".") || entries.ContainsKey(name))
                throw new InvalidDataException("EPUB 包含重复或无效的内部路径。");
            entries.Add(name, entry);
        }
        var encrypted = new HashSet<string>(StringComparer.Ordinal);
        byte[] Read(string name, int limit)
        {
            token.ThrowIfCancellationRequested();
            if (encrypted.Contains(name)) throw new InvalidDataException("这本 EPUB 的正文或图片受加密保护，当前无法读取。");
            if (!entries.TryGetValue(name, out var entry)) throw new InvalidDataException("EPUB 缺少资源：" + name);
            if (entry.Length > limit) throw new InvalidDataException("EPUB 单个资源过大：" + name);
            using var input = entry.Open();
            using var output = new MemoryStream();
            byte[] buffer = new byte[32768];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (output.Length + count > limit) throw new InvalidDataException("EPUB 资源超过读取限制。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        XDocument Xml(string name)
        {
            using var input = new MemoryStream(Read(name, 8 * 1024 * 1024));
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
                MaxCharactersInDocument = 8 * 1024 * 1024, MaxCharactersFromEntities = 1024
            });
            try { return XDocument.Load(reader, LoadOptions.PreserveWhitespace); }
            catch (XmlException ex) { throw new InvalidDataException("EPUB 文档格式损坏：" + name, ex); }
        }
        if (entries.ContainsKey("META-INF/encryption.xml"))
            foreach (var cipher in Xml("META-INF/encryption.xml").Descendants().Where(x => Name(x) == "cipherreference"))
                encrypted.Add(Resolve("", Attr(cipher, "URI")));
        var container = Xml("META-INF/container.xml");
        string rootPath = container.Descendants().Where(x => Name(x) == "rootfile")
            .Select(x => Attr(x, "full-path")).FirstOrDefault() ?? throw new InvalidDataException("EPUB 缺少书籍入口。");
        rootPath = Resolve("", rootPath);
        var package = Xml(rootPath);
        if (package.Descendants().Any(x => Name(x) == "meta" && Attr(x, "property") == "rendition:layout" && x.Value.Trim() == "pre-paginated"))
            throw new InvalidDataException("当前支持可重排 EPUB，固定版式 EPUB 尚未支持。");
        string title = package.Descendants().FirstOrDefault(x => Name(x) == "title")?.Value.Trim() ?? Path.GetFileNameWithoutExtension(path);
        var manifest = package.Descendants().Where(x => Name(x) == "manifest").Elements().Where(x => Name(x) == "item")
            .Select(x => new Item(Attr(x, "id"), Resolve(rootPath, Attr(x, "href")), Attr(x, "media-type"), Attr(x, "properties")))
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
        var spine = package.Descendants().FirstOrDefault(x => Name(x) == "spine") ?? throw new InvalidDataException("EPUB 缺少阅读顺序。");
        var spineItems = spine.Elements().Where(x => Name(x) == "itemref" && Attr(x, "linear") != "no").ToList();
        if (spineItems.Any(x => Token(Attr(x, "properties"), "rendition:layout-pre-paginated")))
            throw new InvalidDataException("当前尚未支持固定版式 EPUB 页面。");
        var navigation = new List<Nav>();
        var nav = manifest.Values.FirstOrDefault(x => Token(x.Properties, "nav"));
        if (nav is not null)
        {
            var toc = Xml(nav.Path).Descendants().FirstOrDefault(x => Name(x) == "nav" && Token(Attr(x, "type"), "toc"));
            if (toc is not null)
                foreach (var link in toc.Descendants().Where(x => Name(x) == "a"))
                    navigation.Add(new(Whitespace.Replace(link.Value, " ").Trim(), Target(nav.Path, Attr(link, "href"))));
        }
        if (navigation.Count == 0)
        {
            manifest.TryGetValue(Attr(spine, "toc"), out var ncx);
            ncx ??= manifest.Values.FirstOrDefault(x => x.Type == "application/x-dtbncx+xml");
            if (ncx is not null)
                foreach (var point in Xml(ncx.Path).Descendants().Where(x => Name(x) == "navpoint"))
                {
                    var label = point.Elements().FirstOrDefault(x => Name(x) == "navlabel");
                    var content = point.Elements().FirstOrDefault(x => Name(x) == "content");
                    if (label is not null && content is not null)
                        navigation.Add(new(Whitespace.Replace(label.Value, " ").Trim(), Target(ncx.Path, Attr(content, "src"))));
                }
        }
        var text = new StringBuilder();
        var images = new Dictionary<int, BookImage>();
        var imageCache = new Dictionary<string, BookImage>(StringComparer.Ordinal);
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        var headings = new HashSet<int>();
        var pageBreaks = new HashSet<int>();
        var fallbackChapters = new List<Chapter>();
        var warnings = new HashSet<string>();
        BookImage? Image(string imagePath)
        {
            if (imageCache.TryGetValue(imagePath, out var cached)) return cached;
            var item = manifest.Values.FirstOrDefault(x => x.Path == imagePath);
            string type = item?.Type ?? "";
            if (type is not ("image/jpeg" or "image/png" or "image/gif" or "image/bmp"))
            { warnings.Add("部分插图格式暂不支持（仅显示 JPEG、PNG、GIF、BMP）。"); return null; }
            var result = new BookImage(imagePath, type, Read(imagePath, 16 * 1024 * 1024));
            imageCache.Add(imagePath, result);
            return result;
        }
        void Line()
        {
            while (text.Length > 0 && text[text.Length - 1] == ' ') text.Length--;
            if (text.Length > 0 && text[text.Length - 1] != '\n') text.Append('\n');
        }
        void AppendText(string value)
        {
            value = Whitespace.Replace(value, " ");
            if (text.Length == 0 || text[text.Length - 1] is '\n' or ' ') value = value.TrimStart(' ');
            text.Append(value);
            if (text.Length > 32 * 1024 * 1024) throw new InvalidDataException("EPUB 正文过大。");
        }
        foreach (var reference in spineItems)
        {
            token.ThrowIfCancellationRequested();
            if (!manifest.TryGetValue(Attr(reference, "idref"), out var item)) throw new InvalidDataException("EPUB 阅读顺序引用了不存在的章节。");
            if (item.Type is not ("application/xhtml+xml" or "text/html")) throw new InvalidDataException("EPUB 包含尚不支持的正文格式：" + item.Type);
            var html = Xml(item.Path);
            var body = html.Descendants().FirstOrDefault(x => Name(x) == "body") ?? throw new InvalidDataException("EPUB 章节缺少正文。");
            Line();
            int start = text.Length;
            anchors[item.Path] = start;
            pageBreaks.Add(start);
            string chapterTitle = navigation.FirstOrDefault(x => x.Target == item.Path)?.Label ??
                html.Descendants().FirstOrDefault(x => Name(x) is "h1" or "h2" or "title")?.Value.Trim() ?? "第 " + (fallbackChapters.Count + 1) + " 节";
            fallbackChapters.Add(new(chapterTitle, start));
            void Walk(XNode node, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 100) throw new InvalidDataException("EPUB 文档嵌套过深。");
                if (node is XText raw) { AppendText(raw.Value); return; }
                if (node is not XElement element) return;
                string name = Name(element);
                if (name is "head" or "script" or "style" or "noscript" or "iframe" or "audio" or "video" ||
                    element.Attribute("hidden") is not null || Regex.IsMatch(Attr(element, "style"), @"display\s*:\s*none|visibility\s*:\s*hidden", RegexOptions.IgnoreCase)) return;
                bool block = name is "p" or "div" or "section" or "article" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "blockquote" or "pre" or "figure" or "figcaption" or "tr";
                if (block || name is "br" or "hr") Line();
                string id = Attr(element, "id");
                if (id.Length > 0) anchors[item.Path + "#" + id] = text.Length;
                if (name is "h1" or "h2" or "h3") headings.Add(text.Length);
                if (name is "img" or "image")
                {
                    Line();
                    string source = Attr(element, name == "img" ? "src" : "href");
                    BookImage? image = null;
                    if (IsExternal(source)) warnings.Add("远程图片未联网加载。");
                    else if (source.Length > 0) image = Image(Resolve(item.Path, source));
                    if (image is not null) { images[text.Length] = image; text.Append('\uFFFC'); }
                    else AppendText("[插图暂无法显示] " + Attr(element, "alt"));
                    Line();
                    return;
                }
                if (name == "rp") return;
                if (name == "rt") AppendText("（");
                foreach (var child in element.Nodes()) Walk(child, depth + 1);
                if (name == "rt") AppendText("）");
                if (block) Line();
            }
            Walk(body, 0);
            Line();
        }
        if (text.Length == 0) throw new InvalidDataException("这本 EPUB 没有可阅读的正文。");
        var chapters = navigation.Where(x => anchors.ContainsKey(x.Target)).Select(x => new Chapter(x.Label, anchors[x.Target]))
            .GroupBy(x => x.Offset).Select(x => x.First()).Where(x => x.Offset < text.Length).OrderBy(x => x.Offset).ToList();
        if (chapters.Count == 0) chapters = fallbackChapters.Where(x => x.Offset < text.Length).GroupBy(x => x.Offset).Select(x => x.First()).ToList();
        if (chapters.Count == 0 || chapters[0].Offset != 0) chapters.Insert(0, new("开篇", 0));
        foreach (var chapter in chapters) pageBreaks.Add(chapter.Offset);
        string coverId = package.Descendants().FirstOrDefault(x => Name(x) == "meta" && Attr(x, "name") == "cover") is { } meta ? Attr(meta, "content") : "";
        manifest.TryGetValue(coverId, out var coverItem);
        coverItem ??= manifest.Values.FirstOrDefault(x => Token(x.Properties, "cover-image"));
        var cover = coverItem is null ? images.OrderBy(x => x.Key).FirstOrDefault().Value : Image(coverItem.Path);
        return new(title, text.ToString(), "EPUB", Path.GetFullPath(path), chapters, images, headings, pageBreaks, cover, warnings.ToArray());
    }

    private static bool IsExternal(string value) => value.StartsWith("//", StringComparison.Ordinal) ||
        Regex.IsMatch(value, @"^[a-zA-Z][a-zA-Z0-9+.-]*:");
    private static string Target(string source, string href)
    {
        int hash = href.IndexOf('#');
        return Resolve(source, href) + (hash < 0 ? "" : "#" + Uri.UnescapeDataString(href.Substring(hash + 1)));
    }
    private static string Resolve(string source, string href)
    {
        if (IsExternal(href) || href.StartsWith("/", StringComparison.Ordinal)) throw new InvalidDataException("EPUB 引用了不支持的外部资源。");
        string name = Uri.UnescapeDataString(href.Split('#', '?')[0]);
        if (name.Contains('\\') || name.Contains(':') || name.Contains('\0')) throw new InvalidDataException("EPUB 资源路径无效。");
        if (name.Length == 0) return source;
        var parts = source.Split('/').Take(Math.Max(0, source.Split('/').Length - 1)).ToList();
        foreach (string part in name.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("EPUB 资源路径越界。"); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join("/", parts);
    }
}
