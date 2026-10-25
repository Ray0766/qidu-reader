using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using TxtReader.Core;

namespace TxtReader.App;

public enum ReadingMode { Page, Scroll }
public sealed record ReaderSnapshot(int Anchor, int FirstVisible, int LastVisible, bool AnchorVisible,
    string Excerpt, string Mode, double FontSize, int LoadedCharacters, int Chunk, int Page, int PageCount,
    int HighlightedCharacters = 0, string FontFamily = "", string Theme = "", double LayoutWidth = 0,
    int VisiblePages = 1, int AnchorPage = 0, int[]? VisiblePageStarts = null);

/// <summary>Both views use FlowDocument; all durable positions are offsets in BookDocument.Text.</summary>
public sealed partial class ReaderSurface : Grid
{
    private readonly Grid _pages;
    private readonly DocumentPageView _leftPage, _rightPage;
    private readonly Border _spine;
    private readonly PageTurnLayer _turnLayer;
    private readonly RichTextBox _scroll;
    private readonly List<RunMap> _runs = [];
    private readonly DispatcherTimer _resizeTimer;
    private readonly SemaphoreSlim _layoutGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Task _pendingScrollCapture = Task.CompletedTask;
    private FlowDocument? _document;
    private DynamicDocumentPaginator? _paginator;
    private BookDocument? _book;
    private int _anchor, _firstChunk, _lastChunk, _centerChunk;
    private int _highlightStart = -1, _highlightLength;
    private bool _restoring, _scrollCaptureQueued;
    private double _scrollAnchorY = 24;
    private ReadingAppearance _appearance = new();
    private int _pageIndex;

    public ReadingMode Mode { get; private set; } = ReadingMode.Page;
    public double ReadingFontSize { get; private set; } = 22;
    public BookDocument? Book => _book;
    public int Anchor => _anchor;
    public bool IsBusy => _restoring || _turnLayer.IsRunning || CornerDragActive;
    public int VisiblePages => Mode == ReadingMode.Page && _appearance.PageLayout == "double" ? 2 : 1;
    internal bool AnimationsEnabled { get; set; } = true;
    internal int LastAnimationFrames => _turnLayer.Frames;
    internal bool AnimationLayerVisible => _turnLayer.Visibility == Visibility.Visible;
    internal bool PageViewsReady => _leftPage.DocumentPage != DocumentPage.Missing &&
        (VisiblePages == 1 || _pageIndex + 1 >= (_paginator?.PageCount ?? 0) || _rightPage.DocumentPage != DocumentPage.Missing);
    public event EventHandler? PositionChanged;

    private sealed record RunMap(Run Run, int Start, int Length);

