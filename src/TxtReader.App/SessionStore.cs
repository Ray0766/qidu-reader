using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TxtReader.Core;

namespace TxtReader.App;

public sealed record ReadingSession(string BookId, string Path, int Offset, string Mode, double FontSize, string EncodingName);
public sealed record ShelfBook(string BookId, string Title, string Path, string EncodingName, int Length,
    int Offset, string LastRead, bool HasRead)
{
    public double Progress => Math.Clamp(Offset * 100d / Math.Max(1, Length - 1), 0, 100);
    public string ProgressText => !HasRead ? "未开始" : Length == 0 ? "已读过" : $"已读 {Progress:F1}%";
    public string Detail => $"{EncodingName}    {(Length > 0 ? $"{Length:N0} 字符" : "打开后更新字数")}";
    public string LastReadText => HasRead && DateTimeOffset.TryParse(LastRead, out var time)
        ? "上次阅读 " + time.ToLocalTime().ToString("MM-dd HH:mm") : "刚加入书架";
}
public sealed record Bookmark(long Id, string BookId, int Offset, string Title, string Excerpt, string Created);

public sealed class SessionStore : IDisposable
{
    private readonly SqliteConnection _connection;
    public string Folder { get; }

    public SessionStore(string? folder = null)
    {
        Folder = folder ?? LibraryLocation.PrepareDefault();
        Directory.CreateDirectory(Folder);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Folder, "reading.db"), Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sessions (
              book_id TEXT PRIMARY KEY, path TEXT NOT NULL, offset INTEGER NOT NULL,
              mode TEXT NOT NULL, font_size REAL NOT NULL, encoding TEXT NOT NULL, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS books (
              book_id TEXT PRIMARY KEY, title TEXT NOT NULL, path TEXT NOT NULL, encoding TEXT NOT NULL,
              length INTEGER NOT NULL, added TEXT NOT NULL, archived INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS bookmarks (
              id INTEGER PRIMARY KEY AUTOINCREMENT, book_id TEXT NOT NULL, offset INTEGER NOT NULL,
              title TEXT NOT NULL, excerpt TEXT NOT NULL, created TEXT NOT NULL, UNIQUE(book_id,offset));
            CREATE INDEX IF NOT EXISTS bookmarks_by_book ON bookmarks(book_id,offset);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY,value TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        // Additive migration: old progress remains intact, including books not opened yet.
        using var legacy = _connection.CreateCommand();
        legacy.CommandText = "SELECT book_id,path,encoding,updated FROM sessions WHERE book_id NOT IN (SELECT book_id FROM books)";
        var records = new List<(string Id, string Path, string Encoding, string Updated)>();
        using (var reader = legacy.ExecuteReader())
            while (reader.Read()) records.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        using var transaction = _connection.BeginTransaction();
        foreach (var record in records)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO books VALUES($id,$title,$path,$encoding,0,$added,0)";
            insert.Parameters.AddWithValue("$id", record.Id);
            insert.Parameters.AddWithValue("$title", Path.GetFileNameWithoutExtension(record.Path));
            insert.Parameters.AddWithValue("$path", record.Path);
            insert.Parameters.AddWithValue("$encoding", record.Encoding);
            insert.Parameters.AddWithValue("$added", record.Updated);
            insert.ExecuteNonQuery();
        }
        using var version = _connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "PRAGMA user_version=3";
        version.ExecuteNonQuery();
        transaction.Commit();
    }

    public ReadingSession? Read(string? bookId = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT book_id,path,offset,mode,font_size,encoding FROM sessions " +
            (bookId is null ? "WHERE book_id IN (SELECT book_id FROM books WHERE archived=0) ORDER BY updated DESC LIMIT 1" : "WHERE book_id=$id");
        if (bookId is not null) command.Parameters.AddWithValue("$id", bookId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetDouble(4), reader.GetString(5)) : null;
    }

