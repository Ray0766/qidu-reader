using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TxtReader.App;

internal sealed class AppearanceDialog : Window
{
    public AppearanceDialog(Window owner, ReadingAppearance current, Func<ReadingAppearance, Task> changed)
    {
        Owner = owner; Title = "阅读排版"; Width = 480;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 60);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(244, 246, 242));
        var panel = new StackPanel { Margin = new Thickness(24) };
        var appearance = current;
        var selectedBrush = new SolidColorBrush(Color.FromRgb(220, 229, 215));
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        void Choices<T>(IEnumerable<T> items, int columns, Func<T, string> name, Func<T, bool> selected,
            Func<T, ReadingAppearance> update, string id, Func<T, string>? font = null)
        {
            var grid = new UniformGrid { Columns = columns, Margin = new Thickness(-3, 0, -3, 17) };
            var buttons = new List<(Button Button, T Item)>();
            foreach (var item in items)
            {
                var button = new Button
                {
                    Content = name(item), Padding = new Thickness(5), Height = 36, Margin = new Thickness(3),
                    FontSize = 12, Background = selected(item) ? selectedBrush : Brushes.White
                };
                if (font is not null) button.FontFamily = new FontFamily(font(item));
                System.Windows.Automation.AutomationProperties.SetAutomationId(button, id + "_" + name(item));
                buttons.Add((button, item));
                button.Click += async (_, _) =>
                {
                    appearance = update(item);
                    foreach (var option in buttons) option.Button.Background = selected(option.Item) ? selectedBrush : Brushes.White;
                    await changed(appearance);
                };
                grid.Children.Add(button);
            }
            panel.Children.Add(grid);
        }
        Label("正文字体");
        Choices(ReadingAppearance.Fonts, 3, x => x.Name, x => x.Family == appearance.FontFamily,
            x => appearance with { FontFamily = x.Family }, "Font", x => x.Family);
        Label("阅读背景");
        var themes = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 0, -3, 17) };
        var themeButtons = new List<(Button Button, ThemeChoice Theme)>();
        foreach (var theme in ReadingAppearance.Themes)
        {
            var button = new Button
            {
                Content = (theme.Key == current.Theme ? "✓ " : "") + theme.Name,
                Background = theme.MakeBackground(), Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Foreground)),
                Margin = new Thickness(3), Height = 42, Padding = new Thickness(4), FontSize = 12
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Theme_" + theme.Key);
            themeButtons.Add((button, theme));
            button.Click += async (_, _) =>
            {
                appearance = appearance with { Theme = theme.Key };
                foreach (var item in themeButtons) item.Button.Content = (item.Theme.Key == theme.Key ? "✓ " : "") + item.Theme.Name;
                await changed(appearance);
            };
            themes.Children.Add(button);
        }
        panel.Children.Add(themes);
        Label("正文宽度");
        Choices(ReadingAppearance.Widths, 4, x => x.Name, x => x.Width == appearance.Width,
            x => appearance with { Width = x.Width }, "Width");
        Label("翻页动效");
        Choices(ReadingAppearance.Effects, 2, x => x.Name, x => x.Key == appearance.TurnEffect,
            x => appearance with { TurnEffect = x.Key }, "Effect");
        panel.Children.Add(new TextBlock
        {
            Text = "字体、背景和宽度适用于所有书籍。\n单页和双页均按章起页；系统关闭动画时不播放动效。",
            FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(108, 120, 108)), LineHeight = 21
        });
        var close = new Button
        {
            Content = "完成", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(53, 74, 64)), Foreground = Brushes.White,
            Margin = new Thickness(0, 18, 0, 0)
        };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
