using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TxtReader.App;

namespace Qidu.Desktop;

internal static class DesktopTests
{
    private static readonly List<object> Checks = [];
    private static void Check(bool condition, string name, object? detail = null)
    {
        Checks.Add(new { name, passed = condition, detail });
        if (!condition) throw new InvalidOperationException(name + ": " + JsonSerializer.Serialize(detail));
    }
    private static async Task Settle() { await Task.Delay(90); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); }
    private static void Capture(MainWindow window, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
    internal static async Task Run(MainWindow window, string folder)
    {
        Directory.CreateDirectory(folder); Exception? error = null; var reader = window.Surface;
        try
        {
            reader.AnimationsEnabled = false;
            string txt = Path.Combine(folder, "山间来信.txt");
            await File.WriteAllTextAsync(txt, string.Join("\n", Enumerable.Range(1, 24).Select(c => $"第{c}章 山间来信\n" +
                string.Join("\n", Enumerable.Range(1, 65).Select(p => $"清晨的风从山谷吹来，第{p}封信静静放在窗边。远处的松林传来细细的声响，纸上的文字像一条小路，通向未知的春天。🙂")))), new UTF8Encoding(false));
            await window.OpenFile(txt); await Settle();
            Check(reader.Book!.Chapters.Count >= 24, "TXT chapter discovery");
            string id = reader.Book.ContentId; int anchor = reader.Book.Chapters[8].Offset + 132;
            reader.Navigate(anchor); await Settle();
            foreach (string view in new[] { "single", "double", "scroll" })
            foreach (int font in new[] { 16, 22, 32 })
            foreach (int width in new[] { 620, 1000 })
            {
                window.Configure(view == "scroll" ? "scroll" : "page", font, reader.Appearance with { PageLayout = view, Width = width });
                await Settle();
                using var image = new RenderTargetBitmap(new PixelSize((int)reader.Bounds.Width, (int)reader.Bounds.Height)); image.Render(reader);
                Check(reader.Anchor == anchor && reader.AnchorVisible, "reflow retains visible character anchor", new { view, font, width, reader.Anchor, reader.AnchorVisible });
            }
            window.Configure("page", 22, reader.Appearance with { PageLayout = "double", Theme = "paper", Width = 790 }); await Settle();
            var mark = window.AddBookmark(); Check(mark?.Offset == anchor, "bookmark uses character offset");
            int destination = reader.Destination(1);
            Check(reader.BeginDrag(1), "body drag prepares next spread"); reader.MoveDrag(25); reader.ReleaseDrag();
            Check(reader.Anchor == anchor, "short drag rebounds without saving preview");
            Check(reader.BeginDrag(1), "curved leaf preview starts"); reader.MoveDrag(140);
            await Settle(); Capture(window, Path.Combine(folder, "curved-leaf.png"));
            Check(reader.Anchor == anchor, "drawing the curled leaf does not commit progress"); reader.CancelDrag();
            Check(reader.BeginDrag(1), "second drag starts"); reader.MoveDrag(280); reader.CancelDrag();
            Check(reader.Anchor == anchor, "canceled drag retains original offset");
            Check(reader.BeginDrag(1), "font change drag starts"); reader.MoveDrag(280);
            window.Configure("scroll", 28, reader.Appearance); Check(!reader.IsDragging && reader.Anchor == anchor, "mode and font change cancel pending turn");
            window.Configure("page", 22, reader.Appearance); await Settle();
            destination = reader.Destination(1); Check(reader.BeginDrag(1), "committing drag starts"); reader.MoveDrag(280); reader.ReleaseDrag();
            Check(reader.Anchor == destination && reader.Anchor != anchor, "long drag commits exactly one spread");
            reader.Navigate(anchor); reader.AnimationsEnabled = true; reader.BeginDrag(1); reader.MoveDrag(250); reader.ReleaseDrag(); reader.CancelDrag();
            await Task.Delay(300); Check(reader.Anchor == anchor, "canceled settling callback cannot later commit"); reader.AnimationsEnabled = false;
            foreach (var chapter in reader.Book.Chapters.Take(12)) { reader.Navigate(chapter.Offset); await Settle(); Check(reader.Anchor == chapter.Offset, "chapter jump retains heading offset"); }
            var hits = await BookSearch.FindPageAsync(reader.Book, "春天"); Check(hits.Hits.Count == 200 && hits.HasMore, "literal full-text search pages large results");
            reader.Navigate(hits.Hits[5].Offset, hits.Hits[5].Length); await Settle(); Capture(window, Path.Combine(folder, "txt-reading.png"));
            reader.Navigate(anchor); window.Save(); await window.OpenFile(window.Store.FindBook(id)!.Path); await Settle();
            Check(reader.Anchor == anchor && window.Store.ListBookmarks(id).Single().Offset == anchor, "managed TXT reopen preserves progress and bookmark");
            string epub = CreateEpub(folder); await window.OpenFile(epub); await Settle();
            Check(reader.Book!.IsEpub && reader.Book.Images.Count == 2 && reader.Book.Cover is not null, "EPUB metadata, cover and illustrations");
            foreach (int offset in reader.Book.Images.Keys)
            foreach (string view in new[] { "single", "double", "scroll" })
            {
                reader.Navigate(offset); window.Configure(view == "scroll" ? "scroll" : "page", 30, reader.Appearance with { PageLayout = view }); await Settle();
                using var image = new RenderTargetBitmap(new PixelSize((int)reader.Bounds.Width, (int)reader.Bounds.Height)); image.Render(reader);
                Check(reader.Anchor == offset && reader.AnchorVisible && reader.VisibleImages > 0, "EPUB image anchor survives all modes", new { view, offset, reader.VisibleImages });
            }
            window.Configure("page", 22, reader.Appearance with { PageLayout = "double", Theme = "white" }); reader.Navigate(0); await Settle(); Capture(window, Path.Combine(folder, "epub-reading.png"));
            var chapters = window.FindControl<ListBox>("ChapterList")!;
            var firstChapter = chapters.ContainerFromIndex(0);
            double? chapterY = firstChapter?.TranslatePoint(default, chapters)?.Y;
            Check(chapterY is >= 0 and < 12, "short EPUB contents reset the previous book's scroll layout", chapterY);
            if (MainWindow.Argument("--test-epub") is { } actual)
            {
                await window.OpenFile(actual); await Settle();
                Check(reader.Book!.Images.Count > 0, "user EPUB has illustrations");
                foreach (int offset in reader.Book.Images.Keys)
                { reader.Navigate(offset); await Settle(); using var image = new RenderTargetBitmap(new PixelSize((int)reader.Bounds.Width, (int)reader.Bounds.Height)); image.Render(reader); Check(reader.VisibleImages > 0 && reader.AnchorVisible, "user EPUB illustration visible", offset); }
                reader.Navigate(0); await Settle(); Capture(window, Path.Combine(folder, "user-epub-private.png"));
            }
            await window.OpenFile(window.Store.FindBook(id)!.Path); window.Configure("scroll", 26, reader.Appearance with { Theme = "paper" }); reader.Navigate(anchor); window.Save();
            var expected = new { bookId = id, path = window.Store.FindBook(id)!.Path, anchor, mode = "scroll", font = 26, bookmarks = 1 };
            await File.WriteAllTextAsync(Path.Combine(folder, "resume-expected.json"), JsonSerializer.Serialize(expected));
            window.ShowShelf(); await Settle(); Capture(window, Path.Combine(folder, "bookshelf.png"));
        }
        catch (Exception ex) { error = ex; Environment.ExitCode = 1; }
        await File.WriteAllTextAsync(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new { passed = error is null, platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), checks = Checks, failure = error?.ToString(), nativeMacVerified = OperatingSystem.IsMacOS() }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
    }
    internal static async Task Resume(MainWindow window, string folder)
    {
        Exception? error = null;
        try
        {
            using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "resume-expected.json"))); var e = expected.RootElement;
            await window.OpenFile(e.GetProperty("path").GetString()!); await Settle();
            var reader = window.Surface;
            Check(reader.Anchor == e.GetProperty("anchor").GetInt32() && reader.Mode == e.GetProperty("mode").GetString() && reader.ReadingFontSize == e.GetProperty("font").GetDouble(), "fresh process restores offset, mode and font");
            Check(window.Store.ListBookmarks(e.GetProperty("bookId").GetString()!).Count == e.GetProperty("bookmarks").GetInt32(), "fresh process retains bookmark");
            Check(reader.Appearance.Theme == "paper", "fresh process restores appearance");
        }
        catch (Exception ex) { error = ex; Environment.ExitCode = 1; }
        await File.WriteAllTextAsync(Path.Combine(folder, "resume-report.json"), JsonSerializer.Serialize(new { passed = error is null, checks = Checks, failure = error?.ToString() }, new JsonSerializerOptions { WriteIndented = true })); window.Close();
    }
    private static string CreateEpub(string folder)
    {
        string path = Path.Combine(folder, "星河来信.epub");
        using var art = new RenderTargetBitmap(new PixelSize(600, 840), new Vector(96, 96));
        using (var dc = art.CreateDrawingContext())
        {
            dc.FillRectangle(Brush.Parse("#243F45"), new Rect(0, 0, 600, 840));
            dc.DrawEllipse(Brush.Parse("#E4D7B4"), null, new Point(410, 180), 75, 75);
            for (int i = 0; i < 20; i++) dc.DrawEllipse(Brush.Parse("#D8D6BF"), null, new Point(30 + i * 27, 40 + (i * 67 % 320)), 2, 2);
            dc.FillRectangle(Brush.Parse("#698D7A"), new Rect(0, 550, 600, 290));
            using var title = new Avalonia.Media.TextFormatting.TextLayout("星 河 来 信", new Typeface(ReadingAppearance.FontFamilies[0]), 52, Brushes.White);
            title.Draw(dc, new Point(60, 415));
        }
        using var bytes = new MemoryStream(); art.Save(bytes, PngBitmapEncoderOptions.Default);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Entry(string name, string value) { using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(value); }
            Entry("mimetype", "application/epub+zip");
            Entry("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Entry("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>qidu-own-fixture</dc:identifier><dc:title>星河来信</dc:title><dc:language>zh-CN</dc:language></metadata><manifest><item id='c' href='cover.png' media-type='image/png' properties='cover-image'/><item id='b' href='book.xhtml' media-type='application/xhtml+xml'/><item id='n' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/></manifest><spine><itemref idref='b'/></spine></package>");
            Entry("nav.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol><li><a href='book.xhtml#first'>第一章 夜航</a></li><li><a href='book.xhtml#second'>第二章 来信</a></li></ol></nav></body></html>");
            Entry("book.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><body><img src='cover.png'/><h1 id='first'>第一章 夜航</h1>" + string.Concat(Enumerable.Range(0, 80).Select(i => "<p>星河漫过安静的月台。一封来自远方的信，在清晨抵达。每一页都藏着一个尚未讲完的故事。</p>")) + "<h1 id='second'>第二章 来信</h1><img src='cover.png'/><p>于是，我们打开了新的一页。</p></body></html>");
            using var entry = archive.CreateEntry("cover.png").Open(); entry.Write(bytes.ToArray());
        }
        return path;
    }
}
