using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TxtReader.Core;

namespace TxtReader.App;

internal static class PrototypeTests
{
    public static async Task RunAsync(MainWindow window, string folder)
    {
        Directory.CreateDirectory(folder);
        var checks = new List<object>();
        var timings = new List<object>();
        string? failure = null;
        void Check(bool ok, string name, object? detail = null)
        {
            checks.Add(new { name, passed = ok, detail });
            File.WriteAllText(Path.Combine(folder, "in-progress.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            if (!ok) throw new InvalidOperationException(name);
        }
        try
        {
            var dpi = DesktopProbe.Dpi(window);
            Check(dpi.ExpectedScale is null || Math.Abs(dpi.WpfScale - dpi.ExpectedScale.Value) < .01 &&
                Math.Abs(dpi.NativeDpi / 96d - dpi.ExpectedScale.Value) < .01, "actual Windows and WPF DPI match requested scale", dpi);
            timings.Add(dpi);
            // Independent known text, including supplementary characters and mixed newlines.
            const string known = "第一章 春水\r\n林舟走过渡口。\r\n繁體與简体，𠮷与🙂，e\u0301。\r第二章 风声\n两字人名：林舟。";
            foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
            {
                byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(known)];
                var decoded = BookLoader.Decode(bytes);
                Check(decoded.Text == known, "BOM decode " + encoding.WebName);
            }
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            const string chinese = "第一章 海边\n风吹过长街，林舟在桥下等候。";
            Check(BookLoader.Decode(Encoding.GetEncoding("GB18030").GetBytes(chinese)).Text == chinese, "GB18030 automatic decode");
            const string traditional = "第一章 海邊\n風吹過長街，林舟在橋下等候。";
            Check(BookLoader.Decode(Encoding.GetEncoding("Big5").GetBytes(traditional), "Big5").Text == traditional, "Big5 manual decode");
            var mixed = new BookDocument("mixed", known);
            Check(!mixed.Text.Contains('\r') && mixed.Chapters.Count == 2, "newlines and chapter index");

            var demo = await BookLoader.LoadAsync(LibraryService.DemoPath);
            await window.LoadTestBookAsync(demo);
            var reader = window.ReadingSurface;
            Check(reader.Snapshot().AnchorVisible, "initial page visible", reader.Snapshot());
            for (int i = 0; i < 8; i++) await reader.TurnAsync(1);
            int target = reader.Anchor;
            Check(target > 500, "page navigation advances content", reader.Snapshot());
            string expected = demo.Excerpt(target);
            foreach (var mode in new[] { ReadingMode.Scroll, ReadingMode.Page, ReadingMode.Scroll, ReadingMode.Page })
            {
                await reader.SetModeAsync(mode);
                var shot = reader.Snapshot();
                Check(shot.Anchor == target && shot.AnchorVisible && shot.Excerpt == expected, "mode preserves actual visible content " + mode, shot);
                foreach (int font in new[] { 30, 16, 24, 22 })
                {
                    await reader.SetFontAsync(font);
                    shot = reader.Snapshot();
                    Check(shot.Anchor == target && shot.AnchorVisible, $"font {font} preserves actual visible content {mode}", shot);
                }
            }
            foreach (var mode in new[] { ReadingMode.Page, ReadingMode.Scroll })
            {
                await reader.SetModeAsync(mode);
                foreach (var size in new[] { (980d, 680d), (1180d, 800d) })
                {
                    window.Width = size.Item1; window.Height = size.Item2;
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    await Task.Delay(180);
                    await reader.RefreshLayoutAsync();
                    var shot = reader.Snapshot();
                    Check(shot.Anchor == target && shot.AnchorVisible, $"resize {size} preserves actual visible content {mode}", shot);
                }
            }

            // Pages must cover adjacent source ranges without reversing or skipping a chunk.
            var navigationBook = new BookDocument("Boundary fixture", string.Concat(Enumerable.Repeat(demo.Text + "\n", 4)));
            await window.LoadTestBookAsync(navigationBook);
            await reader.SetModeAsync(ReadingMode.Page);
            await reader.NavigateAsync(0);
            int crossed = 0;
            for (int i = 0; i < 160 && crossed < 2; i++)
            {
                var before = reader.Snapshot();
                await reader.TurnAsync(1);
                var after = reader.Snapshot();
                if (before.Chunk != after.Chunk) crossed++;
                if (after.Anchor == before.Anchor) break;
                Check(after.FirstVisible >= before.LastVisible && after.FirstVisible - before.LastVisible <= 2 && after.AnchorVisible,
                    "consecutive page source ranges " + i, new { before, after });
            }
            Check(crossed > 0, "pagination crosses content chunks");
            var forward = reader.Snapshot();
            await reader.TurnAsync(-1);
            Check(reader.Anchor < forward.Anchor, "backward page navigation");

            await reader.SetModeAsync(ReadingMode.Scroll);
            await reader.NavigateAsync(Math.Max(0, navigationBook.Chunks[1].Start - 200));
            for (int i = 0; i < 4; i++) await reader.TurnAsync(1);
            target = reader.Anchor;
            Check(reader.Snapshot().AnchorVisible && target >= navigationBook.Chunks[1].Start, "scroll crosses chunk boundary", reader.Snapshot());
            await reader.SetModeAsync(ReadingMode.Page);
            await reader.SetFontAsync(28);
            Check(reader.Anchor == target && reader.Snapshot().AnchorVisible, "scroll boundary to page plus font", reader.Snapshot());

            using (var store = new SessionStore(Path.Combine(folder, "persistence")))
            {
                store.Save(new(navigationBook.ContentId, "fixture.txt", target, "Page", 28, "UTF-8"));
            }
            using (var reopened = new SessionStore(Path.Combine(folder, "persistence")))
            {
                var saved = reopened.Read(navigationBook.ContentId)!;
                await reader.OpenAsync(navigationBook, saved.Offset, Enum.Parse<ReadingMode>(saved.Mode), saved.FontSize);
                Check(reader.Anchor == target && reader.Snapshot().AnchorVisible && reader.ReadingFontSize == 28, "SQLite reopen resumes visible content", reader.Snapshot());
            }

            foreach (int mib in new[] { 10, 50 })
            {
                string path = Path.Combine(folder, $"fixture-{mib}MiB.txt");
                await Task.Run(() =>
                {
                    using var output = new StreamWriter(path, false, new UTF8Encoding(false));
                    string paragraph = "长街的雨停了。林舟收起伞，看见桥头的灯光照着一封旧信。走过石阶，他又想起昨天听到的海潮声。\n";
                    for (int i = 0; output.BaseStream.Position < mib * 1024L * 1024; i++)
                    {
                        output.WriteLine($"第{i + 1}章 长街来信");
                        for (int j = 0; j < 40; j++) output.Write(paragraph);
                    }
                });
                var watch = Stopwatch.StartNew();
                var large = await BookLoader.LoadAsync(path);
                long parseMs = watch.ElapsedMilliseconds;
                await window.LoadTestBookAsync(large, large.Text.Length * 2 / 3);
                long displayedMs = watch.ElapsedMilliseconds;
                target = reader.Anchor;
                await reader.SetModeAsync(ReadingMode.Scroll);
                await reader.SetFontAsync(30);
                await reader.SetModeAsync(ReadingMode.Page);
                var shot = reader.Snapshot();
                Check(shot.Anchor == target && shot.AnchorVisible && shot.LoadedCharacters < 60000, $"{mib} MiB bounded view and position", shot);
                timings.Add(new { fileMiB = mib, parseMs, firstLayoutMs = displayedMs, processWorkingSetMiB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024, snapshot = shot });
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        if (failure is null)
        {
            try
            {
                await MigrationTests.RunAsync(folder, Check);
                await FeatureTests.RunAsync(window, folder, Check, timings);
                await SpreadTests.RunAsync(window, Check);
                await DragTests.RunAsync(window, folder, Check, timings);
                await NavigationInputTests.RunAsync(window, Check);
                await BodyDragTests.RunAsync(window, folder, Check);
                await ChapterFollowTests.RunAsync(window, folder, Check);
            }
            catch (Exception ex) { failure = ex.ToString(); }
        }
        var report = new { passed = failure is null, timestamp = DateTimeOffset.Now, os = Environment.OSVersion.ToString(), framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, checks, timings, failure };
        File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(failure is null ? 0 : 1);
    }
}
