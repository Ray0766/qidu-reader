using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using TxtReader.App;

namespace Qidu.Desktop;

/// <summary>Bounded, character-anchored layout shared by the Mac UI's reading modes.</summary>
internal sealed class ReaderView : Control, IDisposable
{
    private sealed record Row(int Start, int End, double Height, TextLine? Line, Bitmap? Image, bool Break);
    private sealed class Layout : IDisposable
    {
        public readonly List<Row> Rows = [];
        public readonly List<TextLayout> Text = [];
        public readonly List<(int From, int To)> Pages = [];
        public void Dispose() { foreach (var text in Text) text.Dispose(); }
    }
    private readonly record struct Page(int Chunk, int Index);
    private readonly Dictionary<int, Layout> _layouts = [];
    private readonly Dictionary<BookImage, Bitmap> _images = [];
    private double _lastWidth, _lastHeight, _scrollRemainder;
    private Point? _press;
    private IPointer? _pointer;
    private int _direction, _candidate;
    private double _progress;
    private RenderTargetBitmap? _before, _after;
    private DispatcherTimer? _animation;
    private bool _releasing;
    public BookDocument? Book { get; private set; }
    public int Anchor { get; private set; }
    public string Mode { get; private set; } = "page";
    public double ReadingFontSize { get; private set; } = 22;
    public ReadingAppearance Appearance { get; private set; } = new ReadingAppearance().Normalize();
    public int HighlightLength { get; private set; }
    public bool AnimationsEnabled { get; set; } = true;
    public bool IsDragging => _before is not null;
    public double DragProgress => _progress;
    public event Action? PositionChanged;
    public int CachedChunks => _layouts.Count;
    public int VisibleImages { get; private set; }
    public bool AnchorVisible { get; private set; }
    private int Columns => Mode == "page" && Appearance.PageLayout == "double" ? 2 : 1;
    private double PaperWidth => Math.Max(180, Math.Min(Bounds.Width - 40, Appearance.Width == 0 ? Bounds.Width - 40 : Appearance.Width * (Columns == 2 ? 1.48 : 1)));
    private double LeafWidth => PaperWidth / Columns;
    private double TextWidth => Math.Max(90, LeafWidth - 80);
    private double TextHeight => Math.Max(90, Bounds.Height - 104);
    private Rect Paper => new((Bounds.Width - PaperWidth) / 2, 16, PaperWidth, Math.Max(120, Bounds.Height - 32));
    private IBrush PaperBrush => Brush.Parse(Appearance.Background);
    private IBrush Ink => Brush.Parse(Appearance.Foreground);

