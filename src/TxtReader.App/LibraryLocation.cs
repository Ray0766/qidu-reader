using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TxtReader.App;

/// <summary>Shared by packaged and unpackaged builds, outside MSIX AppData redirection.</summary>
internal static class LibraryLocation
{
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    // .NET's normal LocalApplicationData lookup can return the package's LocalCache.
    // Ask Windows for the user's original folder when locating an unpackaged library.
    internal static string PhysicalLocalAppData
    {
        get
        {
            Guid localAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
            int result = SHGetKnownFolderPath(in localAppData, 0x00010000, 0, out nint value); // KF_FLAG_NO_PACKAGE_REDIRECTION
            if (result == unchecked((int)0x80070057)) // Older Windows does not recognize this flag.
            {
                Marshal.FreeCoTaskMem(value);
                result = SHGetKnownFolderPath(in localAppData, 0, 0, out value);
            }
            try
            {
                Marshal.ThrowExceptionForHR(result);
                return Marshal.PtrToStringUni(value) ?? throw new IOException("无法定位旧版书库目录。");
            }
            finally { Marshal.FreeCoTaskMem(value); }
        }
    }
    internal static string LegacyFolder => Path.Combine(PhysicalLocalAppData, "TxtReaderPreview");
    internal static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "QiduLibrary");
    internal static string PrepareDefault() => Prepare(LegacyFolder, DefaultFolder);

    internal static string Prepare(string legacyFolder, string destination)
    {
        string source = Path.GetFullPath(legacyFolder).TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || Within(source, target) || Within(target, source))
            throw new ArgumentException("旧书库和新书库必须使用互不包含的独立目录。");
        RejectLinks(target);
        string key = RuntimeCompat.Sha256Hex(Encoding.UTF8.GetBytes(target.ToUpperInvariant()));
        using var gate = new Mutex(false, @"Local\Qidu.LibraryMigration." + key);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("另一个窗口正在准备书库，请稍后重试。");
        try
        {
            // A committed library is authoritative, even if an older preview is still present.
            if (File.Exists(Path.Combine(target, "reading.db"))) return target;
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                throw new IOException("新书库目录已有文件，未覆盖任何数据：" + target);
            if (!File.Exists(Path.Combine(source, "reading.db")))
            {
                Directory.CreateDirectory(target);
                return target;
            }
            RejectLinks(source);
            string stage = target + ".migration-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stage);
            try
            {
                var copies = new List<object>();
                using (var original = Open(Path.Combine(source, "reading.db"), SqliteOpenMode.ReadOnly))
                using (var snapshot = Open(Path.Combine(stage, "reading.db"), SqliteOpenMode.ReadWriteCreate))
                {
                    // Includes committed WAL pages without opening the user's database for writing.
                    original.BackupDatabase(snapshot);
                    Execute(snapshot, "PRAGMA journal_mode=DELETE");
                    var tables = new List<string>();
                    using (var command = snapshot.CreateCommand())
                    {
                        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('books','sessions')";
                        using var reader = command.ExecuteReader();
                        while (reader.Read()) tables.Add(reader.GetString(0));
                    }
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string table in tables)
                    {
                        using var command = snapshot.CreateCommand();
                        command.CommandText = $"SELECT DISTINCT path FROM {table}";
                        using var reader = command.ExecuteReader();
                        while (reader.Read()) paths.Add(reader.GetString(0));
                    }
                    using var transaction = snapshot.BeginTransaction();
                    foreach (string path in paths.Where(p => WindowsCompat.IsPathFullyQualified(p) && Within(p, source)))
                    {
                        RejectLinks(path);
                        string relative = WindowsCompat.GetRelativePath(source, path);
                        string copied = Path.Combine(stage, relative);
                        if (!File.Exists(path)) throw new FileNotFoundException("旧书库中的小说副本缺失；迁移已停止，原进度仍保留。", path);
                        Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
                        File.Copy(path, copied, false);
                        string hash = Hash(path);
                        if (hash != Hash(copied)) throw new IOException("小说副本校验失败：" + relative);
                        copies.Add(new { path = relative, sha256 = hash });
                        foreach (string table in tables)
                        {
                            using var update = snapshot.CreateCommand();
                            update.Transaction = transaction;
                            update.CommandText = $"UPDATE {table} SET path=$new WHERE path=$old COLLATE NOCASE";
                            update.Parameters.AddWithValue("$new", Path.Combine(target, relative));
                            update.Parameters.AddWithValue("$old", path);
                            update.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                    using var integrity = snapshot.CreateCommand();
                    integrity.CommandText = "PRAGMA integrity_check";
                    if ((string?)integrity.ExecuteScalar() != "ok") throw new IOException("迁移数据库校验失败，原书库保持不变。");
                }
                File.WriteAllText(Path.Combine(stage, "migration.json"), JsonSerializer.Serialize(new
                { version = 1, completedAt = DateTimeOffset.UtcNow, source, target, copies }, new JsonSerializerOptions { WriteIndented = true }));
                if (Directory.Exists(target)) Directory.Delete(target, false); // Empty directory only.
                Directory.Move(stage, target); // Same-volume, atomic commit after every file has passed validation.
                return target;
            }
            catch (Exception error)
            {
                // Keep staging for diagnosis; never delete the old library or replace a valid destination.
                throw new IOException($"书库迁移未完成，原书库未修改。临时副本：{stage}\n{error.Message}", error);
            }
        }
        finally { gate.ReleaseMutex(); }
    }

    internal static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    internal static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static string Hash(string path)
    { using var stream = File.OpenRead(path); return RuntimeCompat.Sha256Hex(stream); }
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("书库迁移不支持符号链接或重定向目录：" + current);
    }
}
