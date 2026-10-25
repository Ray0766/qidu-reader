using System.Windows;
using System.Windows.Media;
using System.Text.Json.Serialization;

namespace TxtReader.App;

public sealed record FontChoice(string Name, string Family);
public sealed record WidthChoice(string Name, int Width);
public sealed record ThemeChoice(string Key, string Name, string Background, string Foreground, bool Paper = false)
{
    public Brush MakeBackground()
    {
        var color = (Color)ColorConverter.ConvertFromString(Background);
        if (!Paper) return new SolidColorBrush(color);
        // A deterministic, native vector paper grain: no downloaded image or large bitmap.
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 256, 256));
            var random = new Random(417);
            for (int i = 0; i < 2600; i++)
            {
                var fiber = new SolidColorBrush(Color.FromArgb((byte)random.Next(8, 23), 117, 95, 61));
                double x = random.NextDouble() * 256, y = random.NextDouble() * 256;
                context.DrawEllipse(fiber, null, new Point(x, y), .15 + random.NextDouble() * .4, .2 + random.NextDouble() * .5);
            }
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(14, 137, 111, 74)), .35);
            for (int i = 0; i < 160; i++)
            {
                double x = random.NextDouble() * 250, y = random.NextDouble() * 250;
                context.DrawLine(pen, new Point(x, y), new Point(x + random.NextDouble() * 5, y + random.NextDouble() * 1.4));
            }
        }
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 256, 256), ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 256, 256), Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }
}

public sealed record EffectChoice(string Name, string Key);

public sealed record ReadingAppearance(string FontFamily = "SimSun", string Theme = "white", int Width = 790,
    string PageLayout = "single", string TurnEffect = "curl", bool SidebarVisible = true)
{
    public static readonly FontChoice[] Fonts = [new("宋体", "SimSun"), new("微软雅黑", "Microsoft YaHei"),
        new("楷体", "KaiTi"), new("仿宋", "FangSong"), new("等线", "DengXian")];
    public static readonly WidthChoice[] Widths = [new("窄", 620), new("标准", 790), new("宽", 1000), new("铺满可用空间", 0)];
    public static readonly EffectChoice[] Effects = [new("纸页翻动", "curl"), new("平移", "slide"), new("淡入淡出", "fade"), new("无动效", "none")];
    public static readonly ThemeChoice[] Themes = [
        new("white", "柔白", "#FCFCFA", "#293540"),
        new("warm", "米色", "#F4ECD8", "#3D382F"),
        new("green", "护眼绿", "#DDEBD8", "#283A2D"),
        new("blue", "雾蓝", "#E6EDF3", "#293C4C"),
        new("night", "夜读", "#23292F", "#E4E1D8"),
        new("paper", "纸张质感", "#F1E6CF", "#443C30", true)
    ];
    [JsonIgnore] public ThemeChoice Palette => Themes.FirstOrDefault(x => x.Key == Theme) ?? Themes[0];
    public ReadingAppearance Normalize() => new(
        Fonts.Any(x => x.Family == FontFamily) ? FontFamily : "SimSun",
        Palette.Key, Widths.Any(x => x.Width == Width) ? Width : 790,
        PageLayout == "double" ? "double" : "single", Effects.Any(x => x.Key == TurnEffect) ? TurnEffect : "curl", SidebarVisible);
}
