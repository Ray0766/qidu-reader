using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TxtReader.App;

internal static class NavigationInputTests
{
    internal static async Task RunAsync(MainWindow window, Action<bool, string, object?> check)
    {
        var reader = window.ReadingSurface;
        void Check(bool ok, string name) => check(ok, "input: " + name, reader.Snapshot());
        reader.AnimationsEnabled = false;
        foreach (string layout in new[] { "single", "double" })
        {
            await window.SetReadingViewAsync(ReadingMode.Page, layout);
            await window.SetSidebarVisibleAsync(false);
            foreach (double y in new[] { .02, .5, .98 })
            foreach (double x in new[] { .02, .25, .75, .98 })
            {
                await reader.NavigateAsync(5000);
                var before = reader.Snapshot();
                await reader.TurnAsync(x < .5 ? -1 : 1);
                int expected = reader.Anchor;
                await reader.NavigateAsync(before.Anchor);
                Point point = new(reader.ActualWidth * x, reader.ActualHeight * y);
                Check(reader.BeginPagePointer(point), $"{layout} accepts paper press {x}/{y}");
                await reader.EndPagePointerAsync(point);
                Check(reader.Anchor == expected && reader.Snapshot().AnchorVisible, $"{layout} single click turns once {x}/{y}");
            }
        }
        await reader.NavigateAsync(5000);
        int original = reader.Anchor;
        Point body = new(reader.ActualWidth * .75, reader.ActualHeight * .5);
        reader.BeginPagePointer(body);
        reader.MovePagePointer(body + new Vector(-20, 0));
        await reader.EndPagePointerAsync(body);
        Check(reader.Anchor == original, "moving away then back is not an accidental click");
        foreach (string change in new[] { "cancel", "font", "scroll" })
        {
            await reader.SetModeAsync(ReadingMode.Page);
            body = new(reader.ActualWidth * .75, reader.ActualHeight * .5);
            Check(reader.BeginPagePointer(body), change + " starts pending click");
            if (change == "cancel") reader.CancelPageDrag();
            else if (change == "font") await reader.SetFontAsync(28);
            else await reader.SetModeAsync(ReadingMode.Scroll);
            await reader.EndPagePointerAsync(body);
            Check(reader.Anchor == original && reader.Snapshot().AnchorVisible, change + " invalidates pending click and late release");
        }
        await reader.SetModeAsync(ReadingMode.Scroll);
        Check(!reader.BeginPagePointer(body), "scrolling text does not become a page click");
        await window.SetReadingViewAsync(ReadingMode.Page, "double");
        await window.SetSidebarVisibleAsync(true);
        ((TabControl)window.FindName("SidebarTabs")).SelectedIndex = 0;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout);
        window.Activate();
        async Task Key(Key key, FrameworkElement focus, bool shouldTurn)
        {
            await reader.NavigateAsync(5000);
            int start = reader.Anchor;
            await reader.TurnAsync(key is System.Windows.Input.Key.Left or System.Windows.Input.Key.PageUp ? -1 : 1);
            int expected = reader.Anchor;
            await reader.NavigateAsync(start);
            focus.Focus();
            Check(focus.IsKeyboardFocusWithin, "test focus reached " + focus.Name);
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(input);
            await reader.FlushPendingInputAsync();
            Check(shouldTurn ? input.Handled && reader.Anchor == expected : !input.Handled && reader.Anchor == start,
                key + " with focus on " + focus.Name + (shouldTurn ? " turns one page" : " preserves control navigation"));
        }
        var pages = (FrameworkElement)reader.Children[0];
        foreach (var key in new[] { System.Windows.Input.Key.Right, System.Windows.Input.Key.Left, System.Windows.Input.Key.PageDown, System.Windows.Input.Key.PageUp })
            await Key(key, pages, true);
        var chapters = (ListBox)window.FindName("ChapterList");
        await Key(System.Windows.Input.Key.Right, chapters, true);
        await Key(System.Windows.Input.Key.Left, chapters, true);
        await Key(System.Windows.Input.Key.PageDown, chapters, false);
        ((TabControl)window.FindName("SidebarTabs")).SelectedItem = window.FindName("SearchTab");
        await window.Dispatcher.InvokeAsync(window.UpdateLayout);
        await Key(System.Windows.Input.Key.Right, (TextBox)window.FindName("SearchInput"), false);
        await window.SetSidebarVisibleAsync(false);
    }
}
