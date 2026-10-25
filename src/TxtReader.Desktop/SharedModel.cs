global using TxtReader.Core;
using System.Text.Json.Serialization;

namespace TxtReader.App;

internal static class LibraryLocation
{
    internal static string PrepareDefault() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Qidu")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QiduDesktopPreview");
}

public sealed record ReadingAppearance(string FontFamily = "Songti SC", string Theme = "white", int Width = 790,
    string PageLayout = "single", string TurnEffect = "curl", bool SidebarVisible = true)
{
    public static readonly string[] FontFamilies = OperatingSystem.IsMacOS()
        ? ["Songti SC", "PingFang SC", "Kaiti SC", "Heiti SC"] : ["SimSun", "Microsoft YaHei", "KaiTi", "FangSong"];
    public static readonly (string Key, string Name, string Background, string Foreground)[] Themes = [
        ("white", "柔白", "#FCFCFA", "#293540"), ("warm", "米色", "#F4ECD8", "#3D382F"),
        ("green", "护眼绿", "#DDEBD8", "#283A2D"), ("blue", "雾蓝", "#E6EDF3", "#293C4C"),
        ("night", "夜读", "#23292F", "#E4E1D8"), ("paper", "纸张质感", "#F1E6CF", "#443C30")];
    [JsonIgnore] public string Background => Themes.FirstOrDefault(x => x.Key == Theme).Background ?? Themes[0].Background;
    [JsonIgnore] public string Foreground => Themes.FirstOrDefault(x => x.Key == Theme).Foreground ?? Themes[0].Foreground;
    public ReadingAppearance Normalize() => this with
    {
        FontFamily = FontFamilies.Contains(FontFamily) ? FontFamily : FontFamilies[0],
        Theme = Themes.Any(x => x.Key == Theme) ? Theme : "white",
        Width = new[] { 0, 620, 790, 1000 }.Contains(Width) ? Width : 790,
        PageLayout = PageLayout == "double" ? "double" : "single",
        TurnEffect = new[] { "curl", "slide", "fade", "none" }.Contains(TurnEffect) ? TurnEffect : "curl"
    };
}