    public ReaderView() { Focusable = true; ClipToBounds = true; }
    public void Open(BookDocument book, int anchor, string mode, double font, ReadingAppearance appearance)
    {
        CancelDrag(); ClearLayout(true); Book = book;
        Anchor = book.ClampOffset(anchor); Mode = mode == "scroll" ? "scroll" : "page";
        ReadingFontSize = Math.Clamp(font, 14, 40); Appearance = appearance.Normalize();
        HighlightLength = 0; _scrollRemainder = 0; InvalidateVisual();
    }
    public void Configure(string mode, double font, ReadingAppearance appearance)
    {
        CancelDrag(); Mode = mode == "scroll" ? "scroll" : "page";
        ReadingFontSize = Math.Clamp(font, 14, 40); Appearance = appearance.Normalize();
        ClearLayout(false); _scrollRemainder = 0; InvalidateVisual(); PositionChanged?.Invoke();
    }
    public void Navigate(int offset, int highlight = 0)
    {
        CancelDrag(); if (Book is null) return;
        Anchor = Book.ClampOffset(offset); HighlightLength = highlight; _scrollRemainder = 0;
        InvalidateVisual(); PositionChanged?.Invoke();
    }
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e); CancelDrag(); ClearLayout(false); _scrollRemainder = 0;
    }
    private void ClearLayout(bool images)
    {
        foreach (var layout in _layouts.Values) layout.Dispose(); _layouts.Clear();
        if (images) { foreach (var image in _images.Values) image.Dispose(); _images.Clear(); }
    }
    private Bitmap? Image(BookImage image)
    {
        if (_images.TryGetValue(image, out var cached)) return cached;
        var bitmap = Artwork.Decode(image.Data);
        if (bitmap is null) return null;
        _images.Add(image, bitmap); return bitmap;
    }
    private Layout GetLayout(int chunkIndex)
    {
        if (_layouts.TryGetValue(chunkIndex, out var cached)) return cached;
        if (Book is null) throw new InvalidOperationException();
        var chunk = Book.Chunks[chunkIndex]; var result = new Layout();
        for (int b = chunk.FirstBlock; b < chunk.FirstBlock + chunk.BlockCount; b++)
        {
            var block = Book.Blocks[b];
            if (block.Image is { } illustration && Image(illustration) is { } bitmap)
            {
                double scale = Math.Min(TextWidth / bitmap.Size.Width, (TextHeight - 4) / bitmap.Size.Height);
                result.Rows.Add(new(block.Start, block.Start + block.Length, bitmap.Size.Height * scale + 4, null, bitmap, block.PageBreakBefore));
                continue;
            }
            double size = ReadingFontSize * (block.Heading ? 1.18 : 1);
            var text = new TextLayout(block.Image is not null ? "[插图无法显示]" : block.Length == 0 ? " " : Book.Text.Substring(block.Start, block.Length),
                new Typeface(Appearance.FontFamily, FontStyle.Normal, block.Heading ? FontWeight.SemiBold : FontWeight.Normal),
                size, Ink, textWrapping: TextWrapping.Wrap, maxWidth: TextWidth, lineHeight: size * 1.7);
            result.Text.Add(text);
            for (int lineIndex = 0; lineIndex < text.TextLines.Count; lineIndex++)
            {
                var line = text.TextLines[lineIndex];
                int start = block.Start + Math.Min(block.Length, line.FirstTextSourceIndex);
                int end = block.Start + Math.Min(block.Length, line.FirstTextSourceIndex + line.Length);
                bool last = lineIndex == text.TextLines.Count - 1;
                result.Rows.Add(new(start, Math.Max(start + 1, end + (last ? 1 : 0)), line.Height + (last ? size * .48 : 0), line, null,
                    lineIndex == 0 && (block.Heading || block.PageBreakBefore)));
            }
        }
        int from = 0; double height = 0;
        for (int i = 0; i < result.Rows.Count; i++)
        {
            var row = result.Rows[i];
            if (i > from && (row.Break || height + row.Height > TextHeight))
            { result.Pages.Add((from, i)); from = i; height = 0; }
            height += row.Height;
        }
        result.Pages.Add((from, result.Rows.Count));
        _layouts.Add(chunkIndex, result); return result;
    }
    private Page At(int anchor)
    {
        int chunk = Book!.ChunkAt(anchor); var layout = GetLayout(chunk); int page = 0;
        for (int i = 1; i < layout.Pages.Count; i++)
            if (layout.Rows[layout.Pages[i].From].Start <= anchor) page = i; else break;
        return new(chunk, page);
    }
    private Page? Neighbor(Page page, int direction)
    {
        if (direction > 0)
        {
            if (page.Index + 1 < GetLayout(page.Chunk).Pages.Count) return page with { Index = page.Index + 1 };
            return page.Chunk + 1 < Book!.Chunks.Count ? new(page.Chunk + 1, 0) : null;
        }
        if (page.Index > 0) return page with { Index = page.Index - 1 };
        return page.Chunk > 0 ? new(page.Chunk - 1, GetLayout(page.Chunk - 1).Pages.Count - 1) : null;
    }
    private int Offset(Page page) => GetLayout(page.Chunk).Rows[GetLayout(page.Chunk).Pages[page.Index].From].Start;
    internal int Destination(int direction)
    {
        if (Book is null) return Anchor;
        Page? page = At(Anchor); Page? last = page;
        for (int i = 0; i < Columns; i++)
        {
            page = Neighbor(page!.Value, direction);
            if (page is null) break;
            last = page;
        }
        // A final unpaired leaf is already visible in a two-page spread.
        if (direction > 0 && Columns == 2 && page is null) return Anchor;
        return last is null ? Anchor : Offset(last.Value);
    }
    public void Turn(int direction)
    {
        if (Mode == "scroll") { Scroll(direction * TextHeight * .85); return; }
        if (IsDragging) return;
        if (BeginDrag(direction)) ReleaseDrag(true);
    }
    public void Scroll(double pixels)
    {
        if (Book is null) return; CancelDrag();
        int chunk = Book.ChunkAt(Anchor); var layout = GetLayout(chunk);
        int row = FindRow(layout, Anchor); double y = _scrollRemainder + pixels;
        while (y >= layout.Rows[row].Height)
        {
            double h = layout.Rows[row].Height;
            if (row + 1 < layout.Rows.Count) row++;
            else if (chunk + 1 < Book.Chunks.Count) { chunk++; layout = GetLayout(chunk); row = 0; }
            else { y = 0; break; }
            y -= h;
        }
        while (y < 0)
        {
            if (row > 0) row--;
            else if (chunk > 0) { chunk--; layout = GetLayout(chunk); row = layout.Rows.Count - 1; }
            else { y = 0; break; }
            y += layout.Rows[row].Height;
        }
        Anchor = Book.ClampOffset(layout.Rows[row].Start); _scrollRemainder = y;
        HighlightLength = 0; InvalidateVisual(); PositionChanged?.Invoke(); TrimCache();
    }
    private static int FindRow(Layout layout, int offset)
    {
        int i = 0; for (; i + 1 < layout.Rows.Count && layout.Rows[i + 1].Start <= offset; i++) { }
        return i;
    }
    private void TrimCache()
    {
        if (Book is null || _layouts.Count <= 8) return;
        int current = Book.ChunkAt(Anchor);
        foreach (int key in _layouts.Keys.Where(k => Math.Abs(k - current) > 2).ToArray())
        { _layouts[key].Dispose(); _layouts.Remove(key); }
        var used = _layouts.Values.SelectMany(l => l.Rows).Select(r => r.Image).Where(i => i is not null).ToHashSet();
        foreach (var key in _images.Keys.Where(k => !used.Contains(_images[k])).ToArray())
        { _images[key].Dispose(); _images.Remove(key); }
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_lastWidth != Bounds.Width || _lastHeight != Bounds.Height)
        { _lastWidth = Bounds.Width; _lastHeight = Bounds.Height; ClearLayout(false); }
        if (_before is null) RenderBook(context, Anchor);
        else RenderTurn(context);
        TrimCache();
    }
    private void RenderBook(DrawingContext context, int anchor)
    {
        VisibleImages = 0; AnchorVisible = false;
        var paper = Paper;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)), paper.Translate(new Vector(0, 4)));
        context.FillRectangle(PaperBrush, paper);
        if (Appearance.Theme == "paper")
            for (int y = (int)paper.Top + 3; y < paper.Bottom; y += 5)
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(7, 90, 67, 28)), .5), new(paper.Left + 2, y), new(paper.Right - 2, y));
        if (Book is null) return;
        using (context.PushClip(paper.Deflate(new Thickness(0, 25))))
        {
            if (Mode == "scroll")
            {
                int chunk = Book.ChunkAt(anchor); var layout = GetLayout(chunk); int row = FindRow(layout, anchor);
                double y = paper.Top + 36 - _scrollRemainder;
                while (y < paper.Bottom - 28)
                {
                    var item = layout.Rows[row]; DrawRow(context, item, paper.Left + 40, y, anchor);
                    y += item.Height; row++;
                    if (row >= layout.Rows.Count)
                    { if (++chunk >= Book.Chunks.Count) break; layout = GetLayout(chunk); row = 0; }
                }
            }
            else
            {
                Page? page = At(anchor);
                for (int column = 0; column < Columns && page is not null; column++)
                {
                    var layout = GetLayout(page.Value.Chunk); var range = layout.Pages[page.Value.Index];
                    double y = paper.Top + 36;
                    for (int r = range.From; r < range.To; r++)
                    { DrawRow(context, layout.Rows[r], paper.Left + column * LeafWidth + 40, y, anchor); y += layout.Rows[r].Height; }
                    page = Neighbor(page.Value, 1);
                }
            }
        }
        if (Columns == 2)
        {
            double mid = paper.Center.X;
            for (int x = -12; x <= 12; x++)
                context.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(28 * (1 - Math.Abs(x) / 13d)), 40, 35, 30)), new Rect(mid + x, paper.Top, 1, paper.Height));
        }
    }
    private void DrawRow(DrawingContext context, Row row, double x, double y, int anchor)
    {
        if (row.Start <= anchor && row.End > anchor) AnchorVisible = true;
        if (row.Image is { } bitmap)
        {
            double scale = Math.Min(TextWidth / bitmap.Size.Width, (TextHeight - 4) / bitmap.Size.Height);
            double w = bitmap.Size.Width * scale, h = bitmap.Size.Height * scale;
            context.DrawImage(bitmap, new Rect(x + (TextWidth - w) / 2, y, w, h)); VisibleImages++; return;
        }
        if (row.Line is not { } line) return;
        if (HighlightLength > 0 && row.Start < Anchor + HighlightLength && row.End > Anchor)
        {
            int first = line.FirstTextSourceIndex + Math.Max(0, Anchor - row.Start);
            int last = line.FirstTextSourceIndex + Math.Min(line.Length, Anchor + HighlightLength - row.Start);
            double a = line.GetDistanceFromCharacterHit(new CharacterHit(first, 0));
            double b = line.GetDistanceFromCharacterHit(new CharacterHit(last, 0));
            context.FillRectangle(Brush.Parse("#DCCA84"), new Rect(x + a, y, Math.Max(1, b - a), line.Height));
        }
        line.Draw(context, new Point(x, y));
    }
    private RenderTargetBitmap Snapshot(int anchor)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(Bounds.Width), (int)Math.Ceiling(Bounds.Height)), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext()) RenderBook(context, anchor);
        return bitmap;
    }
    internal bool BeginDrag(int direction)
    {
        if (Book is null || Mode != "page" || Bounds.Width < 100) return false;
        CancelDrag(); _candidate = Destination(direction); if (_candidate == Anchor) return false;
        _direction = direction; _before = Snapshot(Anchor); _after = Snapshot(_candidate); _progress = 0;
        InvalidateVisual(); return true;
    }
    internal void MoveDrag(double distance)
    {
        if (!IsDragging || _animation is not null) return;
        _progress = Math.Clamp(distance / Math.Clamp(LeafWidth * .7, 120, 360), 0, 1);
        InvalidateVisual();
    }
    internal void ReleaseDrag(bool? complete = null)
    {
        if (!IsDragging) return;
        bool commit = complete ?? _progress >= .32;
        if (!AnimationsEnabled || Appearance.TurnEffect == "none") { Finish(commit); return; }
        double start = _progress, target = commit ? 1 : 0; var clock = System.Diagnostics.Stopwatch.StartNew();
        _animation?.Stop(); _animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animation.Tick += (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / 220);
            _progress = start + (target - start) * (1 - Math.Pow(1 - t, 3)); InvalidateVisual();
            if (t >= 1) Finish(commit);
        };
        _animation.Start();
    }
    private void Finish(bool commit)
    {
        int offset = _candidate; CancelDrag();
        if (commit) Navigate(offset);
    }
    public void CancelDrag()
    {
        _animation?.Stop(); _animation = null; _before?.Dispose(); _after?.Dispose(); _before = _after = null;
        _press = null; _progress = 0;
        var pointer = _pointer; _pointer = null;
        _releasing = true; pointer?.Capture(null); _releasing = false;
        InvalidateVisual();
    }
    private void RenderTurn(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        context.DrawImage(_after!, rect);
        if (Appearance.TurnEffect == "fade")
        { using (context.PushOpacity(1 - _progress)) context.DrawImage(_before!, rect); return; }
        if (Appearance.TurnEffect == "slide")
        { using (context.PushClip(rect)) context.DrawImage(_before!, rect.Translate(new Vector(-_direction * PaperWidth * _progress, 0))); return; }
        // A leaf bends over a rounded cylindrical fold. Narrow strips retain the
        // text texture while the returned side receives a warm paper tint.
        var paper = Paper; double leaf = LeafWidth;
        double origin = Columns == 2 ? paper.Center.X : (_direction > 0 ? paper.Left : paper.Right);
        if (Columns == 2)
        {
            var still = _direction > 0 ? new Rect(paper.Left, paper.Top, leaf, paper.Height) : new Rect(paper.Center.X, paper.Top, leaf, paper.Height);
            context.DrawImage(_before!, still, still);
        }
        double target = leaf * (1 - 2 * _progress), radius = Math.Min(leaf * .075, leaf * _progress * .28);
        double fold = (leaf + target) / 2 - radius * Math.PI / 2;
        double edge = origin + _direction * (fold + radius);
        for (int shadow = 18; shadow >= 0; shadow--)
            context.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(3 + (18 - shadow) * 2), 30, 25, 18)),
                new Rect(_direction > 0 ? edge + shadow : edge - shadow - 1, paper.Top + 3, 1.2, paper.Height));
        double Map(double x)
        {
            double d = x - fold;
            if (d <= 0) return x;
            if (radius < .001) return fold - d;
            return fold + (d < Math.PI * radius ? radius * Math.Sin(d / radius) : Math.PI * radius - d);
        }
        for (double x = 0; x < leaf; x += 4)
        {
            double end = Math.Min(leaf, x + 4), a = Map(x), b = Map(end);
            double sourceX = _direction > 0 ? origin + x : origin - end;
            double destX = _direction > 0 ? origin + Math.Min(a, b) : origin - Math.Max(a, b);
            var source = new Rect(sourceX, paper.Top, end - x, paper.Height);
            double angle = Math.Clamp((x - fold) / Math.Max(.001, radius), 0, Math.PI);
            double lift = radius * (1 - Math.Cos(angle)) * .18;
            var destination = new Rect(destX, paper.Top - lift, Math.Max(.2, Math.Abs(b - a) + .35), paper.Height + lift * 2);
            if (b < a && Columns == 2)
            {
                // The reverse of the turning leaf is the new left page when
                // going forward, or the new right page when going backward.
                double backX = _direction > 0 ? paper.Left + leaf - end : paper.Center.X + x;
                context.DrawImage(_after!, new Rect(backX, paper.Top, end - x, paper.Height), destination);
            }
            else context.DrawImage(_before!, source, destination);
            if (b < a) context.FillRectangle(new SolidColorBrush(Color.FromArgb(Columns == 2 ? (byte)22 : (byte)215, 238, 229, 206)), destination);
            else if (x > fold) context.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(55 * Math.Min(1, (x - fold) / Math.Max(1, radius * Math.PI))), 35, 30, 20)), destination);
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(45, 70, 60, 42)), .6), destination.TopLeft, destination.TopRight);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Book is null) return;
        CancelDrag(); Focus(); _press = e.GetPosition(this); _pointer = e.Pointer; e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (_press is not { } start || Mode != "page") return;
        double distance = e.GetPosition(this).X - start.X;
        if (!IsDragging && Math.Abs(distance) > 7)
        {
            var pointer = _pointer; var press = _press;
            if (!BeginDrag(distance < 0 ? 1 : -1)) return;
            _press = press; _pointer = pointer; pointer?.Capture(this);
        }
        if (IsDragging) MoveDrag(-_direction * distance);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); if (_press is not { } point) return;
        _press = null; _releasing = true; _pointer?.Capture(null); _pointer = null; _releasing = false;
        if (IsDragging) ReleaseDrag();
        else if (Mode == "page") Turn(point.X < Bounds.Width / 2 ? -1 : 1);
        e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); if (!_releasing) CancelDrag(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Mode == "scroll") Scroll(-e.Delta.Y * ReadingFontSize * 3);
        else if (Math.Abs(e.Delta.Y) > .01) Turn(e.Delta.Y < 0 ? 1 : -1);
        e.Handled = true;
    }
    public void Dispose() { CancelDrag(); ClearLayout(true); }
}