    public void Save(ReadingSession session)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions VALUES($id,$path,$offset,$mode,$font,$encoding,$updated)
            ON CONFLICT(book_id) DO UPDATE SET path=excluded.path,offset=excluded.offset,
            mode=excluded.mode,font_size=excluded.font_size,encoding=excluded.encoding,updated=excluded.updated;
            """;
        command.Parameters.AddWithValue("$id", session.BookId);
        command.Parameters.AddWithValue("$path", session.Path);
        command.Parameters.AddWithValue("$offset", session.Offset);
        command.Parameters.AddWithValue("$mode", session.Mode);
        command.Parameters.AddWithValue("$font", session.FontSize);
        command.Parameters.AddWithValue("$encoding", session.EncodingName);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ShelfBook> ListBooks(string filter = "", bool includeArchived = false)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT b.book_id,b.title,b.path,b.encoding,b.length,coalesce(s.offset,0),
                   coalesce(s.updated,b.added),s.book_id IS NOT NULL
            FROM books b LEFT JOIN sessions s ON s.book_id=b.book_id
            WHERE ($all=1 OR b.archived=0)
            ORDER BY coalesce(s.updated,b.added) DESC,b.title
            """;
        command.Parameters.AddWithValue("$all", includeArchived ? 1 : 0);
        var books = new List<ShelfBook>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = new ShelfBook(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetBoolean(7));
            if (item.Title.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)) books.Add(item);
        }
        return books;
    }

    public ShelfBook? FindBook(string id) => ListBooks(includeArchived: true).FirstOrDefault(x => x.BookId == id);

    public ShelfBook Register(BookDocument book, string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO books VALUES($id,$title,$path,$encoding,$length,$added,0)
            ON CONFLICT(book_id) DO UPDATE SET path=excluded.path,encoding=excluded.encoding,length=excluded.length,archived=0
            """;
        command.Parameters.AddWithValue("$id", book.ContentId);
        command.Parameters.AddWithValue("$title", book.Title);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$encoding", book.EncodingName);
        command.Parameters.AddWithValue("$length", book.Text.Length);
        command.Parameters.AddWithValue("$added", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return FindBook(book.ContentId)!;
    }

    public void RenameBook(string id, string title)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 120) throw new ArgumentException("书名应为 1–120 个字符。");
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE books SET title=$title WHERE book_id=$id";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void ArchiveBook(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE books SET archived=1 WHERE book_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Bookmark> ListBookmarks(string bookId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id,book_id,offset,title,excerpt,created FROM bookmarks WHERE book_id=$id ORDER BY offset";
        command.Parameters.AddWithValue("$id", bookId);
        using var reader = command.ExecuteReader();
        var result = new List<Bookmark>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }

    public Bookmark AddBookmark(BookDocument book, int offset)
    {
        offset = book.ClampOffset(offset);
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO bookmarks(book_id,offset,title,excerpt,created) VALUES($id,$offset,$title,$excerpt,$created)";
        command.Parameters.AddWithValue("$id", book.ContentId);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$title", $"{book.ChapterAt(offset).Title}  ·  {offset * 100d / Math.Max(1, book.Text.Length - 1):F1}%");
        command.Parameters.AddWithValue("$excerpt", book.Excerpt(offset, 90));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return ListBookmarks(book.ContentId).Single(x => x.Offset == offset);
    }

    public void DeleteBookmark(string bookId, long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM bookmarks WHERE id=$id AND book_id=$book";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$book", bookId);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    public ReadingAppearance ReadAppearance()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key='appearance'";
        if (command.ExecuteScalar() is not string json) return new();
        try { return (JsonSerializer.Deserialize<ReadingAppearance>(json) ?? new()).Normalize(); }
        catch (JsonException) { return new(); }
    }

    public void SaveAppearance(ReadingAppearance appearance)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO settings VALUES('appearance',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(appearance.Normalize()));
        command.ExecuteNonQuery();
    }
}
