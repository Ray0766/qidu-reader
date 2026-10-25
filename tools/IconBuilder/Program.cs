using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    // The SVG is the editable source of truth. Only the simple vector primitives
    // used by this icon are supported; unknown elements fail instead of vanishing.
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--store"))
            throw new ArgumentException("IconBuilder <app-icon.svg> <asset directory> [--store]");
        var svg = XDocument.Load(args[0]).Root!;
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            foreach (var element in svg.Elements())
            {
                double Number(string name) => double.Parse(element.Attribute(name)?.Value ?? "0", CultureInfo.InvariantCulture);
                if (element.Name.LocalName is "title" or "desc") continue;
                var fill = (Brush)new BrushConverter().ConvertFromInvariantString(element.Attribute("fill")!.Value)!;
                switch (element.Name.LocalName)
                {
                    case "rect":
                        dc.DrawRoundedRectangle(fill, null, new Rect(Number("x"), Number("y"), Number("width"), Number("height")), Number("rx"), Number("rx"));
                        break;
                    case "path":
                        dc.DrawGeometry(fill, null, Geometry.Parse(element.Attribute("d")!.Value));
                        break;
                    default: throw new InvalidDataException("Unsupported icon element: " + element.Name);
                }
            }
        }
        drawing.Freeze();
        byte[] Render(int size)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / 512d, size / 512d));
                dc.DrawDrawing(drawing);
                dc.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        if (args.Length == 3)
        {
            // Resource qualifiers let Windows choose a native-size raster at each DPI.
            foreach (var (name, size) in new[] { ("Square44x44Logo", 44), ("Square150x150Logo", 150), ("StoreLogo", 50) })
            {
                File.WriteAllBytes(Path.Combine(output, name + ".png"), Render(size));
                foreach (int scale in new[] { 125, 150, 200, 400 })
                    File.WriteAllBytes(Path.Combine(output, $"{name}.scale-{scale}.png"), Render((int)Math.Round(size * scale / 100d, MidpointRounding.AwayFromZero)));
            }
            foreach (int size in new[] { 16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256 })
                File.WriteAllBytes(Path.Combine(output, $"Square44x44Logo.targetsize-{size}_altform-unplated.png"), Render(size));
            File.WriteAllBytes(Path.Combine(output, "StoreListing-300.png"), Render(300));
            Console.WriteLine("Generated 31 Store PNG assets from the existing SVG.");
            return;
        }
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var frames = sizes.Select(Render).ToArray();
        using (var writer = new BinaryWriter(File.Create(Path.Combine(output, "AppIcon.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
        File.WriteAllBytes(Path.Combine(output, "AppIcon.png"), Render(512));
        File.WriteAllBytes(Path.Combine(output, "AppIcon-1024.png"), Render(1024));
        Console.WriteLine($"Generated PNG 512/1024 and ICO [{string.Join(", ", sizes)}] from {Path.GetFullPath(args[0])}");
    }
}