    public ReaderSurface()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(252, 252, 250));
        _pages = new Grid { Focusable = true, Background = Background };
        _pages.ColumnDefinitions.Add(new());
        _pages.ColumnDefinitions.Add(new() { Width = new GridLength(0) });
        _leftPage = new DocumentPageView { Stretch = Stretch.None };
        _rightPage = new DocumentPageView { Stretch = Stretch.None, Visibility = Visibility.Collapsed };
        Grid.SetColumn(_rightPage, 1);
        _pages.Children.Add(_leftPage); _pages.Children.Add(_rightPage);
        _spine = new Border { Width = 26, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        var fold = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        fold.GradientStops.Add(new(Colors.Transparent, 0));
        fold.GradientStops.Add(new(Color.FromArgb(7, 55, 43, 29), .30));
        fold.GradientStops.Add(new(Color.FromArgb(20, 55, 43, 29), .46));
        fold.GradientStops.Add(new(Color.FromArgb(48, 55, 43, 29), .49));
        fold.GradientStops.Add(new(Color.FromArgb(48, 55, 43, 29), .51));
        fold.GradientStops.Add(new(Color.FromArgb(22, 255, 255, 255), .55));
        fold.GradientStops.Add(new(Color.FromArgb(5, 55, 43, 29), .73));
        fold.GradientStops.Add(new(Colors.Transparent, 1));
        _spine.Background = fold;
        Grid.SetColumnSpan(_spine, 2); _pages.Children.Add(_spine);
        _turnLayer = new PageTurnLayer();
        _scroll = new RichTextBox
        {
            IsReadOnly = true, IsReadOnlyCaretVisible = false, IsDocumentEnabled = true,
            BorderThickness = new Thickness(0), Padding = new Thickness(28, 24, 28, 24), Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed, AllowDrop = false
        };
        Children.Add(_pages);
        Children.Add(_scroll);
        Children.Add(_turnLayer);
        InitializePageDrag();
        _scroll.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrolled));
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _resizeTimer.Tick += async (_, _) => { _resizeTimer.Stop(); await ChangeAsync(RebuildAsync); };
        SizeChanged += (_, _) =>
        {
            if (_book is null) return;
            CancelPageDrag();
            _restoring = true; // Layout-driven scrolling must not replace the user's content anchor.
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };
        _pages.PreviewMouseWheel += async (_, e) =>
        {
            e.Handled = true;
            if (!_restoring) await TurnAsync(e.Delta < 0 ? 1 : -1);
        };
    }

    public Task OpenAsync(BookDocument book, int offset = 0, ReadingMode mode = ReadingMode.Page, double fontSize = 22) => ChangeAsync(async () =>
    {
        _book = book;
        _highlightStart = -1;
        _highlightLength = 0;
        _anchor = book.ClampOffset(offset);
        ReadingFontSize = Math.Clamp(fontSize, 14, 36);
        Mode = mode;
        _scrollAnchorY = 24;
        await RebuildAsync();
    });

    public Task SetModeAsync(ReadingMode mode) => ChangeAsync(async () =>
    {
        if (Mode == mode) return;
        Mode = mode;
        _scrollAnchorY = 24;
        await RebuildAsync();
    });

    public Task SetFontAsync(double size) => ChangeAsync(async () =>
    {
        ReadingFontSize = Math.Clamp(size, 14, 36);
        _scrollAnchorY = 24;
        await RebuildAsync();
    });

    public Task AdjustFontAsync(double delta) => ChangeAsync(async () =>
    {
        ReadingFontSize = Math.Clamp(ReadingFontSize + delta, 14, 36);
        _scrollAnchorY = 24;
        await RebuildAsync();
    });

    public Task NavigateAsync(int offset, int highlightLength = 0) => ChangeAsync(async () =>
    {
        if (_book is null) return;
        _anchor = _book.ClampOffset(offset);
        _highlightStart = highlightLength > 0 ? offset : -1;
        _highlightLength = highlightLength;
        _scrollAnchorY = 24;
        await RebuildAsync();
    });

    public Task TurnAsync(int direction) => CornerDragActive ? Task.CompletedTask : ChangeAsync(async () =>
    {
        if (_book is null || _restoring) return;
        if (Mode == ReadingMode.Scroll)
        {
            // Capture once, after this programmatic movement. Otherwise ScrollChanged
            // can start a second, unawaited rebuild while the caller changes modes.
            _restoring = true;
            try
            {
                _scroll.ScrollToVerticalOffset(Math.Max(0, _scroll.VerticalOffset + direction * Math.Max(80, ActualHeight - ReadingFontSize * 2)));
                await SettleAsync();
            }
            finally { _restoring = false; }
            await CaptureScrollAsync();
            return;
        }
        int page = _pageIndex;
        int count = _paginator?.PageCount ?? 0;
        bool canTurn = direction > 0 ? page + VisiblePages < count || _centerChunk + 1 < _book.Chunks.Count : page > 0 || _centerChunk > 0;
        if (!canTurn) return;
        string effect = AnimationsEnabled && SystemParameters.ClientAreaAnimation ? _appearance.TurnEffect : "none";
        if (effect == "curl" && RenderCapability.Tier == 0) effect = "fade";
        var before = effect == "none" ? null : PageTurnLayer.Capture(_pages);
        _turnLayer.ResetFrames();
        if (before is not null) _turnLayer.Hold(before);
        if (direction > 0 && page + VisiblePages < count || direction < 0 && page > 0)
        {
            _restoring = true;
            ShowPage(Math.Max(0, page + direction * VisiblePages));
            await SettleAsync();
            _anchor = PageStart(_pageIndex);
            _restoring = false;
        }
        else
        {
            int next = _centerChunk + direction;
            if (next < 0 || next >= _book.Chunks.Count) return;
            _anchor = direction > 0 ? _book.Chunks[next].Start : Math.Max(_book.Chunks[next].Start, _book.Chunks[next].End - 1);
            await RebuildAsync();
            _anchor = PageStart(_pageIndex);
        }
        if (before is not null) await _turnLayer.PlayAsync(before, PageTurnLayer.Capture(_pages), effect, direction, VisiblePages == 2);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    });

    public Task RefreshLayoutAsync() => ChangeAsync(RebuildAsync);

    public Task ApplyAppearanceAsync(ReadingAppearance appearance) => ChangeAsync(async () =>
    {
        _appearance = appearance.Normalize();
        Background = _appearance.Palette.MakeBackground();
        _pages.Background = Background;
        _scrollAnchorY = 24;
        await RebuildAsync();
    });

    public Task FlushPendingInputAsync() => ChangeAsync(() => Task.CompletedTask);

    private async Task ChangeAsync(Func<Task> action)
    {
        CancelPageDrag();
        await _operationGate.WaitAsync();
        try
        {
            // Drain queued wheel input before a search/bookmark jump or a view change.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await _pendingScrollCapture;
            if (_resizeTimer.IsEnabled) { _resizeTimer.Stop(); await RebuildAsync(); }
            await action();
        }
        finally { _operationGate.Release(); }
    }

    private async Task RebuildAsync()
    {
        if (_book is null || ActualWidth < 100 || ActualHeight < 100) return;
        await _layoutGate.WaitAsync();
        try
        {
            _restoring = true;
            _resizeTimer.Stop();
            _centerChunk = _book.ChunkAt(_anchor);
            _firstChunk = Mode == ReadingMode.Page ? _centerChunk : Math.Max(0, _centerChunk - 1);
            _lastChunk = Mode == ReadingMode.Page ? _centerChunk : Math.Min(_book.Chunks.Count - 1, _centerChunk + 1);
            _leftPage.DocumentPaginator = null;
            _rightPage.DocumentPaginator = null;
            _scroll.Document = new FlowDocument();
            _runs.Clear();
            _document = BuildDocument();
            if (Mode == ReadingMode.Page)
            {
                _scroll.Visibility = Visibility.Collapsed;
                _pages.Visibility = Visibility.Visible;
                _pages.ColumnDefinitions[1].Width = VisiblePages == 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                _rightPage.Visibility = _spine.Visibility = VisiblePages == 2 ? Visibility.Visible : Visibility.Collapsed;
                _document.PageWidth = Math.Max(100, ActualWidth / VisiblePages);
                _document.PageHeight = Math.Max(100, ActualHeight);
                _paginator = (DynamicDocumentPaginator)((IDocumentPaginatorSource)_document).DocumentPaginator;
                _paginator.IsBackgroundPaginationEnabled = false;
                _paginator.ComputePageCount();
                _leftPage.DocumentPaginator = _paginator;
                _rightPage.DocumentPaginator = VisiblePages == 2 ? _paginator : null;
                await SettleAsync();
                var pointer = PointerAt(_anchor);
                int page = pointer is null ? 0 : _paginator.GetPageNumber(pointer);
                ShowPage(Math.Max(0, page) / VisiblePages * VisiblePages);
            }
            else
            {
                _pages.Visibility = Visibility.Collapsed;
                _scroll.Visibility = Visibility.Visible;
                _paginator = null;
                _scroll.Document = _document;
                await SettleAsync();
                RestoreScrollPosition();
            }
            await SettleAsync();
        }
        finally { _restoring = false; _layoutGate.Release(); }
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowPage(int page)
    {
        _pageIndex = page;
        _leftPage.PageNumber = page;
        _rightPage.PageNumber = page + 1;
    }

    private FlowDocument BuildDocument(int? firstChunk = null, int? lastChunk = null, List<RunMap>? runMap = null)
    {
        var book = _book!;
        var maps = runMap ?? _runs;
        var document = new FlowDocument
        {
            FontFamily = new FontFamily(_appearance.FontFamily), FontSize = ReadingFontSize,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_appearance.Palette.Foreground)),
            Background = Background, PagePadding = new Thickness(28, 24, 28, 24),
            ColumnWidth = 100000, IsColumnWidthFlexible = false,
            LineHeight = ReadingFontSize * 1.85,
            Language = XmlLanguage.GetLanguage("zh-CN"),
            TextAlignment = TextAlignment.Left, IsHyphenationEnabled = false,
            IsOptimalParagraphEnabled = false
        };
        int from = book.Chunks[firstChunk ?? _firstChunk].FirstBlock;
        var last = book.Chunks[lastChunk ?? _lastChunk];
        Paragraph? paragraph = null;
        bool hasVisibleContent = false;
        for (int i = from; i < last.FirstBlock + last.BlockCount; i++)
        {
            var block = book.Blocks[i];
            if (!block.Continuation || paragraph is null)
            {
                paragraph = new Paragraph
                {
                    Margin = new Thickness(0, 0, 0, block.Length == 0 ? 0 : ReadingFontSize * .5),
                    TextIndent = !block.Heading && !block.Continuation && block.Length > 0 && !char.IsWhiteSpace(book.Text[block.Start]) ? ReadingFontSize * 2 : 0,
                    KeepTogether = false, KeepWithNext = block.Heading,
                    BreakPageBefore = Mode == ReadingMode.Page && block.Heading && hasVisibleContent,
                    LineHeight = block.Length == 0 ? ReadingFontSize * .6 : ReadingFontSize * 1.85
                };
                if (block.Heading)
                {
                    paragraph.FontFamily = new FontFamily("Microsoft YaHei UI");
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.FontSize = ReadingFontSize + 2;
                    paragraph.Margin = new Thickness(0, 16, 0, 18);
                }
                document.Blocks.Add(paragraph);
            }
            int highlightFrom = Math.Max(block.Start, _highlightStart);
            int highlightTo = Math.Min(block.Start + block.Length, _highlightStart + _highlightLength);
            void AddRun(int start, int length, bool highlighted = false)
            {
                var run = new Run(book.Text.Substring(start, length));
                if (highlighted)
                {
                    run.Background = new SolidColorBrush(Color.FromRgb(255, 240, 174));
                    run.Foreground = new SolidColorBrush(Color.FromRgb(41, 53, 64));
                }
                paragraph.Inlines.Add(run);
                maps.Add(new(run, start, length));
            }
            if (_highlightStart >= 0 && highlightTo > highlightFrom)
            {
                if (highlightFrom > block.Start) AddRun(block.Start, highlightFrom - block.Start);
                AddRun(highlightFrom, highlightTo - highlightFrom, true);
                if (highlightTo < block.Start + block.Length) AddRun(highlightTo, block.Start + block.Length - highlightTo);
            }
            else AddRun(block.Start, block.Length);
            hasVisibleContent |= block.Length > 0 && !string.IsNullOrWhiteSpace(book.Text.Substring(block.Start, block.Length));
        }
        return document;
    }

    private TextPointer? PointerAt(int offset)
    {
        foreach (var map in _runs)
        {
            if (offset <= map.Start + map.Length)
                return map.Run.ContentStart.GetPositionAtOffset(Math.Clamp(offset - map.Start, 0, map.Length), LogicalDirection.Forward);
        }
        return _runs.LastOrDefault()?.Run.ContentEnd;
    }

    private int SourceOffset(TextPointer? pointer)
    {
        if (pointer is null) return _anchor;
        foreach (var map in _runs)
        {
            if (pointer.CompareTo(map.Run.ContentStart) <= 0) return map.Start;
            if (pointer.CompareTo(map.Run.ContentEnd) <= 0)
                return map.Start + Math.Clamp(map.Run.ContentStart.GetOffsetToPosition(pointer), 0, map.Length);
        }
        return _runs.Count > 0 ? _runs[^1].Start + _runs[^1].Length : _anchor;
    }

    private int PageStart(int page)
    {
        if (_paginator is null) return _anchor;
        var p = _paginator.GetPage(page);
        return SourceOffset(_paginator.GetPagePosition(p) as TextPointer);
    }

    private void RestoreScrollPosition()
    {
        var pointer = PointerAt(_anchor);
        if (pointer is null) return;
        // Public WPF geometry is relative to the RichTextBox view; preserve the same
        // content point rather than scaling a previous scrollbar percentage.
        var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
        if (!rect.IsEmpty) _scroll.ScrollToVerticalOffset(Math.Max(0, _scroll.VerticalOffset + rect.Top - _scrollAnchorY));
    }

    private void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_restoring || Mode != ReadingMode.Scroll || _scrollCaptureQueued || Math.Abs(e.VerticalChange) < .01) return;
        _scrollCaptureQueued = true;
        _pendingScrollCapture = Dispatcher.InvokeAsync(async () =>
        {
            _scrollCaptureQueued = false;
            if (!_restoring) await CaptureScrollAsync();
        }, DispatcherPriority.Background).Task.Unwrap();
    }

    private async Task CaptureScrollAsync()
    {
        if (_book is null || Mode != ReadingMode.Scroll || _restoring) return;
        var pointer = _scroll.GetPositionFromPoint(new Point(30, 25), true);
        if (pointer is null) return;
        _anchor = _book.ClampOffset(SourceOffset(pointer));
        var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
        _scrollAnchorY = rect.IsEmpty ? 24 : rect.Top;
        int actualChunk = _book.ChunkAt(_anchor);
        if (actualChunk != _centerChunk) await RebuildAsync();
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public ReaderSnapshot Snapshot()
    {
        if (_book is null || _document is null) return new(0, 0, 0, false, "", Mode.ToString(), ReadingFontSize, 0, 0, 0, 0);
        int first, last;
        bool visible;
        int page = _pageIndex;
        int anchorPage = 0;
        int[] starts = [];
        if (Mode == ReadingMode.Page && _paginator is not null)
        {
            first = PageStart(page);
            last = page + VisiblePages < _paginator.PageCount ? PageStart(page + VisiblePages) : _book.Chunks[_lastChunk].End;
            var pointer = PointerAt(_anchor);
            anchorPage = pointer is null ? -1 : _paginator.GetPageNumber(pointer);
            visible = pointer is not null && anchorPage >= page && anchorPage < page + VisiblePages;
            starts = Enumerable.Range(page, Math.Min(VisiblePages, _paginator.PageCount - page)).Select(PageStart).ToArray();
        }
        else
        {
            first = SourceOffset(_scroll.GetPositionFromPoint(new Point(30, 25), true));
            last = SourceOffset(_scroll.GetPositionFromPoint(new Point(Math.Max(30, ActualWidth - 45), Math.Max(25, ActualHeight - 25)), true));
            var rect = PointerAt(_anchor)?.GetCharacterRect(LogicalDirection.Forward) ?? Rect.Empty;
            visible = !rect.IsEmpty && rect.Bottom >= 0 && rect.Top < ActualHeight;
        }
        return new(_anchor, first, last, visible, _book.Excerpt(_anchor), Mode.ToString(), ReadingFontSize,
            _book.Chunks[_lastChunk].End - _book.Chunks[_firstChunk].Start, _centerChunk, page + 1, _paginator?.PageCount ?? 0,
            _runs.Where(x => x.Run.Background is SolidColorBrush).Sum(x => x.Length),
            _document.FontFamily.Source, _appearance.Theme, ActualWidth, VisiblePages, anchorPage + 1, starts);
    }

    private async Task SettleAsync()
    {
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }
}
