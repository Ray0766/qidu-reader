using System.IO;
using Microsoft.Data.Sqlite;
using TxtReader.Core;

namespace TxtReader.App;

internal static class MigrationTests
{
    internal static async Task RunAsync(string folder, Action<bool, string, object?> check)
    {
        void Check(bool value, string name) => check(value, "library migration: " + name, null);
        Check(Path.IsPathFullyQualified(LibraryLocation.PhysicalLocalAppData) &&
            !LibraryLocation.PhysicalLocalAppData.Contains("\\Packages\\", StringComparison.OrdinalIgnoreCase), "legacy lookup resolves original user AppData");
        string source = Path.Combine(folder, "migration-source");
        string target = Path.Combine(folder, "migration-target");
        string input = Path.Combine(folder, "迁移测试.txt");
        await File.WriteAllTextAsync(input, "第一章 纸上星光\n" + new string('书', 4000));
        using (var original = new SessionStore(source))
        {
            var book = await new LibraryService(original).ImportAsync(input);
            var session = new ReadingSession(book.Document.ContentId, book.Shelf.Path, 1234, "Scroll", 28, "UTF-8");
            original.Save(session);
            original.AddBookmark(book.Document, 1200);
            original.RenameBook(book.Document.ContentId, "保留我的书名");
            var appearance = new ReadingAppearance("KaiTi", "paper", 1000, "double", "curl", false);
            original.SaveAppearance(appearance);
            // Leave the source connection and WAL open: a file-copy-only migration would miss these rows.
            Check(LibraryLocation.Prepare(source, target) == target, "WAL snapshot succeeds with source connection open");
            using var migrated = new SessionStore(target);
            var restored = migrated.Read()!;
            Check(restored == session with { Path = Path.Combine(target, Path.GetRelativePath(source, session.Path)) }, "exact book ID, anchor, mode, font and encoding survive");
            Check(File.ReadAllBytes(restored.Path).SequenceEqual(File.ReadAllBytes(session.Path)), "managed Chinese filename and book bytes survive");
            Check(migrated.ListBooks().Single().Title == "保留我的书名", "renamed shelf title survives");
            Check(migrated.ListBookmarks(restored.BookId).Single().Offset == 1200, "bookmark survives");
            Check(migrated.ReadAppearance() == appearance, "all appearance settings survive");
            Check(original.Read() == session && original.ReadAppearance() == appearance, "source progress and settings remain unchanged");
            Check(File.Exists(Path.Combine(target, "migration.json")), "commit receipt exists");
            migrated.Save(restored with { Offset = 2345 });
            LibraryLocation.Prepare(source, target);
            Check(migrated.Read()!.Offset == 2345, "newer destination is never replaced by old source");
        }
        using (var reopened = new SessionStore(LibraryLocation.Prepare(source, target)))
            Check(reopened.Read()!.Offset == 2345, "restart retains latest destination progress");
        string fresh = Path.Combine(folder, "migration-fresh");
        using (var empty = new SessionStore(LibraryLocation.Prepare(Path.Combine(folder, "no-legacy"), fresh)))
            Check(empty.ListBooks().Count == 0, "first install without legacy data creates empty shelf");
        string occupied = Path.Combine(folder, "migration-occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "keep");
        bool rejected = false;
        try { LibraryLocation.Prepare(source, occupied); } catch (IOException) { rejected = true; }
        Check(rejected && File.ReadAllText(Path.Combine(occupied, "keep.txt")) == "keep", "unknown destination content is not overwritten");
        string broken = Path.Combine(folder, "migration-missing");
        string failedTarget = Path.Combine(folder, "migration-failed-target");
        using (var missing = new SessionStore(broken))
        {
            var doc = new BookDocument("Missing", "小说正文");
            string missingPath = Path.Combine(broken, "books", "missing.txt");
            missing.Register(doc, missingPath);
            missing.Save(new(doc.ContentId, missingPath, 2, "Page", 24, "UTF-8"));
        }
        string before = LibraryLocation.Hash(Path.Combine(broken, "reading.db"));
        rejected = false;
        try { LibraryLocation.Prepare(broken, failedTarget); } catch (IOException) { rejected = true; }
        Check(rejected && !Directory.Exists(failedTarget), "missing book aborts before atomic commit");
        Check(before == LibraryLocation.Hash(Path.Combine(broken, "reading.db")), "failed migration leaves original database byte-identical");
        string interrupted = failedTarget + ".migration-interrupted";
        Directory.CreateDirectory(interrupted);
        File.WriteAllText(Path.Combine(interrupted, "partial"), "incomplete");
        Directory.CreateDirectory(Path.Combine(broken, "books"));
        File.WriteAllText(Path.Combine(broken, "books", "missing.txt"), "小说正文");
        LibraryLocation.Prepare(broken, failedTarget);
        Check(File.Exists(Path.Combine(failedTarget, "reading.db")), "retry ignores an incomplete staging directory");
        rejected = false;
        try { LibraryLocation.Prepare(source, Path.Combine(source, "nested")); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "nested migration roots are rejected");
    }
}
