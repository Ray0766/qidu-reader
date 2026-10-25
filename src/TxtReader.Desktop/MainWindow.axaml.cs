using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TxtReader.App;

namespace Qidu.Desktop;

public partial class MainWindow : Window
{
    internal SessionStore Store { get; }
    private readonly LibraryService _library;
    private readonly FileStream _lock;
    private readonly List<Bitmap> _covers = [];
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private bool _loading, _following, _closed;
    private string? _bookPath;
    private CancellationTokenSource? _searchCancel;
    private SearchPage? _search;
    private string _searchQuery = "";
    private readonly List<SearchHit> _hits = [];
    internal ReaderView Surface => Reader;
    internal bool IsShelf => ShelfView.IsVisible;
    internal static string? Argument(string name)
    {
        int index = Array.IndexOf(Program.Arguments, name);
        return index >= 0 && index + 1 < Program.Arguments.Length ? Program.Arguments[index + 1] : null;
    }
    public MainWindow()
    {
        InitializeComponent();
        string folder = Argument("--data-dir") ?? LibraryLocation.PrepareDefault(); Directory.CreateDirectory(folder);
        _lock = new FileStream(Path.Combine(folder, ".reader.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Store = new SessionStore(folder); _library = new LibraryService(Store);
        _loading = true;
        ModePicker.ItemsSource = new[] { "单页", "双页", "滚动" };
        FontPicker.ItemsSource = ReadingAppearance.FontFamilies;
        ThemePicker.ItemsSource = ReadingAppearance.Themes.Select(x => x.Name).ToArray();
        WidthPicker.ItemsSource = new[] { "紧凑宽度", "标准宽度", "宽幅阅读", "填满窗口" };
        EffectPicker.ItemsSource = new[] { "纸页翻动", "水平滑动", "淡入淡出", "无动画" };
        var appearance = Store.ReadAppearance().Normalize();
        Reader.Configure("page", 22, appearance); SyncControls(); _loading = false;
        ChapterList.ItemTemplate = new FuncDataTemplate<Chapter>((chapter, _) => new TextBlock { Text = chapter?.Title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 7) });
        SearchList.ItemTemplate = new FuncDataTemplate<SearchHit>((hit, _) => new TextBlock { Text = hit is null ? "" : hit.Chapter + "\n" + hit.Prefix + hit.Match + hit.Suffix, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 7) });
        BookmarkList.ItemTemplate = new FuncDataTemplate<Bookmark>((mark, _) => new TextBlock { Text = mark is null ? "" : mark.Title + "\n" + mark.Excerpt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 7) });
        ImportButton.Click += async (_, _) => await Run(ChooseFiles);
        SampleButton.Click += async (_, _) => await Run(() => OpenFile(LibraryService.DemoPath));
        ShelfButton.Click += (_, _) => ShowShelf();
        SidebarButton.Click += (_, _) => { Reader.CancelDrag(); SetAppearance(Reader.Appearance with { SidebarVisible = !Reader.Appearance.SidebarVisible }); };
        BookmarkButton.Click += (_, _) => AddBookmark();
        PreviousButton.Click += (_, _) => Reader.Turn(-1); NextButton.Click += (_, _) => Reader.Turn(1);
        SmallerButton.Click += (_, _) => Configure(Reader.Mode, Reader.ReadingFontSize - 2, Reader.Appearance);
        LargerButton.Click += (_, _) => Configure(Reader.Mode, Reader.ReadingFontSize + 2, Reader.Appearance);
        ModePicker.SelectionChanged += (_, _) => { if (!_loading) Configure(ModePicker.SelectedIndex == 2 ? "scroll" : "page", Reader.ReadingFontSize, Reader.Appearance with { PageLayout = ModePicker.SelectedIndex == 1 ? "double" : "single" }); };
        FontPicker.SelectionChanged += (_, _) => { if (!_loading && FontPicker.SelectedItem is string font) SetAppearance(Reader.Appearance with { FontFamily = font }); };
        ThemePicker.SelectionChanged += (_, _) => { if (!_loading && ThemePicker.SelectedIndex >= 0) SetAppearance(Reader.Appearance with { Theme = ReadingAppearance.Themes[ThemePicker.SelectedIndex].Key }); };
        WidthPicker.SelectionChanged += (_, _) => { if (!_loading && WidthPicker.SelectedIndex >= 0) SetAppearance(Reader.Appearance with { Width = new[] { 620, 790, 1000, 0 }[WidthPicker.SelectedIndex] }); };
        EffectPicker.SelectionChanged += (_, _) => { if (!_loading && EffectPicker.SelectedIndex >= 0) SetAppearance(Reader.Appearance with { TurnEffect = new[] { "curl", "slide", "fade", "none" }[EffectPicker.SelectedIndex] }); };
        ChapterList.SelectionChanged += (_, _) => { if (!_following && ChapterList.SelectedItem is Chapter chapter) Reader.Navigate(chapter.Offset); };
        SearchList.SelectionChanged += (_, _) => { if (SearchList.SelectedItem is SearchHit hit) { Reader.Navigate(hit.Offset, hit.Length); Reader.Focus(); } };
        BookmarkList.SelectionChanged += (_, _) => { if (!_following && BookmarkList.SelectedItem is Bookmark mark) Reader.Navigate(mark.Offset); };
        SearchButton.Click += async (_, _) => await Run(() => Search(false));
        MoreSearchButton.Click += async (_, _) => await Run(() => Search(true));
        SearchBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Run(() => Search(false)); } };
        DeleteBookmarkButton.Click += (_, _) => { if (BookmarkList.SelectedItem is Bookmark mark) { Store.DeleteBookmark(mark.BookId, mark.Id); RefreshBookmarks(); } };
        ShelfFilter.TextChanged += (_, _) => RefreshShelf();
        Reader.PositionChanged += () => { Save(); FollowChapter(); };
        Deactivated += (_, _) => { Reader.CancelDrag(); Save(); };
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized) { Reader.CancelDrag(); Save(); } };
        Closing += (_, e) =>
        {
            Reader.CancelDrag(); Save();
            if (_openGate.CurrentCount == 0) { e.Cancel = true; Notice.Text = "正在导入书籍，请稍候再关闭。"; }
        };
        Closed += (_, _) => { _closed = true; _searchCancel?.Cancel(); Reader.Dispose(); Store.Dispose(); _lock.Dispose(); foreach (var cover in _covers) cover.Dispose(); };
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var files = e.DataTransfer.TryGetFiles()?.Select(x => x.TryGetLocalPath()).Where(x => x is not null && BookLoader.IsSupportedPath(x)).ToArray();
            if (files is not null) foreach (string? path in files) await Run(() => OpenFile(path!));
        });
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Copy; e.Handled = true; });
        DragDrop.SetAllowDrop(this, true);
        Opened += async (_, _) =>
        {
            if (Screens.Primary is { } screen)
            { Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling); Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling - 24); }
            if (Argument("--self-test") is { } report) { await DesktopTests.Run(this, report); return; }
            if (Argument("--resume-test") is { } resume) { await DesktopTests.Resume(this, resume); return; }
            string? path = Program.Arguments.LastOrDefault(x => BookLoader.IsSupportedPath(x) && File.Exists(x));
            if (path is not null) await Run(() => OpenFile(path)); else ShowShelf();
        };
    }
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) Notice.Text = "未能完成：" + ex.Message; }
    }
    private async Task ChooseFiles()
    {
        Reader.CancelDrag();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入 TXT 或 EPUB", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("电子书") { Patterns = ["*.txt", "*.epub"], MimeTypes = ["text/plain", "application/epub+zip"] }] });
        foreach (var file in files) if (file.TryGetLocalPath() is { } path) await OpenFile(path);
    }
    internal async Task OpenFile(string path)
    {
        await _openGate.WaitAsync();
        try
        {
        if (_closed) return;
        Save(); Reader.CancelDrag(); Notice.Text = "正在打开书籍…";
        var imported = await _library.ImportAsync(path);
        var session = Store.Read(imported.Shelf.BookId);
        _searchCancel?.Cancel(); _hits.Clear(); SearchList.ItemsSource = null; MoreSearchButton.IsVisible = false;
        _bookPath = imported.Shelf.Path;
        Reader.Open(imported.Document, session?.Offset ?? 0, session?.Mode ?? Reader.Mode, session?.FontSize ?? Reader.ReadingFontSize, Store.ReadAppearance());
        BookTitle.Text = imported.Shelf.Title; Title = imported.Shelf.Title + " — 栖读";
        _following = true;
        ChapterList.ItemsSource = null;
        // Replacing a long virtualized list with a short EPUB table of contents
        // must not retain the old panel's scroll anchor and leading space.
        ChapterList.ItemsPanel = new FuncTemplate<Panel?>(() => imported.Document.Chapters.Count > 100
            ? new VirtualizingStackPanel { VerticalAlignment = VerticalAlignment.Top }
            : new StackPanel { VerticalAlignment = VerticalAlignment.Top });
        ChapterList.ItemsSource = imported.Document.Chapters;
        _following = false;
        ShowReading(); SyncControls(); RefreshBookmarks(); FollowChapter(); Save();
        Notice.Text = imported.Document.Warnings.Count > 0 ? string.Join("；", imported.Document.Warnings) : imported.Document.IsEpub ? "EPUB 图文阅读" : imported.Document.EncodingName;
        }
        finally { _openGate.Release(); }
    }
    internal void Configure(string mode, double size, ReadingAppearance appearance)
    { Reader.Configure(mode, size, appearance); SyncControls(); Save(); }
    private void SetAppearance(ReadingAppearance appearance) => Configure(Reader.Mode, Reader.ReadingFontSize, appearance);
    private void SyncControls()
    {
        _loading = true;
        ModePicker.SelectedIndex = Reader.Mode == "scroll" ? 2 : Reader.Appearance.PageLayout == "double" ? 1 : 0;
        FontPicker.SelectedItem = Reader.Appearance.FontFamily;
        ThemePicker.SelectedIndex = Array.FindIndex(ReadingAppearance.Themes, x => x.Key == Reader.Appearance.Theme);
        WidthPicker.SelectedIndex = Array.IndexOf(new[] { 620, 790, 1000, 0 }, Reader.Appearance.Width);
        EffectPicker.SelectedIndex = Array.IndexOf(new[] { "curl", "slide", "fade", "none" }, Reader.Appearance.TurnEffect);
        SizeLabel.Text = Reader.ReadingFontSize.ToString("0"); Sidebar.IsVisible = Reader.Appearance.SidebarVisible;
        _loading = false;
    }
    internal void Save()
    {
        if (_closed) return;
        if (Reader.Book is { } book && _bookPath is not null)
            Store.Save(new ReadingSession(book.ContentId, _bookPath, Reader.Anchor, Reader.Mode, Reader.ReadingFontSize, book.EncodingName));
        Store.SaveAppearance(Reader.Appearance);
    }
    private void FollowChapter()
    {
        if (Reader.Book is not { } book) return;
        _following = true; ChapterList.SelectedItem = book.ChapterAt(Reader.Anchor);
        if (ChapterList.SelectedItem is { } item) ChapterList.ScrollIntoView(item);
        _following = false;
        ProgressLabel.Text = $"{Reader.Anchor * 100d / Math.Max(1, book.Text.Length - 1):F1}%";
    }
    internal Bookmark? AddBookmark()
    {
        if (Reader.Book is not { } book) return null;
        var mark = Store.AddBookmark(book, Reader.Anchor); RefreshBookmarks(); Notice.Text = "书签已保存"; return mark;
    }
    private void RefreshBookmarks()
    { _following = true; BookmarkList.ItemsSource = Reader.Book is { } b ? Store.ListBookmarks(b.ContentId) : null; _following = false; }
    private async Task Search(bool more)
    {
        if (Reader.Book is not { } book) return;
        string query = SearchBox.Text?.Trim() ?? ""; if (query.Length == 0) return;
        _searchCancel?.Cancel(); _searchCancel = new(); var token = _searchCancel.Token;
        if (!more || query != _searchQuery) { _hits.Clear(); _search = null; _searchQuery = query; }
        var page = await BookSearch.FindPageAsync(book, query, _search?.NextOffset ?? 0, token);
        if (token.IsCancellationRequested || Reader.Book != book || _closed) return;
        _search = page; _hits.AddRange(page.Hits); SearchList.ItemsSource = _hits.ToArray(); MoreSearchButton.IsVisible = page.HasMore;
        Notice.Text = $"找到 {_hits.Count} 处" + (page.HasMore ? "，可继续加载" : "");
    }
    internal void ShowShelf()
    {
        Reader.CancelDrag(); Save(); ShelfView.IsVisible = true; ReadingGrid.IsVisible = false; ReadingToolbar.IsVisible = false;
        BookmarkButton.IsVisible = SidebarButton.IsVisible = PreviousButton.IsVisible = NextButton.IsVisible = false;
        BookTitle.Text = "给阅读留一处安静的地方"; ProgressLabel.Text = ""; RefreshShelf();
    }
    private void ShowReading()
    {
        ShelfView.IsVisible = false; ReadingGrid.IsVisible = ReadingToolbar.IsVisible = true;
        BookmarkButton.IsVisible = SidebarButton.IsVisible = PreviousButton.IsVisible = NextButton.IsVisible = true;
    }
    private void RefreshShelf()
    {
        ShelfPanel.Children.Clear(); foreach (var cover in _covers) cover.Dispose(); _covers.Clear();
        var books = Store.ListBooks(ShelfFilter.Text ?? ""); ShelfCount.Text = $"{books.Count} 本藏书";
        EmptyShelf.IsVisible = Store.ListBooks().Count == 0;
        foreach (var book in books)
        {
            var coverGrid = new Grid { Width = 130, Height = 182, Background = Brush.Parse("#566C61") };
            string artPath = Path.Combine(Path.GetDirectoryName(book.Path)!, ".qidu-cover");
            if (File.Exists(artPath))
            {
                try { var bitmap = Artwork.Decode(File.ReadAllBytes(artPath), 260, 364); if (bitmap is not null) { _covers.Add(bitmap); coverGrid.Children.Add(new Image { Source = bitmap, Stretch = Stretch.UniformToFill }); } }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { }
            }
            if (coverGrid.Children.Count == 0)
            {
                coverGrid.Children.Add(new TextBlock { Text = book.Title, Foreground = Brushes.White, FontSize = 20, FontFamily = new FontFamily(ReadingAppearance.FontFamilies[0]), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 30, 12, 20) });
                coverGrid.Children.Add(new TextBlock { Text = "栖 读 · 藏 书", Foreground = Brush.Parse("#D5DFC9"), FontSize = 10, Margin = new Thickness(18, 0, 0, 14), VerticalAlignment = VerticalAlignment.Bottom });
            }
            coverGrid.Children.Add(new Border { Width = 7, Background = new SolidColorBrush(Color.FromArgb(35, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Left });
            var button = new Button { Content = coverGrid, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += async (_, _) => await Run(() => OpenFile(book.Path));
            var rename = new MenuItem { Header = "重命名" }; rename.Click += async (_, _) => await Rename(book);
            var remove = new MenuItem { Header = "从书架移除（保留文件和进度）" }; remove.Click += (_, _) => { Store.ArchiveBook(book.BookId); RefreshShelf(); };
            button.ContextMenu = new ContextMenu { ItemsSource = new[] { rename, remove } };
            var stack = new StackPanel { Width = 162, Margin = new Thickness(0, 8, 14, 20), Spacing = 8 };
            stack.Children.Add(button);
            stack.Children.Add(new Border { Height = 6, Background = Brush.Parse("#B9B99E"), Margin = new Thickness(-6, -3, 0, 0) });
            stack.Children.Add(new TextBlock { Text = book.Title, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = book.ProgressText, Foreground = Brush.Parse("#637564"), FontSize = 12 }); ShelfPanel.Children.Add(stack);
        }
    }
    private async Task Rename(ShelfBook book)
    {
        var input = new TextBox { Text = book.Title, MaxLength = 120 }; var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window { Title = "修改书名", Width = 380, Height = 170, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 20, Children = { input, save } } };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) { Store.RenameBook(book.BookId, input.Text); dialog.Close(); RefreshShelf(); } };
        await dialog.ShowDialog(this);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool command = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        if (command && e.Key == Key.O) { _ = Run(ChooseFiles); e.Handled = true; return; }
        if (command && e.Key == Key.F && !IsShelf) { SideTabs.SelectedIndex = 1; SetAppearance(Reader.Appearance with { SidebarVisible = true }); SearchBox.Focus(); e.Handled = true; return; }
        if (e.Source is TextBox || IsShelf) return;
        if (e.Key == Key.Escape) Reader.CancelDrag();
        else if (e.Key is Key.Right or Key.PageDown or Key.Space) Reader.Turn(1);
        else if (e.Key is Key.Left or Key.PageUp) Reader.Turn(-1);
        else if (command && e.Key == Key.D) AddBookmark(); else return;
        e.Handled = true;
    }
}
