using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Qidu.Desktop;

internal static class Artwork
{
    internal static Bitmap? Decode(byte[] bytes, int width = 1200, int height = 1600)
    {
        try
        {
            using var header = new MemoryStream(bytes);
            using var codec = SKCodec.Create(header);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > 100_000_000) return null;
            using var stream = new MemoryStream(bytes);
            return (double)codec.Info.Width / codec.Info.Height > (double)width / height
                ? Bitmap.DecodeToWidth(stream, Math.Min(width, codec.Info.Width))
                : Bitmap.DecodeToHeight(stream, Math.Min(height, codec.Info.Height));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return null; }
    }
}
