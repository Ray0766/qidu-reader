using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TxtReader.Core;

namespace TxtReader.App;

internal static class ChapterFollowTests
{
    internal static async Task RunAsync(MainWindow window, string folder, Action<bool, string, object?> check)
    {
        var reader = window.ReadingSurface;
        var list = (ListBox)window.FindName("ChapterList");
        var tabs = (TabControl)window.FindName("SidebarTabs");
        var book = new BookDocument("目录跟随验证", string.Concat(Enumerable.Range(1, 180).Select(i =>
            $"第{i}章 沿河读书\n林舟收起书签，读到第{i}段故事。雨停后，他沿着河岸走到旧书店，窗前还留着一盏灯。\n\n")));
        reader.AnimationsEnabled = false;
        await window.LoadTestBookAsync(book);
        await window.SetSidebarVisibleAsync(true);
        tabs.SelectedIndex = 0;

        async Task Settle()
        {
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            await Task.Delay(60);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
        }
        static T? Child<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                if (Child<T>(child) is { } nested) return nested;
            }
            return null;
        }
        void Visible(string name, int anchor)
        {
            var chapter = reader.Book!.ChapterAt(anchor);
            var item = list.ItemContainerGenerator.ContainerFromItem(chapter) as FrameworkElement;
            var viewport = Child<ScrollContentPresenter>(list);
            Rect? bounds = item is not null && viewport is not null
                ? item.TransformToAncestor(viewport).TransformBounds(new Rect(item.RenderSize)) : null;
            check(list.IsVisible && ReferenceEquals(list.SelectedItem, chapter) && bounds is { } b &&
                b.Height > 0 && b.Top >= -1 && b.Bottom <= viewport!.ActualHeight + 1 &&
                reader.Anchor == anchor && reader.Snapshot().AnchorVisible,
                "chapter follow: " + name, new { anchor, selected = (list.SelectedItem as Chapter)?.Title, bounds,
                    viewportHeight = viewport?.ActualHeight, snapshot = reader.Snapshot() });
        }
        void Capture(string name)
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(folder, "chapter-" + name + ".png"));
            encoder.Save(file);
        }
        foreach (string mode in new[] { "single", "double", "scroll" })
        {
            await window.SetReadingViewAsync(mode == "scroll" ? ReadingMode.Scroll : ReadingMode.Page, mode == "scroll" ? null : mode);
            int anchor = book.Chapters[120].Offset + 18;
            await reader.NavigateAsync(anchor);
            await Settle();
            Visible(mode + " far jump is visible without navigating to chapter start", anchor);
            var before = book.ChapterAt(reader.Anchor);
            await reader.TurnAsync(1);
            anchor = reader.Anchor;
            await Settle();
            check(!ReferenceEquals(before, book.ChapterAt(anchor)), "chapter follow: " + mode + " crosses a chapter during ordinary reading", reader.Snapshot());
            Visible(mode + " ordinary reading follows the new chapter", anchor);
            Capture(mode);
        }
        await window.SetSidebarVisibleAsync(false);
        int hiddenAnchor = book.Chapters[165].Offset + 18;
        await reader.NavigateAsync(hiddenAnchor);
        await window.SetSidebarVisibleAsync(true);
        await Settle();
        Visible("showing sidebar reveals the current chapter without resetting progress", hiddenAnchor);

        tabs.SelectedItem = window.FindName("SearchTab");
        await Settle();
        window.Activate();
        await Settle();
        var search = (TextBox)window.FindName("SearchInput");
        search.Focus();
        await Settle();
        check(search.IsKeyboardFocusWithin, "chapter follow: search focus fixture established", new { window.IsActive, search.IsVisible, search.IsEnabled });
        await reader.NavigateAsync(book.Chapters[25].Offset + 20);
        await reader.NavigateAsync(book.Chapters[154].Offset + 20);
        await Settle();
        check(search.IsKeyboardFocusWithin && tabs.SelectedItem == window.FindName("SearchTab"),
            "chapter follow: updates do not steal the search tab or keyboard focus", null);
        tabs.SelectedIndex = 0;
        await Settle();
        Visible("returning to contents reveals the newest target", book.Chapters[154].Offset + 20);

        // Browsing the contents manually must not cause an unsolicited text jump.
        int untouched = reader.Anchor;
        Child<ScrollViewer>(list)!.ScrollToHome();
        await Settle();
        check(reader.Anchor == untouched, "chapter follow: browsing contents does not change reading position", reader.Snapshot());
        await reader.NavigateAsync(book.Chapters[80].Offset + 18);
        await reader.NavigateAsync(book.Chapters[9].Offset + 18);
        await Settle();
        Visible("rapid jumps discard outdated reveal targets", book.Chapters[9].Offset + 18);

        var second = new BookDocument("换书目录验证", string.Concat(Enumerable.Range(1, 100).Select(i => $"第{i}章 新的书\n另一段故事从这里开始。\n\n")));
        int nextAnchor = second.Chapters[75].Offset + 10;
        await window.LoadTestBookAsync(second, nextAnchor);
        await Settle();
        Visible("switching books reveals only the new book's current chapter", nextAnchor);
    }
}
