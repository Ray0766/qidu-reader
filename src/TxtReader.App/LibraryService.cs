using System.IO;
using TxtReader.Core;

namespace TxtReader.App;

public sealed record ImportedBook(ShelfBook Shelf, BookDocument Document, bool AlreadyPresent);

public sealed class LibraryService(SessionStore store)
{
    internal static string DemoPath => Path.Combine(AppContext.BaseDirectory, "samples", "sample-novel.txt");
    public async Task<ImportedBook> ImportAsync(string path, string encoding = "自动")
    {
        var book = await BookLoader.LoadAsync(path, encoding);
        if (Path.GetFullPath(path).Equals(DemoPath, StringComparison.OrdinalIgnoreCase))
            book = new BookDocument("山海来信", book.Text, book.EncodingName, book.SourcePath);
        var existing = store.FindBook(book.ContentId);
        string managedPath = existing is not null && File.Exists(existing.Path)
            ? existing.Path : Path.Combine(store.Folder, "books", book.ContentId, Path.GetFileName(path));
        if (!Path.GetFullPath(path).Equals(Path.GetFullPath(managedPath), StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
                if (!File.Exists(managedPath)) File.Copy(path, managedPath);
            });
        }
        // If the same decoded text was imported using another encoding, the existing
        // managed copy still uses its original encoding. Keep those metadata together.
        if (existing is not null && File.Exists(existing.Path))
        {
            if (existing.EncodingName != book.EncodingName)
                book = new BookDocument(book.Title, book.Text, existing.EncodingName, existing.Path);
        }
        if (book.Cover is { } cover)
        {
            string coverPath = Path.Combine(Path.GetDirectoryName(managedPath)!, ".qidu-cover");
            if (!File.Exists(coverPath)) await Task.Run(() => File.WriteAllBytes(coverPath, cover.Data));
        }
        return new(store.Register(book, managedPath), book, existing is not null);
    }
}
