using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TxtReader.Core;

namespace TxtReader.App;

internal static class DragTests
{
    internal static async Task RunAsync(MainWindow window, string folder, Action<bool, string, object?> check, List<object> timings)
    {
        void Check(bool ok, string name, object? detail = null) => check(ok, "drag: " + name, detail);
        var reader = window.ReadingSurface;
        var demo = await BookLoader.LoadAsync(LibraryService.DemoPath);
        var book = new BookDocument("页角边界", string.Concat(Enumerable.Repeat(demo.Text + "\n", 6)));
        await window.LoadTestBookAsync(book);
        await window.SetReadingViewAsync(ReadingMode.Page, "double");
        await window.ApplyAppearanceAsync(new("SimSun", "paper", 0, "double", "curl", false));
        await reader.SetFontAsync(22);
        await reader.NavigateAsync(5000);

        // Independent geometric invariants: both spine endpoints stay fixed and the grab follows its constrained target.
        foreach (bool top in new[] { false, true })
        foreach (Point target in new[] { new Point(499, 650), new Point(300, 490), new Point(0, 480), new Point(-250, 560), new Point(-900, 1500) })
        {
            var curl = new PaperCurl(500, 650, top, top ? new Point(target.X, 650 - target.Y) : target);
            var grip = curl.Map(500, top ? 0 : 650);
            var a = curl.Map(0, 0); var b = curl.Map(0, 650);
            Check(Math.Abs(a.X) + Math.Abs(a.Y) + Math.Abs(a.Z) + Math.Abs(b.X) + Math.Abs(b.Y - 650) + Math.Abs(b.Z) < .01 &&
                (new Point(grip.X, grip.Y) - curl.Target).Length < .01 && double.IsFinite(grip.Z),
                $"fixed spine and constrained grab {top} {target}", new { a, b, grip, curl.Target, curl.Radius });
        }
        foreach (double scale in new[] { 1d, 1.5, 2d })
        {
            var logical = new Point(980 * scale / scale, 630 * scale / scale);
            Check(PageDragMath.Corner(logical, new Size(1000, 650), 2) == (1, false) &&
                PageDragMath.Corner(new Point(500, 640), new Size(1000, 650), 2) is null,
                $"corner hit zones use DIPs at {scale:P0} coordinate scale");
        }
        Point Start(int dir, bool top) => new(dir > 0 ? reader.ActualWidth - 2 : 2, top ? 2 : reader.ActualHeight - 2);
        Point Pull(Point start, int dir, bool top, double fraction, double rise = 70) =>
            new(start.X - dir * fraction * reader.DragTravel, start.Y + (top ? rise : -rise));
        async Task<Point> Begin(int dir = 1, bool top = false)
        {
            await reader.FlushPendingInputAsync();
            var start = Start(dir, top);
            Check(reader.BeginCornerDrag(start), $"available corner starts {dir} {top}", reader.Snapshot());
            return start;
        }
        async Task Preview(Point point)
        {
            reader.UpdateCornerDrag(point);
            await reader.WaitForDragPreviewAsync();
            await Task.Delay(40);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Check(reader.DragPreviewReady && reader.CornerDragActive, "adjacent page preview ready");
        }
        void Capture(string name)
        {
            var bitmap = PageTurnLayer.Capture(reader);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
        }
        var original = reader.Snapshot();
        Check(!reader.BeginCornerDrag(new Point(reader.ActualWidth / 2, reader.ActualHeight / 2)), "body does not initiate a turn");
        var tap = await Begin();
        reader.UpdateCornerDrag(tap + new Vector(-3, -2));
        Check(!reader.DragPreviewReady, "movement under 6 DIPs is a click");
        await reader.ReleaseCornerDragAsync(tap);
        Check(reader.Anchor == original.Anchor && !reader.CornerDragActive, "click alone leaves position unchanged");

        // All eight grip combinations exercise both rebound and a committed turn against the normal navigation result.
        foreach (string layout in new[] { "single", "double" })
        foreach (int direction in new[] { 1, -1 })
        foreach (bool top in new[] { false, true })
        {
            reader.AnimationsEnabled = false;
            await window.SetReadingViewAsync(ReadingMode.Page, layout);
            await reader.NavigateAsync(5000, 2);
            var before = reader.Snapshot();
            await reader.TurnAsync(direction);
            var expected = reader.Snapshot();
            await reader.NavigateAsync(before.Anchor, 2);
            reader.AnimationsEnabled = true;
            int events = 0;
            EventHandler count = (_, _) => events++;
            reader.PositionChanged += count;
            try
            {
                var start = await Begin(direction, top);
                await Preview(Pull(start, direction, top, .4));
                Check(reader.Anchor == before.Anchor && reader.Snapshot().FirstVisible == before.FirstVisible && events == 0,
                    $"{layout} {direction} {top} preview cannot change position");
                if (SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0)
                {
                    var corner = reader.DragProjectedCorner;
                    int frames = reader.LastAnimationFrames;
                    await Task.Delay(100);
                    Check(reader.DragProjectedCorner == corner && reader.LastAnimationFrames == frames,
                        "stationary mouse holds the curl without a timed flip");
                    reader.UpdateCornerDrag(Pull(start, direction, top, .4, 130));
                    await Task.Delay(40);
                    Check(reader.DragProjectedCorner != corner && reader.DragCurlRadius > 0, "vertical motion bends the sheet");
                }
                Capture($"drag-{layout}-{direction}-{(top ? "top" : "bottom")}");
                await reader.ReleaseCornerDragAsync(Pull(start, direction, top, .4));
                Check(reader.Anchor == before.Anchor && reader.Snapshot().AnchorVisible && reader.PageViewsReady && events == 0 && !reader.IsBusy,
                    $"{layout} {direction} {top} short pull rebounds without commit", reader.Snapshot());
                start = await Begin(direction, top);
                await Preview(Pull(start, direction, top, .7));
                await reader.ReleaseCornerDragAsync(Pull(start, direction, top, .7));
                var after = reader.Snapshot();
                Check(after.FirstVisible == expected.FirstVisible && after.LastVisible == expected.LastVisible && after.AnchorVisible &&
                    reader.PageViewsReady && events == 1 && !reader.AnimationLayerVisible,
                    $"{layout} {direction} {top} complete commits exactly one turn", new { expected, after, events });
                timings.Add(new { dragLayout = layout, direction, top, preparationMs = reader.LastDragPreparationMs,
                    settlementMs = reader.LastDragSettleMs, geometryCpuMaxMs = reader.DragMaxFrameMs });
            }
            finally { reader.PositionChanged -= count; reader.CancelPageDrag(); }
        }
        reader.AnimationsEnabled = false;
        await window.SetReadingViewAsync(ReadingMode.Page, "double");
        await reader.NavigateAsync(5000);
        var spread = reader.Snapshot();
        int targetOffset = (spread.VisiblePageStarts![1] + spread.LastVisible) / 2;
        await reader.NavigateAsync(targetOffset, 2);
        original = reader.Snapshot();
        reader.AnimationsEnabled = true;
        var heldStart = await Begin();
        foreach (double fraction in new[] { .1, .4, .6 })
        {
            await Preview(Pull(heldStart, 1, false, fraction));
            int frames = reader.LastAnimationFrames;
            var corner = reader.DragProjectedCorner;
            await Task.Delay(1000);
            Check(reader.Anchor == targetOffset && reader.LastAnimationFrames == frames && reader.DragProjectedCorner == corner,
                $"one-second hold at {fraction:P0} remains under pointer control");
            Capture($"hold-{(int)(fraction * 100)}");
        }
        reader.CancelPageDrag();
        foreach (bool complete in new[] { false, true })
        {
            await reader.NavigateAsync(targetOffset);
            heldStart = await Begin();
            var release = Pull(heldStart, 1, false, complete ? .6 : .4);
            await Preview(release);
            string prefix = complete ? "landing" : "rebound";
            Capture(prefix + "-0");
            var animation = reader.ReleaseCornerDragAsync(release);
            await Task.Delay(80); Capture(prefix + "-1");
            await Task.Delay(80); Capture(prefix + "-2");
            await animation; Capture(prefix + "-3");
            Check(!reader.IsBusy && (complete ? reader.Anchor > targetOffset : reader.Anchor == targetOffset),
                prefix + " sequence has a correct final anchor");
        }
        await reader.NavigateAsync(targetOffset);
        heldStart = await Begin();
        await Preview(Pull(heldStart, 1, false, .7));
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(escape);
        Check(escape.Handled && !reader.CornerDragActive && reader.Anchor == targetOffset, "actual Esc handler cancels without changing position");
        heldStart = await Begin();
        await Preview(Pull(heldStart, 1, false, .7));
        Mouse.Capture((IInputElement)reader.Children[0]);
        Mouse.Capture(null);
        Check(!reader.CornerDragActive && reader.Anchor == targetOffset, "mouse capture loss cancels through the routed handler");
        reader.AnimationsEnabled = false;
        await reader.NavigateAsync(targetOffset, 2);
        var gripStart = await Begin();
        await Preview(Pull(gripStart, 1, false, .8));
        reader.UpdateCornerDrag(Pull(gripStart, 1, false, .2));
        await reader.ReleaseCornerDragAsync(Pull(gripStart, 1, false, .2));
        Check(reader.Anchor == targetOffset && reader.Snapshot().HighlightedCharacters == 2 && reader.Snapshot().AnchorVisible,
            "80 percent then 20 percent rebounds using final pointer and keeps right-page anchor");
        gripStart = await Begin();
        await Preview(Pull(gripStart, 1, false, .7));
        await Task.WhenAll(reader.TurnAsync(1), reader.TurnAsync(-1), reader.TurnAsync(1));
        Check(reader.CornerDragActive && reader.Anchor == targetOffset, "wheel and repeated navigation cannot queue behind a drag");
        reader.CancelPageDrag();

        // Invalidating changes must win over both prepared and still-pending candidate pages.
        foreach (string operation in new[] { "cancel", "scroll", "single", "font", "width", "sidebar", "resize", "jump", "book" })
        {
            await window.LoadTestBookAsync(book, targetOffset);
            await window.SetReadingViewAsync(ReadingMode.Page, "double");
            await reader.NavigateAsync(targetOffset, 2);
            gripStart = await Begin();
            await Preview(Pull(gripStart, 1, false, .8));
            switch (operation)
            {
                case "cancel": reader.CancelPageDrag(); break;
                case "scroll": await window.SetReadingViewAsync(ReadingMode.Scroll); break;
                case "single": await window.SetReadingViewAsync(ReadingMode.Page, "single"); break;
                case "font": await reader.SetFontAsync(30); break;
                case "width": await window.ApplyAppearanceAsync(window.Appearance with { Width = 620 }); break;
                case "sidebar": await window.SetSidebarVisibleAsync(true); break;
                case "resize": window.Width -= 80; await reader.RefreshLayoutAsync(); break;
                case "jump": await window.JumpAsync(targetOffset, 2); break;
                case "book": await window.LoadTestBookAsync(new BookDocument("切换目标", book.Text + "尾声"), targetOffset); break;
            }
            await reader.ReleaseCornerDragAsync(Pull(gripStart, 1, false, .9));
            Check(reader.Anchor == targetOffset && reader.Snapshot().AnchorVisible && !reader.CornerDragActive && !reader.AnimationLayerVisible,
                operation + " invalidates preview before reflow or navigation", reader.Snapshot());
        }
        await window.LoadTestBookAsync(book, targetOffset);
        await window.SetReadingViewAsync(ReadingMode.Page, "double");
        await window.ApplyAppearanceAsync(new("SimSun", "paper", 0, "double", "curl", false));
        await reader.SetFontAsync(22);
        foreach (double fraction in new[] { .3, .7 })
        {
            await reader.NavigateAsync(targetOffset);
            original = reader.Snapshot();
            gripStart = await Begin();
            reader.UpdateCornerDrag(Pull(gripStart, 1, false, fraction));
            await reader.ReleaseCornerDragAsync(Pull(gripStart, 1, false, fraction));
            await reader.FlushPendingInputAsync();
            Check(fraction < .5 ? reader.Anchor == original.Anchor : reader.Snapshot().FirstVisible >= original.LastVisible,
                "fast release before staging completes " + fraction, reader.Snapshot());
        }
        await reader.NavigateAsync(targetOffset);
        gripStart = await Begin();
        reader.UpdateCornerDrag(Pull(gripStart, 1, false, .8));
        var pendingPreparation = reader.WaitForDragPreviewAsync();
        reader.CancelPageDrag();
        await pendingPreparation;
        Check(reader.Anchor == targetOffset && !reader.AnimationLayerVisible && !reader.CornerDragActive, "late preparation cannot resurrect a cancelled drag");
        reader.AnimationsEnabled = true;
        gripStart = await Begin();
        await Preview(Pull(gripStart, 1, false, .7));
        var settling = reader.ReleaseCornerDragAsync(Pull(gripStart, 1, false, .7));
        await Task.Delay(30);
        reader.CancelPageDrag();
        await settling;
        Check(!SystemParameters.ClientAreaAnimation || reader.Anchor == targetOffset,
            "cancel during settlement prevents a delayed commit", reader.Snapshot());
        reader.AnimationsEnabled = false;

        // A long run includes both forward and backward crossings of content chunk boundaries.
        await reader.NavigateAsync(book.Chunks[1].Start - 1);
        int crossings = 0;
        for (int i = 0; i < 30; i++)
        {
            var before = reader.Snapshot();
            gripStart = await Begin();
            await reader.ReleaseCornerDragAsync(Pull(gripStart, 1, false, .7));
            var after = reader.Snapshot();
            if (after.Chunk != before.Chunk) crossings++;
            Check(after.FirstVisible >= before.LastVisible && after.FirstVisible - before.LastVisible <= 2 && after.AnchorVisible,
                "continuous drag source ranges " + i, new { before, after });
            gripStart = await Begin(-1);
            await reader.ReleaseCornerDragAsync(Pull(gripStart, -1, false, .7));
            Check(reader.Snapshot().FirstVisible == before.FirstVisible && reader.Snapshot().AnchorVisible,
                "backward drag reverses across chunks " + i);
            gripStart = await Begin();
            await reader.ReleaseCornerDragAsync(Pull(gripStart, 1, false, .7));
        }
        Check(crossings > 0, "drag crosses an actual content chunk boundary", crossings);
        await reader.NavigateAsync(0);
        Check(!reader.BeginCornerDrag(Start(-1, false)), "cannot drag before beginning");
        await reader.NavigateAsync(book.Text.Length - 1);
        Check(!reader.BeginCornerDrag(Start(1, true)), "cannot drag past ending");
        await reader.SetModeAsync(ReadingMode.Scroll);
        Check(!reader.BeginCornerDrag(Start(1, false)), "continuous scroll ignores page corners");

        if (window.TestBookPath is { } realPath)
        {
            await window.OpenFileAsync(realPath);
            await window.SetReadingViewAsync(ReadingMode.Page, "double");
            var real = reader.Book!;
            int anchor = real.ClampOffset((int)(real.Text.Length * .61));
            await reader.NavigateAsync(anchor, 2);
            window.SavePosition(true);
            gripStart = await Begin();
            await Preview(Pull(gripStart, 1, false, .8));
            window.SavePosition(true);
            using (var store = new SessionStore(window.Store.Folder))
                Check(store.Read(real.ContentId)!.Offset == anchor, "force-save during preview persists original real-novel anchor");
            var mark = await window.AddBookmarkAsync();
            Check(mark.Offset == anchor && !reader.CornerDragActive, "bookmark during a drag cancels and records original text");
            await window.OpenFileAsync(realPath);
            Check(reader.Anchor == anchor && reader.Snapshot().AnchorVisible, "cancelled drag reopens at persisted anchor");
            gripStart = await Begin();
            await reader.ReleaseCornerDragAsync(Pull(gripStart, 1, false, .75));
            int committed = reader.Anchor;
            window.SavePosition(true);
            Check(committed > anchor, "real-novel completed drag advances");
            await window.OpenFileAsync(realPath);
            Check(reader.Anchor == committed && reader.Snapshot().AnchorVisible, "completed drag reopens at committed anchor");
            foreach (var view in new[] { "scroll", "single", "double" })
            {
                await window.SetReadingViewAsync(view == "scroll" ? ReadingMode.Scroll : ReadingMode.Page, view == "scroll" ? null : view);
                foreach (int font in new[] { 16, 30, 22 })
                {
                    await reader.SetFontAsync(font);
                    Check(reader.Anchor == committed && reader.Snapshot().AnchorVisible, "real-novel committed position survives " + view + " font " + font);
                }
            }
            await window.JumpToBookmarkAsync(mark);
            Check(reader.Anchor == anchor && reader.Snapshot().AnchorVisible, "original bookmark still restores after drag and reflow");
        }
        reader.CancelPageDrag();
        reader.AnimationsEnabled = false;
        var dpi = VisualTreeHelper.GetDpi(reader);
        timings.Add(new { dragEnvironment = true, dpiScaleX = dpi.DpiScaleX, dpiScaleY = dpi.DpiScaleY,
            renderTier = RenderCapability.Tier >> 16, systemAnimations = SystemParameters.ClientAreaAnimation });
    }
}
