using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TxtReader.Core;

namespace TxtReader.App;

public sealed record ImportFailure(string Path, string Reason);
public sealed record ImportBatchResult(int Imported, int Duplicates, IReadOnlyList<ImportFailure> Failures);

public partial class MainWindow : Window
{
    private readonly string[] _arguments;
    private readonly SessionStore _store;
    private readonly LibraryService _library;
    private readonly DispatcherTimer _saveTimer, _searchTimer;
    private bool _ready, _syncSelection, _opening, _syncSearch, _closed, _closeReady, _closing, _chapterRevealQueued;
    private string? _activePath, _activeTitle;
    private int? _returnAnchor;
    private bool _searchJumpActive;
    private ReadingAppearance _appearance = new();
    public ReadingAppearance Appearance => _appearance;
    private CancellationTokenSource? _searchCancellation;
    private SearchPage? _searchPage;
    private readonly List<int> _searchStarts = [0];
    private int _searchPageIndex;
    public ReaderSurface ReadingSurface => Reader;
    internal SessionStore Store => _store;
    internal SearchPage? CurrentSearchPage => _searchPage;
    internal string? TestBookPath
    {
        get { int i = Array.IndexOf(_arguments, "--test-book"); return i >= 0 && i + 1 < _arguments.Length ? _arguments[i + 1] : null; }
    }
    private bool ShelfVisible => LibraryWorkspace.Visibility == Visibility.Visible;

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        InitializeComponent();
        Reader.AnimationsEnabled = !arguments.Contains("--self-test");
        EncodingPicker.ItemsSource = new[] { "自动", "UTF-8", "GB18030", "GBK", "Big5", "UTF-16", "UTF-16BE" };
        EncodingPicker.SelectedIndex = 0;
        int testIndex = Array.IndexOf(arguments, "--self-test");
        int dataIndex = Array.IndexOf(arguments, "--data-dir");
        string? dataFolder = PackageProbe.Prepare(arguments) ?? (testIndex >= 0 ? Path.Combine(arguments[testIndex + 1], "state")
            : dataIndex >= 0 ? arguments[dataIndex + 1] : null);
        _store = new SessionStore(dataFolder);
        _library = new LibraryService(_store);
        Reader.PositionChanged += (_, _) => UpdateReadingUi();
        ChapterList.IsVisibleChanged += (_, _) => { if (ChapterList.IsVisible) RevealCurrentChapter(); };
        Reader.InteractionFailed += message => Notice.Text = message;
        Deactivated += (_, _) => Reader.CancelPageDrag();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Reader.CancelPageDrag(); };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) => SavePosition();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await SearchForAsync(SearchInput.Text); };
        ContentRendered += OnReady;
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; CancelSearch(); _searchTimer.Stop(); _saveTimer.Stop(); _store.Dispose(); };
    }

    private async void OnReady(object? sender, EventArgs e)
    {
        if (_ready) return;
        _ready = true;
        await ApplyAppearanceAsync(_store.ReadAppearance(), false);
        int index = Array.IndexOf(_arguments, "--self-test");
        if (index >= 0) { await PrototypeTests.RunAsync(this, _arguments[index + 1]); return; }
        await RunUiAsync(async () =>
        {
            string? supplied = _arguments.FirstOrDefault(x => File.Exists(x) && x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            var last = _store.Read();
            if (supplied is not null) await OpenFileAsync(supplied);
            else if (last is not null && File.Exists(last.Path)) await OpenFileAsync(last.Path, last.EncodingName);
            else await ShowShelfAsync();
        });
        _saveTimer.Start();
        if (DesktopProbe.Folder(_arguments) is not null) await DesktopProbe.RunAsync(this, _arguments);
        await PackageProbe.RunAsync(this);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeReady || _arguments.Contains("--self-test")) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _saveTimer.Stop();
        CancelSearch();
        await Reader.FlushPendingInputAsync();
        SavePosition(force: true);
        _closeReady = true;
        Close();
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            if (_closed) return;
            Notice.Text = ShelfNotice.Text = "操作未完成：" + ex.Message;
            MessageBox.Show(this, ex.Message, "栖读", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public async Task OpenFileAsync(string path, string? encoding = null)
    {
        if (_opening) return;
        _opening = true;
        OpenButton.IsEnabled = false;
        try
        {
            await Reader.FlushPendingInputAsync();
            SavePosition(force: true);
            Notice.Text = ShelfNotice.Text = "正在读取小说…";
            var imported = await _library.ImportAsync(path, encoding ?? "自动");
            await DisplayBookAsync(imported);
        }
        finally { _opening = false; OpenButton.IsEnabled = true; }
    }

    public async Task OpenLibraryBookAsync(string bookId)
    {
        var item = _store.FindBook(bookId) ?? throw new InvalidOperationException("这本书不在书架中。");
        if (!File.Exists(item.Path)) throw new FileNotFoundException("书库副本已找不到，请重新导入原 TXT；已有进度和书签仍保留。");
        await OpenFileAsync(item.Path, item.EncodingName);
    }

    public async Task<ImportBatchResult> ImportFilesAsync(IEnumerable<string> paths, bool stayOnShelf = true)
    {
        if (_opening) return new(0, 0, []);
        _opening = true;
        OpenButton.IsEnabled = false;
        var failures = new List<ImportFailure>();
        int importedCount = 0, duplicateCount = 0;
        ImportedBook? first = null;
        try
        {
            await Reader.FlushPendingInputAsync();
            SavePosition(force: true);
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Notice.Text = ShelfNotice.Text = "正在导入 " + Path.GetFileName(path);
                    var imported = await _library.ImportAsync(path);
                    first ??= imported;
                    if (imported.AlreadyPresent) duplicateCount++; else importedCount++;
                }
                catch (Exception ex) { failures.Add(new(Path.GetFileName(path), ex.Message)); }
            }
            if (first is not null && !stayOnShelf) await DisplayBookAsync(first);
            else await ShowShelfAsync();
            string summary = $"新加入 {importedCount} 本，已有 {duplicateCount} 本" + (failures.Count == 0 ? "" : $"，失败 {failures.Count} 本");
            Notice.Text = ShelfNotice.Text = summary;
            return new(importedCount, duplicateCount, failures);
        }
        finally { _opening = false; OpenButton.IsEnabled = true; }
    }

    private async Task DisplayBookAsync(ImportedBook imported)
    {
        ResetSearch();
        _returnAnchor = null;
        ReturnButton.Visibility = Visibility.Collapsed;
        var book = imported.Document;
        var session = _store.Read(book.ContentId);
        _activePath = null;
        _activeTitle = imported.Shelf.Title;
        _syncSelection = true;
        ChapterList.ItemsSource = book.Chapters;
        _syncSelection = false;
        ChapterCount.Text = $"{book.Chapters.Count} 节";
        SetShelfVisibility(false);
        await Reader.OpenAsync(book, session?.Offset ?? 0,
            session?.Mode == "Scroll" ? ReadingMode.Scroll : ReadingMode.Page, session?.FontSize ?? 22);
        _activePath = imported.Shelf.Path;
        UpdateReadingUi();
        RefreshBookmarks();
        Notice.Text = session is null ? "Ctrl+F 搜索全文，Ctrl+D 添加书签" : "已回到上次阅读的位置";
        SavePosition(force: true);
        RefreshShelf();
    }

    public async Task LoadTestBookAsync(BookDocument book, int anchor = 0)
    {
        ResetSearch();
        _activePath = null;
        _activeTitle = book.Title;
        _returnAnchor = null;
        ReturnButton.Visibility = Visibility.Collapsed;
        _syncSelection = true;
        ChapterList.ItemsSource = book.Chapters;
        _syncSelection = false;
        ChapterCount.Text = $"{book.Chapters.Count} 节";
        SetShelfVisibility(false);
        await Reader.OpenAsync(book, anchor);
        RefreshBookmarks();
    }

    public async Task ShowShelfAsync()
    {
        await Reader.FlushPendingInputAsync();
        SavePosition(force: true);
        SetShelfVisibility(true);
        RefreshShelf();
        ShelfNotice.Text = "阅读进度和书签保存在本机";
    }

    private void SetShelfVisibility(bool show)
    {
        LibraryWorkspace.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Keep the reader laid out behind the shelf so hiding it cannot reset scroll geometry.
        ReadingWorkspace.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        ReadingFooter.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        ReadingTools.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SidebarToggle.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        BookInfo.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { BookTitle.Text = "栖读"; BookInfo.Text = "你的本地小说书库"; Title = "我的书架 — 栖读"; }
        else UpdateReadingUi();
    }

    public async Task ApplyAppearanceAsync(ReadingAppearance appearance, bool persist = true)
    {
        _appearance = appearance.Normalize();
        ReadingPaper.MaxWidth = _appearance.Width == 0 ? double.PositiveInfinity : _appearance.Width *
            (Reader.Mode == ReadingMode.Page && _appearance.PageLayout == "double" ? 1.65 : 1);
        ReadingPaper.Background = _appearance.Palette.MakeBackground();
        SidebarColumn.Width = new GridLength(_appearance.SidebarVisible ? 272 : 0);
        Sidebar.Visibility = _appearance.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarToggle.Content = _appearance.SidebarVisible ? "收起目录" : "显示目录";
        await Reader.ApplyAppearanceAsync(_appearance);
        if (persist) _store.SaveAppearance(_appearance);
        Notice.Text = "排版已更新，阅读位置已保留";
    }

    public async Task SetReadingViewAsync(ReadingMode mode, string? pageLayout = null)
    {
        await Reader.FlushPendingInputAsync();
        if (pageLayout is not null) _appearance = _appearance with { PageLayout = pageLayout };
        await Reader.SetModeAsync(mode);
        await ApplyAppearanceAsync(_appearance);
        SavePosition();
    }

    public Task SetSidebarVisibleAsync(bool show) => ApplyAppearanceAsync(_appearance with { SidebarVisible = show });
    private async void OnToggleSidebar(object sender, RoutedEventArgs e) => await RunUiAsync(() => SetSidebarVisibleAsync(!_appearance.SidebarVisible));

    private void OnAppearance(object sender, RoutedEventArgs e)
    {
        var dialog = new AppearanceDialog(this, _appearance, value => RunUiAsync(() => ApplyAppearanceAsync(value)));
        dialog.ShowDialog();
    }

    private void RefreshShelf()
    {
        var books = _store.ListBooks(ShelfFilter.Text);
        ShelfList.ItemsSource = books;
        ShelfCount.Text = $"{books.Count} 本小说   /   按最近阅读排序";
        ShelfEmpty.Visibility = books.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfList.Visibility = books.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool filtered = !string.IsNullOrWhiteSpace(ShelfFilter.Text);
        ShelfEmptyMessage.Text = filtered ? "没有找到这个书名" : "书架还空着";
        ShelfEmptyDetail.Text = filtered ? "换个关键词，或清除筛选查看全部藏书" : "导入正在读的小说，也可以把 TXT 拖进来";
        ShelfEmptyImport.Visibility = filtered ? Visibility.Collapsed : Visibility.Visible;
        ShelfClearFilter.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        ContinueReadingButton.Visibility = _activePath is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateReadingUi()
    {
        if (Reader.Book is not { } book) return;
        if (!ShelfVisible)
        {
            BookTitle.Text = _activeTitle ?? book.Title;
            BookInfo.Text = $"{book.Chapters.Count:N0} 个章节";
            Title = $"{BookTitle.Text} — 栖读";
        }
        FontLabel.Text = Reader.ReadingFontSize.ToString("0");
        PageModeButton.Background = Reader.Mode == ReadingMode.Page && _appearance.PageLayout == "single" ? Brushes.White : Brushes.Transparent;
        SpreadModeButton.Background = Reader.Mode == ReadingMode.Page && _appearance.PageLayout == "double" ? Brushes.White : Brushes.Transparent;
        ScrollModeButton.Background = Reader.Mode == ReadingMode.Scroll ? Brushes.White : Brushes.Transparent;
        PageModeButton.FontWeight = Reader.Mode == ReadingMode.Page && _appearance.PageLayout == "single" ? FontWeights.SemiBold : FontWeights.Normal;
        SpreadModeButton.FontWeight = Reader.Mode == ReadingMode.Page && _appearance.PageLayout == "double" ? FontWeights.SemiBold : FontWeights.Normal;
        ScrollModeButton.FontWeight = Reader.Mode == ReadingMode.Scroll ? FontWeights.SemiBold : FontWeights.Normal;
        PreviousButton.Content = Reader.Mode == ReadingMode.Page ? "上一页" : "向上读";
        NextButton.Content = Reader.Mode == ReadingMode.Page ? "下一页" : "向下读";
        ReadingHint.Text = Reader.Mode == ReadingMode.Page ? "左右拖动或单击翻页 · ← →" : "滚轮上下阅读 · PgUp / PgDn";
        var chapter = book.ChapterAt(Reader.Anchor);
        ChapterLabel.Text = chapter.Title;
        bool chapterChanged = !ReferenceEquals(ChapterList.SelectedItem, chapter);
        _syncSelection = true;
        ChapterList.SelectedItem = chapter;
        _syncSelection = false;
        if (chapterChanged) RevealCurrentChapter();
        if (!ProgressSlider.IsMouseCaptureWithin && !ProgressSlider.IsKeyboardFocusWithin)
            ProgressSlider.Value = Reader.Anchor * 100d / Math.Max(1, book.Text.Length - 1);
        ProgressLabel.Text = $"{Reader.Anchor * 100d / Math.Max(1, book.Text.Length - 1):F1}%";
    }

    private void RevealCurrentChapter()
    {
        if (_closed || _chapterRevealQueued || !ChapterList.IsVisible || Reader.Book is null) return;
        _chapterRevealQueued = true;
        // Wait for a revealed tab/sidebar to finish layout. Read the current book
        // inside the callback so rapid navigation cannot reveal an obsolete chapter.
        Dispatcher.InvokeAsync(() =>
        {
            _chapterRevealQueued = false;
            if (_closed || !ChapterList.IsVisible || Reader.Book is not { } book) return;
            var chapter = book.ChapterAt(Reader.Anchor);
            if (ReferenceEquals(ChapterList.SelectedItem, chapter)) ChapterList.ScrollIntoView(chapter);
        }, DispatcherPriority.Loaded);
    }

    internal void SavePosition(bool force = false)
    {
        if (_closed || Reader.Book is not { } book || _activePath is null || !force && (_opening || Reader.IsBusy || ShelfVisible)) return;
        try { _store.Save(new(book.ContentId, _activePath, Reader.Anchor, Reader.Mode.ToString(), Reader.ReadingFontSize, book.EncodingName)); }
        catch (Exception ex) { Notice.Text = ShelfNotice.Text = "阅读位置未能保存：" + ex.Message; }
    }

    public async Task JumpAsync(int offset, int highlightLength = 0)
    {
        var book = Reader.Book;
        await Reader.FlushPendingInputAsync();
        if (!ReferenceEquals(book, Reader.Book)) return;
        if (highlightLength == 0 || !_searchJumpActive || _returnAnchor is null) _returnAnchor = Reader.Anchor;
        _searchJumpActive = highlightLength > 0;
        await Reader.NavigateAsync(offset, highlightLength);
        ReturnButton.Visibility = Visibility.Visible;
        SavePosition();
    }

    public async Task ReturnToReadingAsync()
    {
        if (_returnAnchor is not int offset) return;
        await Reader.NavigateAsync(offset);
        _returnAnchor = null;
        _searchJumpActive = false;
        ReturnButton.Visibility = Visibility.Collapsed;
        SavePosition();
        Notice.Text = "已返回跳转前的阅读位置";
    }

    public async Task<Bookmark> AddBookmarkAsync()
    {
        await Reader.FlushPendingInputAsync();
        var book = Reader.Book ?? throw new InvalidOperationException("请先打开一本小说。");
        var mark = _store.AddBookmark(book, Reader.Anchor);
        RefreshBookmarks();
        SavePosition();
        Notice.Text = "已保存当前位置书签";
        return mark;
    }

    public async Task JumpToBookmarkAsync(Bookmark mark)
    {
        if (Reader.Book?.ContentId != mark.BookId) throw new InvalidOperationException("书签属于另一本书，请先打开对应小说。");
        await JumpAsync(mark.Offset);
        Notice.Text = "已回到书签位置";
    }

    private void RefreshBookmarks()
    {
        var marks = Reader.Book is { } book ? _store.ListBookmarks(book.ContentId) : [];
        _syncSelection = true;
        BookmarkList.ItemsSource = marks;
        _syncSelection = false;
        BookmarkStatus.Text = marks.Count == 0 ? "还没有书签，读到喜欢的地方再添加" : $"{marks.Count} 个书签，按正文顺序排列";
    }

    private void CancelSearch()
    {
        _searchTimer?.Stop();
        _searchCancellation?.Cancel();
    }

    private void ResetSearch()
    {
        CancelSearch();
        _searchJumpActive = false;
        _searchCancellation = null;
        _searchPage = null;
        _searchPageIndex = 0;
        _searchStarts.Clear(); _searchStarts.Add(0);
        _syncSearch = true;
        SearchInput.Text = "";
        _syncSearch = false;
        SearchResults.ItemsSource = null;
        SearchPrevious.IsEnabled = SearchNext.IsEnabled = false;
        CancelSearchButton.Visibility = Visibility.Collapsed;
        SearchStatus.Text = "支持单字、人名和原文片段";
    }

    public Task SearchForAsync(string query)
    {
        _searchTimer.Stop();
        if (SearchInput.Text != query) _searchJumpActive = false;
        _syncSearch = true; SearchInput.Text = query; _syncSearch = false;
        _searchStarts.Clear(); _searchStarts.Add(0);
        _searchPageIndex = 0;
        return SearchCurrentPageAsync();
    }

    private async Task SearchCurrentPageAsync()
    {
        CancelSearch();
        var book = Reader.Book;
        string query = SearchInput.Text.Trim();
        _searchPage = null;
        SearchResults.ItemsSource = null;
        SearchPrevious.IsEnabled = SearchNext.IsEnabled = false;
        if (book is null || query.Length == 0) { SearchStatus.Text = "输入要查找的人名或原文"; return; }
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        SearchStatus.Text = "正在搜索全文…";
        SearchResults.IsEnabled = false;
        CancelSearchButton.Visibility = Visibility.Visible;
        try
        {
            var page = await BookSearch.FindPageAsync(book, query, _searchStarts[_searchPageIndex], cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested || !ReferenceEquals(book, Reader.Book) || !ReferenceEquals(_searchCancellation, cancellation)) return;
            _searchPage = page;
            SearchResults.ItemsSource = page.Hits;
            SearchPrevious.IsEnabled = _searchPageIndex > 0;
            SearchNext.IsEnabled = page.HasMore;
            int first = _searchPageIndex * BookSearch.PageSize + 1, last = first + page.Hits.Count - 1;
            SearchStatus.Text = page.Hits.Count == 0 ? "整本书没有找到这段文字"
                : $"第 {first}–{last} 条匹配" + (page.HasMore ? "，可查看下一批" : "，已到末尾");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_searchCancellation, cancellation)) SearchStatus.Text = "搜索已取消，按 Enter 重新搜索";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_searchCancellation, cancellation)) SearchStatus.Text = "搜索失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                CancelSearchButton.Visibility = Visibility.Collapsed;
                SearchResults.IsEnabled = true;
            }
            cancellation.Dispose();
        }
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "TXT 小说 (*.txt)|*.txt", Title = "导入本地小说（可多选）", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        bool stayOnShelf = ShelfVisible || dialog.FileNames.Length > 1;
        await RunUiAsync(async () =>
        {
            var result = await ImportFilesAsync(dialog.FileNames, stayOnShelf);
            if (result.Failures.Count > 0)
                MessageBox.Show(this, string.Join("\n\n", result.Failures.Select(x => x.Path + "：" + x.Reason)), "部分文件导入失败");
        });
    }

    private async void OnDemo(object sender, RoutedEventArgs e) => await RunUiAsync(() => OpenFileAsync(LibraryService.DemoPath, "UTF-8"));
    private async void OnShowShelf(object sender, RoutedEventArgs e) { if (!_opening) await RunUiAsync(ShowShelfAsync); }
    private void OnContinueReading(object sender, RoutedEventArgs e) { if (_activePath is not null) SetShelfVisibility(false); }
    private void OnShelfFilterChanged(object sender, TextChangedEventArgs e) { if (_ready) RefreshShelf(); }
    private void OnClearShelfFilter(object sender, RoutedEventArgs e) => ShelfFilter.Clear();
    private async void OnShelfRead(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is ShelfBook book) await RunUiAsync(() => OpenLibraryBookAsync(book.BookId)); }
    private async void OnShelfDoubleClick(object sender, MouseButtonEventArgs e) { if (ShelfList.SelectedItem is ShelfBook book) await RunUiAsync(() => OpenLibraryBookAsync(book.BookId)); }
    private void OnShelfRename(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShelfBook book) return;
        var dialog = new RenameDialog(this, book.Title);
        if (dialog.ShowDialog() != true) return;
        _store.RenameBook(book.BookId, dialog.BookName);
        if (Reader.Book?.ContentId == book.BookId) _activeTitle = dialog.BookName;
        RefreshShelf();
        ShelfNotice.Text = "书名已更新";
    }
    private void OnShelfRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShelfBook book) return;
        _store.ArchiveBook(book.BookId);
        if (Reader.Book?.ContentId == book.BookId) _activePath = null;
        RefreshShelf();
        ShelfNotice.Text = $"《{book.Title}》已移出书架；进度和书签仍保留，重新导入即可恢复";
    }

    private async void OnPageMode(object sender, RoutedEventArgs e) => await RunUiAsync(() => SetReadingViewAsync(ReadingMode.Page, "single"));
    private async void OnSpreadMode(object sender, RoutedEventArgs e) => await RunUiAsync(() => SetReadingViewAsync(ReadingMode.Page, "double"));
    private async void OnScrollMode(object sender, RoutedEventArgs e) => await RunUiAsync(() => SetReadingViewAsync(ReadingMode.Scroll));
    private async void OnFontMinus(object sender, RoutedEventArgs e) => await RunUiAsync(async () => { await Reader.AdjustFontAsync(-2); Notice.Text = "字号已调整，接着原文继续读"; SavePosition(); });
    private async void OnFontPlus(object sender, RoutedEventArgs e) => await RunUiAsync(async () => { await Reader.AdjustFontAsync(2); Notice.Text = "字号已调整，接着原文继续读"; SavePosition(); });
    private async void OnPrevious(object sender, RoutedEventArgs e) => await RunUiAsync(() => Reader.TurnAsync(-1));
    private async void OnNext(object sender, RoutedEventArgs e) => await RunUiAsync(() => Reader.TurnAsync(1));
    private async void OnReturnPosition(object sender, RoutedEventArgs e) => await RunUiAsync(ReturnToReadingAsync);
    private async void OnChapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncSelection && ChapterList.SelectedItem is Chapter chapter)
            await RunUiAsync(async () => { await JumpAsync(chapter.Offset); Notice.Text = "已跳转到 " + chapter.Title; });
    }
    private async void OnSeek(object sender, MouseButtonEventArgs e) => await SeekFromSliderAsync();
    private async void OnSeekKey(object sender, KeyEventArgs e) { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) await SeekFromSliderAsync(); }
    private Task SeekFromSliderAsync() => RunUiAsync(async () =>
    {
        if (Reader.Book is { } book) await JumpAsync((int)(ProgressSlider.Value / 100 * Math.Max(0, book.Text.Length - 1)));
    });
    private async void OnEncodingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_opening && _activePath is not null)
            await RunUiAsync(() => OpenFileAsync(_activePath, EncodingPicker.SelectedItem as string ?? "自动"));
    }
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncSearch) return;
        CancelSearch();
        _searchJumpActive = false;
        _searchPage = null;
        SearchResults.ItemsSource = null;
        SearchPrevious.IsEnabled = SearchNext.IsEnabled = false;
        _searchTimer.Start();
    }
    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchForAsync(SearchInput.Text); }
        if (e.Key == Key.Escape) { e.Handled = true; CancelSearch(); }
    }
    private void OnCancelSearch(object sender, RoutedEventArgs e) => CancelSearch();
    private async void OnSearchPrevious(object sender, RoutedEventArgs e) => await SearchPreviousPageAsync();
    private async void OnSearchNext(object sender, RoutedEventArgs e) => await SearchNextPageAsync();
    public async Task SearchPreviousPageAsync() { if (_searchPageIndex > 0) { _searchPageIndex--; await SearchCurrentPageAsync(); } }
    public async Task SearchNextPageAsync()
    {
        if (_searchPage?.HasMore != true) return;
        int next = _searchPage.NextOffset;
        _searchPageIndex++;
        if (_searchStarts.Count <= _searchPageIndex) _searchStarts.Add(next); else _searchStarts[_searchPageIndex] = next;
        await SearchCurrentPageAsync();
    }
    private async void OnSearchResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is SearchHit hit)
        {
            SearchResults.SelectedItem = null;
            await RunUiAsync(async () => { await JumpAsync(hit.Offset, hit.Length); Notice.Text = "已定位搜索结果，可返回原位置继续读"; });
        }
    }
    private async void OnAddBookmark(object sender, RoutedEventArgs e) => await RunUiAsync(async () => { await AddBookmarkAsync(); });
    private async void OnBookmarkSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncSelection && BookmarkList.SelectedItem is Bookmark mark)
        {
            BookmarkList.SelectedItem = null;
            await RunUiAsync(() => JumpToBookmarkAsync(mark));
        }
    }
    private void OnDeleteBookmark(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Bookmark mark) return;
        _store.DeleteBookmark(mark.BookId, mark.Id);
        RefreshBookmarks();
        Notice.Text = "书签已删除";
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await RunUiAsync(async () =>
        {
            var result = await ImportFilesAsync(paths, ShelfVisible || paths.Length > 1);
            if (result.Failures.Count > 0) MessageBox.Show(this, string.Join("\n", result.Failures.Select(x => x.Path + "：" + x.Reason)), "部分文件导入失败");
        });
    }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Reader.CancelPageDrag()) { e.Handled = true; return; }
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; OnOpen(this, new RoutedEventArgs()); return; }
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control && !ShelfVisible)
        { e.Handled = true; await RunUiAsync(() => SetSidebarVisibleAsync(!_appearance.SidebarVisible)); return; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !ShelfVisible)
        { e.Handled = true; await SetSidebarVisibleAsync(true); SidebarTabs.SelectedItem = SearchTab; SearchInput.Focus(); SearchInput.SelectAll(); return; }
        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control && !ShelfVisible)
        { e.Handled = true; await SetSidebarVisibleAsync(true); SidebarTabs.SelectedItem = BookmarksTab; await RunUiAsync(async () => { await AddBookmarkAsync(); }); return; }
        if (e.Key == Key.F11) { e.Handled = true; WindowStyle = WindowStyle == WindowStyle.None ? WindowStyle.SingleBorderWindow : WindowStyle.None; WindowState = WindowStyle == WindowStyle.None ? WindowState.Maximized : WindowState.Normal; return; }
        if (e.Key == Key.Escape && WindowStyle == WindowStyle.None) { e.Handled = true; WindowStyle = WindowStyle.SingleBorderWindow; WindowState = WindowState.Normal; return; }
        if (ShelfVisible || Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is TextBox ||
            EncodingPicker.IsKeyboardFocusWithin || ProgressSlider.IsKeyboardFocusWithin) return;
        // Left/right always turn in page mode, including after selecting a chapter or bookmark.
        // Up/down and PgUp/PgDn remain available for navigating a focused sidebar list.
        bool horizontalPageKey = Reader.Mode == ReadingMode.Page && e.Key is Key.Right or Key.Left;
        if (!horizontalPageKey && (ChapterList.IsKeyboardFocusWithin || SearchResults.IsKeyboardFocusWithin || BookmarkList.IsKeyboardFocusWithin)) return;
        if (e.Key is Key.PageDown or Key.PageUp || Reader.Mode == ReadingMode.Page && e.Key is Key.Right or Key.Left or Key.Space)
        { e.Handled = true; await RunUiAsync(() => Reader.TurnAsync(e.Key is Key.PageUp or Key.Left ? -1 : 1)); }
    }
}
