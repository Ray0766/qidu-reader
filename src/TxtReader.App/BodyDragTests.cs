using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TxtReader.App;

internal static class BodyDragTests
{
    internal static async Task RunAsync(MainWindow window, string folder, Action<bool, string, object?> check)
    {
        var reader = window.ReadingSurface;
        void Check(bool ok, string name, object? detail = null) => check(ok, "body drag: " + name, detail ?? reader.Snapshot());
        Point At(double x, double y) => new(reader.ActualWidth * x, reader.ActualHeight * y);
        Point Pull(Point start, int direction, double amount) => start + new Vector(-direction * reader.DragTravel * amount, 0);
        void SaveImage(string name)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(PageTurnLayer.Capture(reader)));
            using var output = File.Create(Path.Combine(folder, name + ".png"));
            encoder.Save(output);
        }

        // Any height, including the middle of an edge, can drive the cylindrical fold.
        foreach (double y in new[] { 0d, 65, 325, 585, 650 })
        foreach (var delta in new[] { new Vector(-2, 0), new Vector(-200, 50), new Vector(-600, -80), new Vector(-1400, 1000) })
        {
            var curl = new PaperCurl(500, 650, y, new Point(500, y) + delta);
            var grip = curl.Map(500, y);
            var a = curl.Map(0, 0); var b = curl.Map(0, 650);
            Check((new Point(grip.X, grip.Y) - curl.Target).Length < .01 &&
                Math.Abs(a.X) + Math.Abs(a.Y) + Math.Abs(a.Z) + Math.Abs(b.X) + Math.Abs(b.Y - 650) + Math.Abs(b.Z) < .01,
                $"fixed spine and constrained edge grip {y}/{delta}", new { a, b, grip, curl.Target });
        }

        reader.AnimationsEnabled = false;
        await window.ApplyAppearanceAsync(window.Appearance with { TurnEffect = "curl", Width = 0, SidebarVisible = false });
        foreach (string layout in new[] { "single", "double" })
        {
            await window.SetReadingViewAsync(ReadingMode.Page, layout);
            foreach (int direction in new[] { 1, -1 })
            foreach (double x in new[] { .02, .25, .5, .75, .98 })
            foreach (double y in new[] { .02, .5, .98 })
            {
                await reader.NavigateAsync(5000, 2);
                int original = reader.Anchor;
                await reader.TurnAsync(direction);
                int expected = reader.Anchor;
                await reader.NavigateAsync(original, 2);
                int events = 0;
                EventHandler count = (_, _) => events++;
                reader.PositionChanged += count;
                try
                {
                    Point start = At(x, y);
                    Check(reader.BeginPagePointer(start) && !reader.CornerDragActive, $"{layout} {direction} {x}/{y} waits for gesture direction");
                    reader.MovePagePointer(Pull(start, direction, .49));
                    await reader.WaitForDragPreviewAsync();
                    Check(reader.DragPreviewReady && reader.Anchor == original && events == 0 && Math.Abs(reader.DragProgress - .49) < .001,
                        "all locations share the same threshold and preview cannot commit",
                        new { snapshot = reader.Snapshot(), reader.DragPreviewReady, reader.DragProgress, events,
                            window.IsActive, notice = window.Notice.Text,
                            memory = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 });
                    await reader.EndPagePointerAsync(Pull(start, direction, .49));
                    Check(reader.Anchor == original && events == 0 && !reader.IsBusy && reader.Snapshot().AnchorVisible,
                        "short body pull rebounds and preserves the source anchor");
                    reader.BeginPagePointer(start);
                    // Fast mouse-up: candidate preparation is deliberately still in flight.
                    await reader.EndPagePointerAsync(Pull(start, direction, .70));
                    Check(reader.Anchor == expected && events == 1 && !reader.IsBusy && reader.Snapshot().AnchorVisible,
                        "long body pull commits exactly one page in gesture direction");
                }
                finally { reader.PositionChanged -= count; reader.CancelPageDrag(); }
            }

            // Render the actual paper mesh from upper, middle and lower body grips.
            reader.AnimationsEnabled = true;
            foreach (double y in new[] { .2, .5, .8 })
            {
                await reader.NavigateAsync(5000);
                Point start = At(.65, y);
                reader.BeginPagePointer(start);
                reader.MovePagePointer(Pull(start, 1, .8));
                await reader.WaitForDragPreviewAsync();
                await Task.Delay(80);
                if (SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0)
                {
                    var grip = reader.DragProjectedCorner;
                    Check(grip is not null && Math.Abs(grip.Value.Y - start.Y) < .01 && reader.DragCurlRadius > 0,
                        $"{layout} curved edge follows the press height {y}", new { grip, start, reader.DragCurlRadius });
                    int frames = reader.LastAnimationFrames;
                    await Task.Delay(80);
                    Check(reader.LastAnimationFrames == frames, "holding the pointer holds the bend");
                    reader.MovePagePointer(Pull(start, 1, .8) + new Vector(0, 40));
                    await Task.Delay(50);
                    Check(reader.DragProjectedCorner != grip, "vertical adjustment changes the fold while dragging");
                }
                SaveImage($"body-{layout}-{y:0.0}");
                reader.MovePagePointer(Pull(start, 1, .2));
                await reader.EndPagePointerAsync(Pull(start, 1, .2));
                Check(reader.Anchor == 5000, "pulling back uses final release distance");
            }
            reader.AnimationsEnabled = false;
        }

        foreach (string phase in new[] { "pending", "prepared", "settling" })
        foreach (string operation in new[] { "cancel", "capture", "font", "scroll", "single", "double" })
        {
            await window.SetReadingViewAsync(ReadingMode.Page, "double");
            await reader.NavigateAsync(5000, 2);
            // With system animations disabled, a prepared release lands synchronously.
            // Cancelling afterwards must preserve that committed page, not roll it back.
            bool instantRelease = phase == "settling" && !SystemParameters.ClientAreaAnimation;
            int expectedAnchor = 5000;
            if (instantRelease)
            {
                reader.AnimationsEnabled = false;
                await reader.TurnAsync(1);
                expectedAnchor = reader.Anchor;
                await reader.NavigateAsync(5000, 2);
            }
            reader.AnimationsEnabled = true;
            Point start = At(.7, .5), end = Pull(start, 1, .8);
            reader.BeginPagePointer(start);
            reader.MovePagePointer(end);
            if (phase != "pending") await reader.WaitForDragPreviewAsync();
            Task settling = phase == "settling" ? reader.EndPagePointerAsync(end) : Task.CompletedTask;
            if (instantRelease)
                Check(settling.Status == TaskStatus.RanToCompletion && reader.Anchor == expectedAnchor && !reader.CornerDragActive,
                    "system animation disabled: prepared release commits before the next operation");
            switch (operation)
            {
                case "cancel": reader.CancelPageDrag(); break;
                case "capture": Mouse.Capture((IInputElement)reader.Children[0]); Mouse.Capture(null); break;
                case "font": await reader.SetFontAsync(reader.Snapshot().FontSize == 28 ? 22 : 28); break;
                case "scroll": await window.SetReadingViewAsync(ReadingMode.Scroll); break;
                default: await window.SetReadingViewAsync(ReadingMode.Page, operation); break;
            }
            await settling;
            await reader.EndPagePointerAsync(end);
            await reader.FlushPendingInputAsync();
            Check(reader.Anchor == expectedAnchor && reader.Snapshot().AnchorVisible && !reader.IsBusy && !reader.AnimationLayerVisible,
                phase + "/" + operation + (instantRelease
                    ? " preserves the completed page without a second commit"
                    : " cancels without a late commit"));
        }
        reader.AnimationsEnabled = false;
        await window.SetReadingViewAsync(ReadingMode.Page, "double");
        await reader.NavigateAsync(5000);
        var body = At(.75, .5);
        reader.BeginPagePointer(body);
        reader.MovePagePointer(body + new Vector(2, 90));
        Check(!reader.CornerDragActive, "vertical movement does not start a horizontal turn");
        await reader.EndPagePointerAsync(body);
        Check(reader.Anchor == 5000, "vertical movement cannot become a click on release");
        reader.BeginPagePointer(body);
        reader.MovePagePointer(Pull(body, 1, .8));
        await reader.WaitForDragPreviewAsync();
        await reader.EndPagePointerAsync(Pull(body, -1, .8));
        Check(reader.Anchor == 5000 && !reader.IsBusy, "crossing back past the press rebounds instead of reversing direction");

        await reader.NavigateAsync(0);
        reader.BeginPagePointer(body);
        await reader.EndPagePointerAsync(Pull(body, -1, .8));
        Check(reader.Anchor == 0 && !reader.IsBusy, "cannot drag before the first page");
        await reader.NavigateAsync(reader.Book!.Text.Length - 1);
        int last = reader.Anchor;
        reader.BeginPagePointer(body);
        await reader.EndPagePointerAsync(Pull(body, 1, .8));
        Check(reader.Anchor == last && !reader.IsBusy, "cannot drag beyond the final page");
    }
}
