using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Data.Sqlite;
using TxtReader.Core;

namespace TxtReader.App;

/// <summary>Explicit QA entry point. Its fixed test roots cannot address the user's real library.</summary>
internal static class PackageProbe
{
    internal sealed record Config(string RunId, string Stage, bool Advance, string ReportFolder, bool Standalone = false);
    private static Config? _config;
    private static object? _before;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

    internal static string? Prepare(string[] args)
    {
        int index = Array.IndexOf(args, "--package-probe");
        if (index < 0) return null;
        _config = JsonSerializer.Deserialize<Config>(File.ReadAllText(args[index + 1]))!;
        if (!Guid.TryParseExact(_config.RunId, "N", out _) || _config.Stage.Any(c =>
            !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("Invalid isolated package probe configuration.");
        string legacy = Path.Combine(LibraryLocation.PhysicalLocalAppData, "Qidu-Validation", _config.RunId, "legacy");
        string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Qidu-Validation", _config.RunId, "library");
        Directory.CreateDirectory(_config.ReportFolder);
        File.WriteAllText(Path.Combine(_config.ReportFolder, _config.Stage + "-startup.json"), JsonSerializer.Serialize(new
        {
            legacy, target, normalLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            physicalLocalAppData = LibraryLocation.PhysicalLocalAppData, processId = WindowsCompat.ProcessId
        }, Json));
        LibraryLocation.Prepare(legacy, target);
        using (var initialized = new SessionStore(target)) { }
        _before = Snapshot(target);
        return target;
    }

    internal static bool ReportStartupFailure(string[] args, Exception error)
    {
        int index = Array.IndexOf(args, "--package-probe");
        if (index < 0) return false;
        var config = _config ?? JsonSerializer.Deserialize<Config>(File.ReadAllText(args[index + 1]))!;
        Directory.CreateDirectory(config.ReportFolder);
        File.WriteAllText(Path.Combine(config.ReportFolder, config.Stage + "-startup-error.json"),
            JsonSerializer.Serialize(new { passed = false, failure = error.ToString(), processId = WindowsCompat.ProcessId }, Json));
        return true;
    }

    internal static async Task RunAsync(MainWindow window)
    {
        if (_config is null) return;
        var checks = new List<object>();
        string? failure = null;
        void Check(bool passed, string name, object? detail = null)
        { checks.Add(new { name, passed, detail }); if (!passed) throw new InvalidOperationException(name); }
        string? package = null;
        try
        {
            if (!_config.Standalone)
            {
                uint length = 0;
                int result = GetCurrentPackageFullName(ref length, null);
                if (result == 122)
                {
                    var name = new StringBuilder((int)length);
                    if (GetCurrentPackageFullName(ref length, name) == 0) package = name.ToString();
                }
                Check(package is not null && package.StartsWith("Qidu.InstallValidation_", StringComparison.Ordinal), "running with installed validation MSIX identity", package);
            }
            Check(window.Store.Folder.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Qidu-Validation") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "isolated library outside AppData", window.Store.Folder);
            if (window.ReadingSurface.Book is null)
            {
                Check(window.Store.ListBooks().Count == 0, "fresh installation opens an empty shelf");
                await window.OpenFileAsync(LibraryService.DemoPath, "UTF-8");
                await window.ReadingSurface.NavigateAsync(1200);
                window.SavePosition(true);
                Check(window.ReadingSurface.Book?.Title == "山海来信", "first import opens bundled Chinese sample");
            }
            var session = window.Store.Read()!;
            var reader = window.ReadingSurface;
            Check(reader.Book?.ContentId == session.BookId && reader.Anchor == session.Offset && reader.Snapshot().AnchorVisible, "restored reading anchor is visible", reader.Snapshot());
            int anchor = reader.Anchor;
            var appearance = window.Appearance;
            double font = reader.Snapshot().FontSize;
            var mode = reader.Mode;
            foreach (string layout in new[] { "single", "double" })
            {
                await window.ApplyAppearanceAsync(appearance with { PageLayout = layout });
                await reader.SetModeAsync(ReadingMode.Page);
                await reader.AdjustFontAsync(2);
                Check(reader.Anchor == anchor && reader.Snapshot().AnchorVisible, layout + " font change preserves visible anchor");
                await reader.SetModeAsync(ReadingMode.Scroll);
                Check(reader.Anchor == anchor && reader.Snapshot().AnchorVisible, layout + " to scrolling preserves visible anchor");
            }
            await reader.AdjustFontAsync(font - reader.Snapshot().FontSize);
            await window.ApplyAppearanceAsync(appearance);
            await reader.SetModeAsync(mode);
            if (_config.Advance)
            {
                await reader.NavigateAsync(Math.Min(anchor + 321, reader.Book!.Text.Length - 1));
                await window.AddBookmarkAsync();
                // A new book imported after packaging must also survive update and removal.
                string input = Path.Combine(_config.ReportFolder, "安装后加入.txt");
                Directory.CreateDirectory(_config.ReportFolder);
                await RuntimeCompat.WriteAllTextAsync(input, "第一章 新的书\n这是安装后新加入的测试小说。\n" + new string('新', 800));
                var imported = await new LibraryService(window.Store).ImportAsync(input);
                Check(File.Exists(imported.Shelf.Path) && LibraryLocation.Within(imported.Shelf.Path, window.Store.Folder), "new import is stored in persistent library");
            }
            window.SavePosition(true);
            Check(window.Store.Read()!.Offset == reader.Anchor, "latest reading progress saved exactly", reader.Anchor);
        }
        catch (Exception error) { failure = error.ToString(); }
        Directory.CreateDirectory(_config.ReportFolder);
        File.WriteAllText(Path.Combine(_config.ReportFolder, _config.Stage + ".json"), JsonSerializer.Serialize(new
        {
            passed = failure is null, package, stage = _config.Stage, processId = WindowsCompat.ProcessId,
            framework = RuntimeInformation.FrameworkDescription, is64BitProcess = Environment.Is64BitProcess,
            executable = WindowsCompat.ProcessPath, library = window.Store.Folder, before = _before,
            after = Snapshot(window.Store.Folder), checks, failure
        }, Json));
        window.Close();
    }

    private static object Snapshot(string folder)
    {
        using var connection = LibraryLocation.Open(Path.Combine(folder, "reading.db"), SqliteOpenMode.ReadOnly);
        var tables = new Dictionary<string, List<object?[]>>();
        foreach (string table in new[] { "books", "sessions", "bookmarks", "settings" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY 1";
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount]; reader.GetValues(values);
                rows.Add(values.Select(value => value is string text ? text.Replace(folder, "$LIBRARY", StringComparison.OrdinalIgnoreCase) : value is DBNull ? null : value).ToArray());
            }
            tables[table] = rows;
        }
        return tables;
    }
}
