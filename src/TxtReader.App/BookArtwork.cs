using System.IO;
using System.Windows.Media.Imaging;

namespace TxtReader.App;

internal static class BookArtwork
{
    internal static BitmapSource Decode(byte[] bytes, int maxWidth = 1200, int maxHeight = 1600)
    {
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 100_000_000)
            throw new InvalidDataException("插图尺寸超过支持范围。");
        double scale = Math.Min(1, Math.Min(maxWidth / (double)frame.PixelWidth, maxHeight / (double)frame.PixelHeight));
        var bitmap = new BitmapImage();
        stream.Position = 0;
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
