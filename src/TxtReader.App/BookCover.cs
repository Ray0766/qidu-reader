using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TxtReader.App;

/// <summary>Local typographic covers, derived from book identity; no remote artwork.</summary>
public sealed class BookCover : FrameworkElement
{
    public static readonly DependencyProperty BookProperty = DependencyProperty.Register(nameof(Book), typeof(ShelfBook), typeof(BookCover), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public ShelfBook? Book { get => (ShelfBook?)GetValue(BookProperty); set => SetValue(BookProperty, value); }
    private static readonly string[] ColorsByBook = ["#344F46", "#485667", "#6B4647", "#745E3E", "#4A5050", "#54506A"];
    private string? _coverPath;
    private BitmapSource? _cover;
    protected override void OnRender(DrawingContext dc)
    {
        if (Book is not { } book || ActualWidth < 40) return;
        double w = ActualWidth, h = ActualHeight;
        int hash = book.BookId.Aggregate(0, (a, c) => (a * 31 + c) & 0x7fffffff);
        var color = (Color)ColorConverter.ConvertFromString(ColorsByBook[hash % ColorsByBook.Length]);
        var gold = new SolidColorBrush(Color.FromRgb(218, 203, 169));
        // The paper block, cloth cover and stitched spine have distinct physical edges.
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(24, 0, 0, 0)), null, new Rect(7, 7, w - 7, h - 7), 2, 2);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(224, 218, 200)), null, new Rect(13, 4, w - 16, h - 8));
        for (int i = 0; i < 4; i++)
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(45, 101, 92, 76)), .5), new Point(w - 8 + i, 5), new Point(w - 8 + i, h - 7));
        dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, w - 8, h - 4), 2, 2);
        string coverPath = Path.Combine(Path.GetDirectoryName(book.Path) ?? "", ".qidu-cover");
        if (_coverPath != coverPath)
        {
            _coverPath = coverPath; _cover = null;
            try { if (File.Exists(coverPath) && new FileInfo(coverPath).Length <= 16 * 1024 * 1024) _cover = BookArtwork.Decode(File.ReadAllBytes(coverPath), 400, 600); }
            catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException) { }
        }
        if (_cover is not null)
        {
            double scale = Math.Min((w - 8) / _cover.PixelWidth, (h - 4) / _cover.PixelHeight);
            double imageWidth = _cover.PixelWidth * scale, imageHeight = _cover.PixelHeight * scale;
            dc.DrawImage(_cover, new Rect((w - 8 - imageWidth) / 2, (h - 4 - imageHeight) / 2, imageWidth, imageHeight));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(100, 0, 0, 0), Colors.Transparent, 0), null, new Rect(0, 0, 12, h - 4));
            return;
        }
        var cloth = new Pen(new SolidColorBrush(Color.FromArgb(9, 255, 255, 255)), .5);
        for (double y = 3; y < h - 4; y += 3) dc.DrawLine(cloth, new Point(0, y), new Point(w - 8, y));
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(110, 0, 0, 0), Colors.Transparent, 0), null, new Rect(0, 0, 14, h - 4));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), .7), new Point(14, 1), new Point(14, h - 5));
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(100, 218, 203, 169)), .7), new Rect(25, 19, w - 51, h - 46));
        var title = new FormattedText(book.Title, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("SimSun"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 22, gold, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = w - 70, MaxTextHeight = h - 105, TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis, LineHeight = 34 };
        dc.DrawText(title, new Point(34, 46));
        double center = (w + 8) / 2;
        dc.DrawLine(new Pen(gold, .8), new Point(center - 12, h - 59), new Point(center + 12, h - 59));
        dc.DrawEllipse(null, new Pen(gold, .7), new Point(center, h - 59), 3, 3);
        var label = new FormattedText("本地藏书", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 9, gold, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(center - label.Width / 2, h - 44));
        if (book.HasRead && book.Progress > 0)
        {
            var ribbon = new StreamGeometry();
            using (var c = ribbon.Open())
            { c.BeginFigure(new Point(w - 43, 0), true, true); c.LineTo(new Point(w - 27, 0), true, false); c.LineTo(new Point(w - 27, 31), true, false); c.LineTo(new Point(w - 35, 26), true, false); c.LineTo(new Point(w - 43, 31), true, false); }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(177, 151, 103)), null, ribbon);
        }
    }
}

public sealed class BookshelfPanel : WrapPanel
{
    public BookshelfPanel() { ItemWidth = 210; ItemHeight = 326; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (InternalChildren.Count == 0) return;
        for (double y = 234; y < ActualHeight; y += ItemHeight)
        {
            var top = new LinearGradientBrush(Color.FromRgb(200, 190, 169), Color.FromRgb(180, 169, 148), 90);
            dc.DrawRectangle(top, null, new Rect(0, y, ActualWidth, 6));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(157, 145, 123)), null, new Rect(0, y + 6, ActualWidth, 5));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(37, 46, 39, 26), Colors.Transparent, 90), null, new Rect(0, y + 11, ActualWidth, 11));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(75, 255, 255, 255)), 1), new Point(0, y), new Point(ActualWidth, y));
        }
    }
}
