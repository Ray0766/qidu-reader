using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using TxtReader.Core;

namespace TxtReader.App;

internal static class FeatureTests
{
    public static async Task RunAsync(MainWindow window, string folder, Action<bool, string, object?> check, List<object> timings)
    {
        void Check(bool ok, string name, object? data = null) => check(ok, name, data);
        var boundary = new BookDocument("Search fixture", new string('甲', 65535) + "林舟🙂 哈哈哈 a.b A.B\n" + new string('乙', 200));
        var found = await BookSearch.FindPageAsync(boundary, "林舟");
        Check(found.Hits.Count == 1 && found.Hits[0].Offset == 65535, "search spans scan block boundary");
        found = await BookSearch.FindPageAsync(boundary, "🙂");
        Check(found.Hits.Count == 1 && found.Hits[0].Length == 2, "search supplementary Unicode character");
        found = await BookSearch.FindPageAsync(boundary, "a.b");
        Check(found.Hits.Count == 2, "search is literal and case insensitive");
        var overlap = new BookDocument("Overlap", "哈哈哈");
        var one = await BookSearch.FindPageAsync(overlap, "哈哈", pageSize: 1);
        var two = await BookSearch.FindPageAsync(overlap, "哈哈", one.NextOffset, pageSize: 1);
        Check(one.Hits[0].Offset == 0 && one.HasMore && two.Hits[0].Offset == 1 && !two.HasMore, "overlapping matches survive result pagination");
        var repeated = new BookDocument("Repeated", string.Concat(Enumerable.Repeat("林舟在桥边。\n", 451)));
        var offsets = new List<int>();
        int cursor = 0;
        do
        {
            found = await BookSearch.FindPageAsync(repeated, "林舟", cursor);
            offsets.AddRange(found.Hits.Select(x => x.Offset)); cursor = found.NextOffset;
        } while (found.HasMore);
        Check(offsets.Count == 451 && offsets.Distinct().Count() == 451 && offsets.SequenceEqual(offsets.Order()), "all 451 search results reachable without duplicates");
        Check((await BookSearch.FindPageAsync(repeated, "舟")).Hits.Count == 200, "single Chinese character search");
        Check((await BookSearch.FindPageAsync(repeated, "从未出现的片段")).Hits.Count == 0, "no-match full book search");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool canceled = false;
            try { await BookSearch.FindPageAsync(repeated, "林舟", cancellationToken: cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "search honors cancellation");
        }

        string legacyFolder = Path.Combine(folder, "legacy");
        Directory.CreateDirectory(legacyFolder);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(legacyFolder, "reading.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions(book_id TEXT PRIMARY KEY,path TEXT NOT NULL,offset INTEGER NOT NULL,
                    mode TEXT NOT NULL,font_size REAL NOT NULL,encoding TEXT NOT NULL,updated TEXT NOT NULL);
                INSERT INTO sessions VALUES('legacy','旧书.txt',1234,'Scroll',26,'UTF-8','2026-10-24T01:00:00Z');
                """;
            command.ExecuteNonQuery();
        }
        using (var upgraded = new SessionStore(legacyFolder))
        {
            var session = upgraded.Read("legacy")!;
            Check(upgraded.ListBooks().Single().Title == "旧书" && session.Offset == 1234 && session.Mode == "Scroll" && session.FontSize == 26,
                "legacy database migrates without losing reading state");
        }
        using (var upgradedAgain = new SessionStore(legacyFolder))
            Check(upgradedAgain.ListBooks().Count == 1, "migration is idempotent");

        string fixtures = Path.Combine(folder, "imports");
        Directory.CreateDirectory(fixtures);
        string firstPath = Path.Combine(fixtures, "第一本.txt"), aliasPath = Path.Combine(fixtures, "同内容别名.txt"), secondPath = Path.Combine(fixtures, "第二本.txt");
        string demoText = await File.ReadAllTextAsync(LibraryService.DemoPath);
        await File.WriteAllTextAsync(firstPath, demoText, new UTF8Encoding(false));
        await File.WriteAllTextAsync(aliasPath, demoText, new UTF8Encoding(true));
        await File.WriteAllTextAsync(secondPath, "第二本的开篇\n" + demoText + demoText, new UTF8Encoding(false));
        var batch = await window.ImportFilesAsync([firstPath, aliasPath, secondPath, Path.Combine(fixtures, "missing.txt")]);
        Check(batch.Imported == 2 && batch.Duplicates == 1 && batch.Failures.Count == 1 && window.Store.ListBooks().Count == 2,
            "batch imports deduplicate and report individual failures", batch);
        var first = window.Store.ListBooks().Single(x => x.Title == "第一本");
        var second = window.Store.ListBooks().Single(x => x.Title == "第二本");
        File.Move(firstPath, firstPath + ".moved");
        Check(File.Exists(first.Path) && (await BookLoader.LoadAsync(first.Path)).ContentId == first.BookId,
            "managed copy remains readable after original moves");
        window.Store.RenameBook(first.BookId, "自定义书名");
        Check(window.Store.ListBooks("自定义").Single().BookId == first.BookId && window.Store.ListBooks("无此书名").Count == 0,
            "shelf rename and title filter");
        await window.OpenLibraryBookAsync(first.BookId);
        var reader = window.ReadingSurface;
        await reader.NavigateAsync(1600);
        await reader.SetModeAsync(ReadingMode.Scroll);
        await reader.SetFontAsync(26);
        var mark = await window.AddBookmarkAsync();
        var duplicateMark = await window.AddBookmarkAsync();
        Check(mark.Id == duplicateMark.Id && window.Store.ListBookmarks(first.BookId).Count == 1, "bookmark add is persistent and idempotent at the same position");
        await window.OpenLibraryBookAsync(second.BookId);
        await reader.NavigateAsync(2300);
        await reader.SetModeAsync(ReadingMode.Page);
        await reader.SetFontAsync(18);
        Check(window.Store.ListBookmarks(second.BookId).Count == 0, "bookmarks are scoped to a book");
        await window.OpenLibraryBookAsync(first.BookId);
        Check(reader.Anchor == 1600 && reader.Mode == ReadingMode.Scroll && reader.ReadingFontSize == 26 && reader.Snapshot().AnchorVisible,
            "switching shelf books restores independent reading state", reader.Snapshot());
        await window.OpenLibraryBookAsync(second.BookId);
        Check(reader.Anchor == 2300 && reader.Mode == ReadingMode.Page && reader.ReadingFontSize == 18 && reader.Snapshot().AnchorVisible,
            "second book retains its own position mode and font", reader.Snapshot());
        await window.OpenLibraryBookAsync(first.BookId);
        int original = reader.Anchor;
        window.SidebarTabs.SelectedItem = window.SearchTab;
        await window.SearchForAsync("林舟");
        var hit = window.CurrentSearchPage!.Hits.Last();
        await window.JumpAsync(hit.Offset, hit.Length);
        Check(reader.Anchor == hit.Offset && reader.Snapshot().AnchorVisible && reader.Snapshot().HighlightedCharacters == 2,
            "search result jumps and highlights actual source text", reader.Snapshot());
        foreach (var mode in new[] { ReadingMode.Page, ReadingMode.Scroll })
        {
            await reader.SetModeAsync(mode);
            foreach (int font in new[] { 14, 22, 36 })
            {
                await reader.SetFontAsync(font);
                Check(reader.Anchor == hit.Offset && reader.Snapshot().AnchorVisible && reader.Snapshot().HighlightedCharacters == 2,
                    $"search target survives {mode} font {font}", reader.Snapshot());
            }
        }
        await Task.WhenAll(reader.SetModeAsync(ReadingMode.Page), reader.SetFontAsync(18), reader.SetModeAsync(ReadingMode.Scroll), reader.AdjustFontAsync(2), reader.AdjustFontAsync(2));
        Check(reader.Anchor == hit.Offset && reader.Snapshot().AnchorVisible && reader.Mode == ReadingMode.Scroll && reader.ReadingFontSize == 22,
            "queued mode and font actions preserve search anchor", reader.Snapshot());
        await window.JumpAsync(window.CurrentSearchPage!.Hits[0].Offset, 2);
        await window.ReturnToReadingAsync();
        Check(reader.Anchor == original && reader.Snapshot().AnchorVisible && reader.Snapshot().HighlightedCharacters == 0,
            "return from multiple search results restores original reading place", reader.Snapshot());
        await reader.NavigateAsync(3500);
        window.SidebarTabs.SelectedItem = window.BookmarksTab;
        await window.JumpToBookmarkAsync(mark);
        foreach (var mode in new[] { ReadingMode.Page, ReadingMode.Scroll })
        {
            await reader.SetModeAsync(mode);
            await reader.SetFontAsync(mode == ReadingMode.Page ? 32 : 16);
            Check(reader.Anchor == mark.Offset && reader.Snapshot().AnchorVisible, "bookmark target survives mode and font " + mode, reader.Snapshot());
        }
        await window.ReturnToReadingAsync();
        Check(reader.Anchor == 3500 && reader.Snapshot().AnchorVisible, "bookmark return restores most recent origin");
        window.Store.DeleteBookmark(second.BookId, mark.Id);
        Check(window.Store.ListBookmarks(first.BookId).Count == 1, "bookmark delete cannot affect another book");
        var temporaryMark = await window.AddBookmarkAsync();
        window.Store.DeleteBookmark(first.BookId, temporaryMark.Id);
        Check(window.Store.ListBookmarks(first.BookId).Single().Id == mark.Id, "delete removes only requested bookmark");
        await window.ShowShelfAsync();
        using (var reopened = new SessionStore(window.Store.Folder))
        {
            Check(reopened.ListBookmarks(first.BookId).Single().Offset == mark.Offset && reopened.Read(first.BookId)!.Offset == 3500,
                "reopened database restores bookmarks and progress");
        }
        window.Store.ArchiveBook(first.BookId);
        Check(window.Store.ListBooks().All(x => x.BookId != first.BookId) && window.Store.Read()?.BookId != first.BookId && File.Exists(first.Path),
            "remove from shelf preserves copy and excludes auto resume");
        await window.OpenFileAsync(aliasPath);
        Check(window.Store.FindBook(first.BookId)!.Title == "自定义书名" && window.Store.ListBookmarks(first.BookId).Count == 1 && reader.Anchor == 3500,
            "reimport restores removed book with name progress and bookmarks");

        await window.LoadTestBookAsync(repeated);
        await window.SearchForAsync("林舟");
        int firstPageEnd = window.CurrentSearchPage!.Hits.Last().Offset;
        await window.SearchNextPageAsync();
        Check(window.CurrentSearchPage!.Hits.Count == 200 && window.CurrentSearchPage.Hits[0].Offset > firstPageEnd, "UI search next batch advances");
        await window.SearchPreviousPageAsync();
        Check(window.CurrentSearchPage!.Hits[0].Offset == 0, "UI search previous batch returns");
        await Task.WhenAll(window.SearchForAsync("林舟"), window.SearchForAsync("不存在的独特片段"));
        Check(window.CurrentSearchPage!.Hits.Count == 0, "stale search cannot overwrite newer query");
        var pending = window.SearchForAsync("林舟");
        await window.OpenLibraryBookAsync(second.BookId);
        await pending;
        Check(window.CurrentSearchPage is null && reader.Book!.ContentId == second.BookId, "switching books discards old search results");

        var appearanceBook = reader.Book!;
        int appearanceTarget = appearanceBook.Text.IndexOf("林舟", 2500, StringComparison.Ordinal);
        await window.JumpAsync(appearanceTarget, 2);
        var appearanceMark = await window.AddBookmarkAsync();
        int combination = 0;
        foreach (var font in ReadingAppearance.Fonts)
        foreach (var mode in new[] { ReadingMode.Page, ReadingMode.Scroll })
        {
            var theme = ReadingAppearance.Themes[combination % ReadingAppearance.Themes.Length];
            var width = ReadingAppearance.Widths[combination % ReadingAppearance.Widths.Length];
            await reader.SetModeAsync(mode);
            await window.ApplyAppearanceAsync(new(font.Family, theme.Key, width.Width));
            await reader.SetFontAsync(mode == ReadingMode.Page ? 20 : 28);
            var snapshot = reader.Snapshot();
            Check(snapshot.Anchor == appearanceTarget && snapshot.AnchorVisible && snapshot.HighlightedCharacters == 2 &&
                  snapshot.FontFamily == font.Family && snapshot.Theme == theme.Key && (width.Width == 0 || snapshot.LayoutWidth <= width.Width),
                $"appearance preserves search target {font.Name} {theme.Name} {width.Name} {mode}", snapshot);
            combination++;
        }
        await window.ApplyAppearanceAsync(new("KaiTi", "paper", 620));
        double narrowWidth = reader.ActualWidth;
        await window.ApplyAppearanceAsync(new("KaiTi", "paper", 0));
        Check(reader.ActualWidth > narrowWidth && reader.Anchor == appearanceTarget && reader.Snapshot().AnchorVisible,
            "reading width changes actual page geometry without losing position", new { narrowWidth, fullWidth = reader.ActualWidth });
        using (var appearanceStore = new SessionStore(window.Store.Folder))
            Check(appearanceStore.ReadAppearance() == new ReadingAppearance("KaiTi", "paper", 0), "font theme and width persist across store reopen");
        await reader.NavigateAsync(700);
        await window.JumpToBookmarkAsync(appearanceMark);
        await window.ApplyAppearanceAsync(new("FangSong", "night", 620));
        Check(reader.Anchor == appearanceMark.Offset && reader.Snapshot().AnchorVisible, "bookmark survives a new font background and width");
        await reader.SetModeAsync(ReadingMode.Page);
        await reader.SetFontAsync(22);
        foreach (var chapter in appearanceBook.Chapters.Where(x => x.Offset > 0).Take(6))
        {
            await reader.NavigateAsync(chapter.Offset);
            var snapshot = reader.Snapshot();
            Check(snapshot.FirstVisible == chapter.Offset && snapshot.AnchorVisible, "each chapter begins on its own page " + chapter.Title,
                new { chapter.Offset, snapshot.FirstVisible, snapshot.Page });
        }
        await window.ApplyAppearanceAsync(new());

        if (window.TestBookPath is { } realPath)
        {
            var watch = Stopwatch.StartNew();
            await window.OpenFileAsync(realPath);
            long openMs = watch.ElapsedMilliseconds;
            var book = reader.Book!;
            Check(book.Text.Length > 100000 && book.Chapters.Count > 1 && reader.Snapshot().AnchorVisible,
                "real novel imports with chapters", new { book.Title, book.EncodingName, characters = book.Text.Length, chapters = book.Chapters.Count });
            foreach (double fraction in new[] { .12, .5, .92 })
            {
                int target = book.ClampOffset((int)(book.Text.Length * fraction));
                await reader.NavigateAsync(target);
                string excerpt = book.Excerpt(target);
                foreach (var mode in new[] { ReadingMode.Scroll, ReadingMode.Page })
                {
                    await reader.SetModeAsync(mode);
                    await reader.SetFontAsync(mode == ReadingMode.Scroll ? 30 : 16);
                    Check(reader.Anchor == target && reader.Snapshot().AnchorVisible && reader.Snapshot().Excerpt == excerpt,
                        $"real novel {fraction:P0} survives {mode} and reflow", new { target, mode = mode.ToString(), reader.ReadingFontSize });
                }
            }
            int returnPosition = reader.Anchor;
            int lateOffset = book.ClampOffset((int)(book.Text.Length * .95));
            string latePhrase = book.Text.Substring(lateOffset, Math.Min(30, book.Text.Length - lateOffset)).Trim();
            int lateExpected = book.Text.IndexOf(latePhrase, StringComparison.OrdinalIgnoreCase);
            watch.Restart();
            await window.SearchForAsync(latePhrase);
            long latePhraseSearchMs = watch.ElapsedMilliseconds;
            Check(lateExpected > book.Text.Length / 2 && window.CurrentSearchPage!.Hits.Any(x => x.Offset == lateExpected),
                "real novel searches a phrase far beyond the loaded reading window", new { lateExpected, latePhraseSearchMs });
            watch.Restart();
            await window.SearchForAsync("克莱恩");
            long firstBatchSearchMs = watch.ElapsedMilliseconds;
            int expected = book.Text.IndexOf("克莱恩", StringComparison.OrdinalIgnoreCase);
            Check(expected >= 0 && window.CurrentSearchPage!.Hits.First().Offset == expected, "real novel full text name search", new { firstOffset = expected, resultsInBatch = window.CurrentSearchPage!.Hits.Count, window.CurrentSearchPage.HasMore });
            hit = window.CurrentSearchPage.Hits.Last();
            await window.JumpAsync(hit.Offset, hit.Length);
            var realMark = await window.AddBookmarkAsync();
            await reader.SetModeAsync(ReadingMode.Scroll);
            await reader.SetFontAsync(36);
            Check(reader.Anchor == hit.Offset && reader.Snapshot().AnchorVisible && reader.Snapshot().HighlightedCharacters == 3, "real novel search and bookmark target stays visible");
            await window.ReturnToReadingAsync();
            Check(reader.Anchor == returnPosition && reader.Snapshot().AnchorVisible, "real novel search return preserves reading place");
            await window.OpenLibraryBookAsync(first.BookId);
            await window.OpenLibraryBookAsync(book.ContentId);
            Check(reader.Anchor == returnPosition && reader.Snapshot().AnchorVisible, "real novel shelf reopen resumes position");
            await window.JumpToBookmarkAsync(realMark);
            await reader.SetModeAsync(ReadingMode.Page);
            await reader.SetFontAsync(24);
            Check(reader.Anchor == realMark.Offset && reader.Snapshot().AnchorVisible, "real novel persisted bookmark restores after book switch");
            foreach (var profile in new[] { new ReadingAppearance("KaiTi", "paper", 620), new ReadingAppearance("Microsoft YaHei", "night", 0) })
            foreach (var mode in new[] { ReadingMode.Page, ReadingMode.Scroll })
            {
                await reader.SetModeAsync(mode);
                await window.ApplyAppearanceAsync(profile);
                Check(reader.Anchor == realMark.Offset && reader.Snapshot().AnchorVisible,
                    $"real novel bookmark survives {profile.FontFamily} {profile.Theme} {profile.Width} {mode}");
            }
            await reader.SetModeAsync(ReadingMode.Page);
            foreach (var chapter in book.Chapters.Skip(1).Take(3).Concat(book.Chapters.Skip(book.Chapters.Count / 2).Take(2)))
            {
                await reader.NavigateAsync(chapter.Offset);
                Check(reader.Snapshot().FirstVisible == chapter.Offset && reader.Snapshot().AnchorVisible,
                    "real novel chapter starts new page", new { chapter.Title, chapter.Offset, firstVisible = reader.Snapshot().FirstVisible });
            }
            timings.Add(new { realBook = book.Title, bytes = new FileInfo(realPath).Length, openMs, firstBatchSearchMs, latePhraseSearchMs, chapters = book.Chapters.Count });
        }
    }
}
