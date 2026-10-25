using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TxtReader.Core;

namespace TxtReader.App;

internal static class EpubTests
{
    internal static async Task RunAsync(MainWindow window, string folder, Action<bool, string, object?> check)
    {
        var reader = window.ReadingSurface;
        void Check(bool passed, string name, object? detail = null) => check(passed, "EPUB: " + name, detail);
        string path = Path.Combine(folder, "自有图文样例.epub");
        byte[] art = FixtureArtwork();
        CreateFixture(path, art);
        var book = await BookLoader.LoadAsync(path);
        Check(book.IsEpub && book.Title == "星河来信" && book.Text.IndexOf("第一段", StringComparison.Ordinal) < book.Text.IndexOf("第二段", StringComparison.Ordinal), "metadata and spine order");
        Check(book.Images.Count == 2 && book.Cover is not null && book.Cover.Data.SequenceEqual(art), "cover and relative percent-encoded illustrations");
        Check(book.Chapters.Any(x => x.Title == "抵达月台") && book.Chapters.Any(x => x.Title == "星河来信"), "EPUB 3 nav fragment destinations");
        Check(!book.Text.Contains("discarded-script") && book.Text.Contains("星（xing）"), "script is discarded and ruby remains readable");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool seen = false;
            try { await BookLoader.LoadAsync(path, token: canceled.Token); } catch (OperationCanceledException) { seen = true; }
            Check(seen, "canceled EPUB import cannot register a book");
        }
        string duplicatePath = Path.Combine(folder, "同一本另一个文件名.epub");
        File.Copy(path, duplicatePath);
        Check((await BookLoader.LoadAsync(duplicatePath)).ContentId == book.ContentId, "filename changes preserve content identity");
        string traversal = Path.Combine(folder, "无效内部路径.epub");
        using (var bad = ZipFile.Open(traversal, ZipArchiveMode.Create)) bad.CreateEntry("../outside.txt");
        bool rejected = false;
        try { await BookLoader.LoadAsync(traversal); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(folder, "outside.txt")), "archive traversal rejected without extraction");
        string legacy = Path.Combine(folder, "自有EPUB2样例.epub");
        CreateFixture(legacy, art, true);
        var epub2 = await BookLoader.LoadAsync(legacy);
        Check(epub2.Chapters.Any(x => x.Title == "NCX 抵达月台") && epub2.Images.Count == 2, "EPUB 2 NCX and external DTD without network resolution");

        reader.AnimationsEnabled = false;
        await window.OpenFileAsync(path);
        string id = reader.Book!.ContentId;
        var shelf = window.Store.FindBook(id)!;
        Check(File.Exists(Path.Combine(Path.GetDirectoryName(shelf.Path)!, ".qidu-cover")), "managed EPUB includes a shelf cover");
        File.Move(path, path + ".original-moved");
        await window.OpenLibraryBookAsync(id);
        Check(reader.Book!.Images.Count == 2, "managed EPUB remains readable after original moves");
        foreach (string layout in new[] { "single", "double", "scroll" })
        {
            await window.SetReadingViewAsync(layout == "scroll" ? ReadingMode.Scroll : ReadingMode.Page, layout == "scroll" ? null : layout);
            foreach (int imageAnchor in reader.Book!.Images.Keys)
            {
                await reader.NavigateAsync(imageAnchor);
                Check(reader.Anchor == imageAnchor && reader.Snapshot().AnchorVisible && reader.RenderedIllustrations > 0,
                    layout + " illustration has a visible persistent anchor", reader.Snapshot());
                await reader.SetFontAsync(32);
                Check(reader.Anchor == imageAnchor && reader.Snapshot().AnchorVisible, layout + " illustration position survives font change", reader.Snapshot());
                await reader.SetFontAsync(20);
            }
        }
        await window.SetReadingViewAsync(ReadingMode.Page, "double");
        await window.SearchForAsync("晨星站");
        var hit = window.CurrentSearchPage!.Hits.First();
        await window.JumpAsync(hit.Offset, hit.Length);
        Check(reader.Anchor == hit.Offset && reader.Snapshot().HighlightedCharacters == 3 && reader.Snapshot().AnchorVisible, "search highlights actual EPUB text");
        var bookmark = await window.AddBookmarkAsync();
        await window.SetReadingViewAsync(ReadingMode.Scroll);
        await reader.SetFontAsync(28);
        window.SavePosition(true);
        await window.OpenLibraryBookAsync(id);
        Check(reader.Anchor == bookmark.Offset && reader.Mode == ReadingMode.Scroll && reader.ReadingFontSize == 28 &&
            window.Store.ListBookmarks(id).Any(x => x.Offset == bookmark.Offset), "reopen restores EPUB bookmarks mode font and position");
        await window.SetReadingViewAsync(ReadingMode.Page, "single");
        await reader.NavigateAsync(0);
        int before = reader.Anchor;
        Point start = new(reader.ActualWidth * .8, reader.ActualHeight * .5);
        reader.BeginPagePointer(start);
        reader.MovePagePointer(start + new Vector(-reader.DragTravel * .3, 0));
        await reader.WaitForDragPreviewAsync();
        await reader.EndPagePointerAsync(start + new Vector(-reader.DragTravel * .3, 0));
        Check(reader.Anchor == before && reader.Snapshot().AnchorVisible, "illustration drag rebound retains source position");
        reader.BeginPagePointer(start);
        await reader.EndPagePointerAsync(start + new Vector(-reader.DragTravel * .8, 0));
        Check(reader.Anchor > before && reader.Snapshot().AnchorVisible, "illustration drag completion advances exactly to visible text");
        await reader.NavigateAsync(0);
        Capture(reader, Path.Combine(folder, "epub-sample-illustration.png"));
        await window.ShowShelfAsync();
        Capture(window, Path.Combine(folder, "epub-sample-shelf.png"));

        if (window.TestEpubPath is { } realPath)
        {
            await window.OpenFileAsync(realPath);
            var real = reader.Book!;
            Check(real.IsEpub && real.Chapters.Count > 1 && real.Images.Count > 0, "real EPUB imports with navigation and illustrations",
                new { real.Title, chapters = real.Chapters.Count, illustrations = real.Images.Count, characters = real.Text.Length, warnings = real.Warnings });
            foreach (var image in real.Images.Values.Distinct())
                Check(BookArtwork.Decode(image.Data).PixelWidth > 0, "real illustration decodes", image.Name);
            foreach (var chapter in real.Chapters)
            {
                await reader.NavigateAsync(chapter.Offset);
                Check(reader.Anchor == chapter.Offset && reader.Snapshot().AnchorVisible, "real chapter destination is visible", chapter.Title);
            }
            foreach (string layout in new[] { "single", "double", "scroll" })
            {
                await window.SetReadingViewAsync(layout == "scroll" ? ReadingMode.Scroll : ReadingMode.Page, layout == "scroll" ? null : layout);
                foreach (int target in new[] { real.Images.Keys.First(), real.Images.Keys.Last(), real.ClampOffset(real.Text.Length / 2) })
                {
                    await reader.NavigateAsync(target);
                    await reader.SetFontAsync(34);
                    Check(reader.Anchor == target && reader.Snapshot().AnchorVisible, "real " + layout + " position survives reflow", reader.Snapshot());
                }
            }
            int anchor = reader.Anchor;
            await window.AddBookmarkAsync();
            window.SavePosition(true);
            await window.OpenLibraryBookAsync(real.ContentId);
            Check(reader.Anchor == anchor && reader.Snapshot().AnchorVisible && window.Store.ListBookmarks(real.ContentId).Any(x => x.Offset == anchor), "real book reopens at saved bookmark");
            await window.SetReadingViewAsync(ReadingMode.Page, "double");
            await reader.NavigateAsync(0);
            Capture(window, Path.Combine(folder, "epub-real-reading-private.png"));
        }
    }

    private static void Capture(FrameworkElement element, string file)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(PageTurnLayer.Capture(element)));
        using var output = File.Create(file);
        encoder.Save(output);
    }
    private static byte[] FixtureArtwork()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.DarkSlateBlue, null, new Rect(0, 0, 360, 480));
            dc.DrawEllipse(Brushes.PaleGoldenrod, null, new Point(250, 110), 42, 42);
            dc.DrawRectangle(Brushes.DarkCyan, null, new Rect(0, 340, 360, 140));
        }
        var bitmap = new RenderTargetBitmap(360, 480, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static void CreateFixture(string path, byte[] art, bool epub2 = false)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, string text)
        { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
        Put("mimetype", "application/epub+zip");
        Put("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='Books/book.opf'/></rootfiles></container>");
        string navigation = epub2 ? "<item id='nav' href='toc.ncx' media-type='application/x-dtbncx+xml'/>" : "<item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>";
        Put("Books/book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='" + (epub2 ? "2.0" : "3.0") + "'><metadata><title xmlns='http://purl.org/dc/elements/1.1/'>星河来信</title><meta name='cover' content='cover'/></metadata><manifest>" + navigation + "<item id='second' href='Text/a.xhtml' media-type='application/xhtml+xml'/><item id='first' href='Text/z.xhtml' media-type='application/xhtml+xml'/><item id='cover' href='Images/插图%23%25.png' media-type='image/png' properties='cover-image'/></manifest><spine toc='nav'><itemref idref='first'/><itemref idref='second'/></spine></package>");
        Put("Books/nav.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol><li><a href='Text/z.xhtml#begin'>星河来信</a></li><li><a href='Text/a.xhtml'>抵达月台</a></li></ol></nav></body></html>");
        Put("Books/toc.ncx", "<!DOCTYPE ncx SYSTEM 'https://example.invalid/must-not-fetch.dtd'><ncx><navMap><navPoint><navLabel><text>NCX 星河来信</text></navLabel><content src='Text/z.xhtml#begin'/></navPoint><navPoint><navLabel><text>NCX 抵达月台</text></navLabel><content src='Text/a.xhtml'/></navPoint></navMap></ncx>");
        string paragraphs = string.Concat(Enumerable.Repeat("<p>在晨星站，我们读着这封来自远方的信。夜色渐深，月台上的灯依然亮着。</p>", 100));
        Put("Books/Text/z.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><body><img src='../Images/插图%23%25.png'/><h1 id='begin'>星河来信</h1><p>第一段 <ruby>星<rt>xing</rt></ruby></p><script>discarded-script</script>" + paragraphs + "</body></html>");
        Put("Books/Text/a.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><body><h1>抵达月台</h1><p>第二段</p><img src='../Images/插图%23%25.png'/>" + paragraphs + "</body></html>");
        using var image = zip.CreateEntry("Books/Images/插图#%.png").Open(); image.Write(art, 0, art.Length);
    }
}
