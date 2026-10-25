using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace TxtReader.App;

public sealed partial class ReaderSurface
{
    private sealed record CandidatePage(FlowDocument Document, DynamicDocumentPaginator Paginator,
        List<RunMap> Runs, int Chunk, int Page, int Anchor, BitmapSource Image);
    private sealed class PageDrag(Point start, int direction, bool top, Size size, string effect, double travel)
    {
        internal readonly Point Start = start;
        internal readonly int Direction = direction;
        internal readonly bool Top = top;
        internal readonly Size Size = size;
        internal readonly string Effect = effect;
        internal Point Pointer = start;
        internal bool Released, Settling;
        internal Task? Preparation;
        internal CandidatePage? Candidate;
        internal double Progress => PageDragMath.Progress(Start, Pointer, travel, Direction);
    }
    private PageDrag? _drag;
    private Point? _pagePress;
    private bool _pagePressMoved;
    private bool _releasingCapture;
    private readonly Path _cornerHint = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        Fill = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
        Stroke = new SolidColorBrush(Color.FromArgb(55, 80, 68, 48)), StrokeThickness = .7 };
    public bool CornerDragActive => _drag is not null;
    public event Action<string>? InteractionFailed;
    internal bool DragPreviewReady => _drag?.Candidate is not null;
    internal double DragProgress => _drag?.Progress ?? 0;
    internal double LastDragPreparationMs { get; private set; }
    internal double LastDragSettleMs { get; private set; }
    internal double DragMaxFrameMs => _turnLayer.MaxFrameMs;
    internal Point? DragProjectedCorner => _turnLayer.ProjectedCorner;
    internal double DragCurlRadius => _turnLayer.CurlRadius;
    internal double DragTravel => PageDragMath.Travel(ActualWidth, VisiblePages);

    private void InitializePageDrag()
    {
        Children.Add(_cornerHint);
        _pages.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!BeginPagePointer(e.GetPosition(_pages))) return;
            e.Handled = true;
            _pages.Focus();
            if (!Mouse.Capture(_pages, CaptureMode.Element)) CancelPageDrag();
        };
        _pages.PreviewMouseMove += (_, e) =>
        {
            var point = e.GetPosition(_pages);
            if (_pagePress is not null) { MovePagePointer(point); e.Handled = true; }
            else UpdateCornerHint(point);
        };
        _pages.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            if (_pagePress is null) return;
            e.Handled = true;
            try { await EndPagePointerAsync(e.GetPosition(_pages)); }
            catch (Exception ex) { CancelPageDrag(); InteractionFailed?.Invoke("翻页未完成：" + ex.Message); }
        };
        _pages.LostMouseCapture += (_, _) => { if (!_releasingCapture) CancelPageDrag(); };
        _pages.MouseLeave += (_, _) => { if (_drag is null) HideCornerHint(); };
        Unloaded += (_, _) => CancelPageDrag();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelPageDrag(); };
    }

    // Delay choosing a direction until a horizontal gesture is established.
    // Every paper location uses the same gesture and the same distance threshold.
    // Cancellation clears both states, so a late mouse-up can never turn a page.
    internal bool BeginPagePointer(Point point)
    {
        if (_pagePress is not null || Mode != ReadingMode.Page || _book is null || IsBusy || _operationGate.CurrentCount == 0 ||
            _resizeTimer.IsEnabled || !PageViewsReady || !new Rect(RenderSize).Contains(point)) return false;
        _pagePress = point;
        _pagePressMoved = false;
        return true;
    }

    internal void MovePagePointer(Point point)
    {
        if (_pagePress is not { } start || !RuntimeCompat.IsFinite(point.X) || !RuntimeCompat.IsFinite(point.Y)) return;
        var delta = point - start;
        _pagePressMoved |= delta.Length >= 6;
        if (_drag is null && Math.Abs(delta.X) >= 6 && Math.Abs(delta.X) >= Math.Abs(delta.Y))
            BeginPageDrag(start, delta.X < 0 ? 1 : -1);
        if (_drag is not null) UpdateCornerDrag(point);
    }

    internal async Task EndPagePointerAsync(Point point)
    {
        if (_pagePress is not { } start) return;
        MovePagePointer(point);
        bool click = !_pagePressMoved && new Rect(RenderSize).Contains(point);
        _pagePress = null;
        if (click)
        {
            CancelPageDrag();
            ReleasePageCapture();
            await TurnAsync(start.X < ActualWidth / 2 ? -1 : 1);
        }
        else if (_drag is not null) await ReleaseCornerDragAsync(point);
        else ReleasePageCapture();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        CancelPageDrag();
        base.OnDpiChanged(oldDpi, newDpi);
    }

    private bool CanTurnPage(int direction) => _book is not null && _paginator is not null && Mode == ReadingMode.Page &&
        (direction > 0 ? _pageIndex + VisiblePages < _paginator.PageCount || _centerChunk + 1 < _book.Chunks.Count
            : _pageIndex > 0 || _centerChunk > 0);

    internal bool BeginCornerDrag(Point point)
    {
        var corner = PageDragMath.Corner(point, RenderSize, VisiblePages);
        return corner is { } c && BeginPageDrag(point, c.Direction);
    }

    private bool BeginPageDrag(Point point, int direction)
    {
        if (IsBusy || _operationGate.CurrentCount == 0 || _resizeTimer.IsEnabled || !PageViewsReady || !CanTurnPage(direction)) return false;
        string effect = AnimationsEnabled && SystemParameters.ClientAreaAnimation ? _appearance.TurnEffect : "none";
        if (effect == "curl" && RenderCapability.Tier == 0) effect = "fade";
        _drag = new PageDrag(point, direction, point.Y < ActualHeight / 2, RenderSize, effect, DragTravel);
        HideCornerHint();
        _pages.Cursor = Cursors.Hand;
        return true;
    }

    internal void UpdateCornerDrag(Point point)
    {
        var drag = _drag;
        if (drag is null || drag.Released || drag.Settling || !RuntimeCompat.IsFinite(point.X) || !RuntimeCompat.IsFinite(point.Y)) return;
        drag.Pointer = point;
        if (drag.Preparation is null && (point - drag.Start).Length >= 6) drag.Preparation = PrepareDragAsync(drag);
        if (drag.Candidate is not null) _turnLayer.UpdateDrag(point);
    }

    internal Task WaitForDragPreviewAsync() => _drag?.Preparation ?? Task.CompletedTask;

    private async Task PrepareDragAsync(PageDrag drag)
    {
        await _operationGate.WaitAsync();
        Grid? staging = null;
        var watch = Stopwatch.StartNew();
        try
        {
            if (!ReferenceEquals(_drag, drag)) return;
            var before = PageTurnLayer.Capture(_pages);
            int chunk = _centerChunk, page = _pageIndex + drag.Direction * VisiblePages;
            var document = _document!;
            var paginator = _paginator!;
            var runs = _runs.ToList();
            if (page < 0 || page >= paginator.PageCount)
            {
                chunk += drag.Direction;
                runs = [];
                document = BuildDocument(chunk, chunk, runs);
                document.PageWidth = Math.Max(100, drag.Size.Width / VisiblePages);
                document.PageHeight = Math.Max(100, drag.Size.Height);
                paginator = (DynamicDocumentPaginator)((IDocumentPaginatorSource)document).DocumentPaginator;
                paginator.IsBackgroundPaginationEnabled = false;
                paginator.ComputePageCount();
                page = drag.Direction > 0 ? 0 : (paginator.PageCount - 1) / VisiblePages * VisiblePages;
            }
            var pointer = paginator.GetPagePosition(paginator.GetPage(page)) as TextPointer;
            int anchor = OffsetIn(runs, pointer);
            staging = new Grid { Background = Background, IsHitTestVisible = false };
            staging.ColumnDefinitions.Add(new());
            staging.ColumnDefinitions.Add(new() { Width = VisiblePages == 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0) });
            var left = new DocumentPageView { Stretch = Stretch.None, PageNumber = page, DocumentPaginator = paginator };
            staging.Children.Add(left);
            if (VisiblePages == 2)
            {
                var right = new DocumentPageView { Stretch = Stretch.None, PageNumber = page + 1, DocumentPaginator = paginator };
                Grid.SetColumn(right, 1); staging.Children.Add(right);
                var spine = new Border { Width = _spine.Width, Background = _spine.Background, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetColumnSpan(spine, 2); staging.Children.Add(spine);
            }
            // Stage behind the opaque current pages; the live document and its anchor stay untouched.
            Panel.SetZIndex(staging, -1);
            Children.Add(staging);
            await SettleAsync();
            if (!ReferenceEquals(_drag, drag)) return;
            if (left.DocumentPage == DocumentPage.Missing) throw new InvalidOperationException("下一页尚未准备好，请重试。");
            var image = PageTurnLayer.Capture(staging);
            drag.Candidate = new(document, paginator, runs, chunk, page, anchor, image);
            _turnLayer.BeginDrag(before, image, drag.Effect, drag.Direction, VisiblePages == 2, drag.Top, drag.Start);
            _turnLayer.UpdateDrag(drag.Pointer);
            LastDragPreparationMs = watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_drag, drag)) { CancelPageDrag(); InteractionFailed?.Invoke("翻页未完成：" + ex.Message); }
        }
        finally
        {
            if (staging is not null)
            {
                foreach (var view in staging.Children.OfType<DocumentPageView>()) view.DocumentPaginator = null;
                Children.Remove(staging);
            }
            _operationGate.Release();
        }
    }

    private static int OffsetIn(List<RunMap> runs, TextPointer? pointer)
    {
        if (pointer is not null)
            foreach (var map in runs)
            {
                if (pointer.CompareTo(map.Run.ContentStart) <= 0) return map.Start;
                if (pointer.CompareTo(map.Run.ContentEnd) <= 0)
                    return map.Run is Run ? map.Start + RuntimeCompat.Clamp(map.Run.ContentStart.GetOffsetToPosition(pointer), 0, map.Length) : map.Start;
            }
        return runs.Count == 0 ? 0 : runs[^1].Start + runs[^1].Length;
    }

    internal async Task ReleaseCornerDragAsync(Point point)
    {
        var drag = _drag;
        if (drag is null || drag.Released) return;
        UpdateCornerDrag(point);
        drag.Released = true;
        ReleasePageCapture();
        bool complete = drag.Progress >= .5;
        if (drag.Preparation is null || !complete && drag.Candidate is null) { CancelPageDrag(); return; }
        await drag.Preparation;
        if (!ReferenceEquals(_drag, drag) || drag.Candidate is null) return;
        drag.Settling = true;
        var watch = Stopwatch.StartNew();
        bool settled = await _turnLayer.SettleDragAsync(complete, drag.Effect != "none");
        if (!ReferenceEquals(_drag, drag)) return;
        if (settled && complete)
        {
            await _operationGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(_drag, drag)) return;
                var candidate = drag.Candidate;
                _document = candidate.Document;
                _paginator = candidate.Paginator;
                _runs.Clear(); _runs.AddRange(candidate.Runs);
                _firstChunk = _lastChunk = _centerChunk = candidate.Chunk;
                _leftPage.DocumentPaginator = _paginator;
                _rightPage.DocumentPaginator = VisiblePages == 2 ? _paginator : null;
                ShowPage(candidate.Page);
                _pages.UpdateLayout();
                // This is the only commit point, after the leaf has landed.
                _anchor = candidate.Anchor;
            }
            finally { _operationGate.Release(); }
        }
        LastDragSettleMs = watch.Elapsed.TotalMilliseconds;
        CancelPageDrag();
        if (settled && complete) PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReleasePageCapture()
    {
        _releasingCapture = true;
        try { if (Mouse.Captured == _pages) Mouse.Capture(null); }
        finally { _releasingCapture = false; }
    }

    public bool CancelPageDrag()
    {
        bool hadPress = _pagePress is not null;
        _pagePress = null;
        if (_drag is null) { if (hadPress) ReleasePageCapture(); HideCornerHint(); return hadPress; }
        _drag = null; // Invalidate async preparation/settlement before releasing capture.
        ReleasePageCapture();
        _turnLayer.Cancel();
        HideCornerHint();
        return true;
    }

    private void HideCornerHint()
    {
        _cornerHint.Visibility = Visibility.Collapsed;
        _pages.Cursor = null;
        _pages.ToolTip = null;
    }

    private void UpdateCornerHint(Point point)
    {
        var corner = PageDragMath.Corner(point, RenderSize, VisiblePages);
        if (IsBusy || corner is not { } c || !CanTurnPage(c.Direction)) { HideCornerHint(); return; }
        double x = c.Direction > 0 ? ActualWidth : 0, y = c.Top ? 0 : ActualHeight;
        double dx = -c.Direction * 19, dy = c.Top ? 19 : -19;
        var fold = new StreamGeometry();
        using (var geometry = fold.Open())
        {
            geometry.BeginFigure(new Point(x + dx, y), true, true);
            geometry.QuadraticBezierTo(new Point(x + dx * .65, y + dy * .65), new Point(x, y + dy), true, false);
            geometry.LineTo(new Point(x + dx, y + dy), true, false);
        }
        _cornerHint.Data = fold;
        _cornerHint.Visibility = Visibility.Visible;
        _pages.Cursor = Cursors.Hand;
        _pages.ToolTip = "正文任意位置左右拖动翻页；拉回松手回弹，Esc 取消";
    }
}
