using System.IO;
using System.Windows;
using System.Windows.Threading;
using TxtReader.Core;

namespace TxtReader.App;

internal static class SpreadTests
{
    public static async Task RunAsync(MainWindow window, Action<bool, string, object?> check)
    {
        void Check(bool ok, string name, object? detail = null) => check(ok, name, detail);
        var reader = window.ReadingSurface;
        var demo = await BookLoader.LoadAsync(LibraryService.DemoPath);
        var book = new BookDocument("双页连续性", string.Concat(Enumerable.Repeat(demo.Text + "\n", 6)));
        await window.LoadTestBookAsync(book);
        await window.SetReadingViewAsync(ReadingMode.Page, "double");
        await window.ApplyAppearanceAsync(new("SimSun", "paper", 0, "double", "none", false));
        await reader.NavigateAsync(0);
        var initial = reader.Snapshot();
        Check(initial.VisiblePages == 2 && initial.VisiblePageStarts?.Length == 2 && reader.PageViewsReady, "two actual page views are rendered", initial);
        int crossed = 0, turns = 0;
        for (; turns < 160 && crossed < 2; turns++)
        {
            var before = reader.Snapshot();
            await reader.TurnAsync(1);
            var after = reader.Snapshot();
            if (after.Chunk != before.Chunk) crossed++;
            Check(after.FirstVisible >= before.LastVisible && after.FirstVisible - before.LastVisible <= 2 && after.AnchorVisible && reader.PageViewsReady,
                "consecutive double page source ranges " + turns, new { before, after });
            await reader.TurnAsync(-1);
            Check(reader.Snapshot().FirstVisible == before.FirstVisible && reader.Snapshot().AnchorVisible,
                "double page previous reverses forward " + turns);
            await reader.TurnAsync(1);
        }
        Check(crossed == 2, "double page navigation crosses two content chunk boundaries", new { crossed, turns });
        var spread = reader.Snapshot();
        // Stay inside the right page: splitting a WPF Run for a search highlight
        // can shift a boundary line, while preserving the exact source anchor.
        int target = (spread.VisiblePageStarts![1] + spread.LastVisible) / 2;
        await reader.NavigateAsync(target, 2);
        Check(reader.Snapshot().AnchorVisible && reader.Snapshot().AnchorPage == reader.Snapshot().Page + 1, "anchor can live on the right page", reader.Snapshot());
        foreach (var view in new[] { "single", "double", "scroll", "double" })
        {
            await window.SetReadingViewAsync(view == "scroll" ? ReadingMode.Scroll : ReadingMode.Page, view == "scroll" ? null : view);
            foreach (int font in new[] { 16, 30, 22 })
            {
                await reader.SetFontAsync(font);
                await window.SetSidebarVisibleAsync(font == 30);
                Check(reader.Anchor == target && reader.Snapshot().AnchorVisible && reader.Snapshot().HighlightedCharacters == 2,
                    $"right-page anchor survives {view}, font {font} and sidebar", reader.Snapshot());
            }
        }
        foreach (var size in new[] { (1040d, 680d), (1400d, 900d) })
        {
            window.Width = size.Item1; window.Height = size.Item2;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await reader.RefreshLayoutAsync();
            Check(reader.Anchor == target && reader.Snapshot().AnchorVisible && reader.PageViewsReady, "double page resize preserves right-page anchor", reader.Snapshot());
        }
        foreach (var chapter in book.Chapters.Skip(1).Take(8))
        {
            await reader.NavigateAsync(chapter.Offset);
            var shot = reader.Snapshot();
            Check(shot.VisiblePageStarts!.Contains(chapter.Offset) && shot.AnchorVisible,
                "chapter starts an individual page in a spread", new { chapter.Offset, shot.VisiblePageStarts });
        }
        await reader.NavigateAsync(book.Text.Length - 1);
        var end = reader.Snapshot();
        await reader.TurnAsync(1);
        Check(reader.Anchor == end.Anchor && reader.Snapshot().AnchorVisible, "last spread cannot turn beyond end");
        await reader.NavigateAsync(0);
        await reader.TurnAsync(-1);
        Check(reader.Anchor == 0, "first spread cannot turn before beginning");

        reader.AnimationsEnabled = true;
        foreach (var effect in ReadingAppearance.Effects)
        {
            await window.ApplyAppearanceAsync(window.Appearance with { TurnEffect = effect.Key });
            await reader.NavigateAsync(0);
            await reader.TurnAsync(1);
            var after = reader.Snapshot();
            Check(after.Anchor > 0 && after.AnchorVisible && !reader.AnimationLayerVisible && !reader.IsBusy,
                "page effect completes and releases input " + effect.Key, new { reader.LastAnimationFrames, after.Anchor });
            Check(effect.Key == "none" || !SystemParameters.ClientAreaAnimation ? reader.LastAnimationFrames == 0 : reader.LastAnimationFrames > 1,
                "page effect renders only when enabled " + effect.Key, new { reader.LastAnimationFrames });
            await reader.TurnAsync(-1);
            Check(reader.Anchor == 0 && !reader.AnimationLayerVisible, "reverse page effect restores first spread " + effect.Key);
        }
        await window.ApplyAppearanceAsync(window.Appearance with { TurnEffect = "curl" });
        await reader.NavigateAsync(0);
        await Task.WhenAll(reader.TurnAsync(1), reader.TurnAsync(1), reader.SetFontAsync(26));
        Check(reader.Anchor > 0 && reader.Snapshot().AnchorVisible && reader.ReadingFontSize == 26 && !reader.AnimationLayerVisible,
            "queued turns and typography finish with valid content anchor");
        reader.AnimationsEnabled = false;
        using (var persisted = new SessionStore(window.Store.Folder))
            Check(persisted.ReadAppearance().PageLayout == "double" && persisted.ReadAppearance().TurnEffect == "curl" && !persisted.ReadAppearance().SidebarVisible,
                "spread motion and hidden sidebar preferences survive reopen");

        if (window.TestBookPath is { } realPath)
        {
            await window.OpenFileAsync(realPath);
            await window.SetReadingViewAsync(ReadingMode.Page, "double");
            var real = reader.Book!;
            int realTarget = real.ClampOffset((int)(real.Text.Length * .67));
            await reader.NavigateAsync(realTarget);
            var bookmark = await window.AddBookmarkAsync();
            await reader.TurnAsync(1);
            await window.JumpToBookmarkAsync(bookmark);
            await reader.SetFontAsync(30);
            await window.SetSidebarVisibleAsync(true);
            Check(reader.Anchor == realTarget && reader.Snapshot().AnchorVisible && reader.VisiblePages == 2 && reader.PageViewsReady,
                "real novel double page bookmark survives typography and sidebar", reader.Snapshot());
            await window.SetReadingViewAsync(ReadingMode.Scroll);
            await window.SetReadingViewAsync(ReadingMode.Page, "single");
            await window.SetReadingViewAsync(ReadingMode.Page, "double");
            Check(reader.Anchor == realTarget && reader.Snapshot().AnchorVisible, "real novel survives scroll single and double transitions");
        }
    }
}
