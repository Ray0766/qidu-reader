using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Qidu.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try { desktop.MainWindow = new MainWindow(); }
            catch (IOException)
            {
                desktop.MainWindow = new Window { Title = "栖读", Width = 420, Height = 160, CanResize = false,
                    Content = new TextBlock { Text = "书库正在使用中，或当前无法写入。请先关闭已打开的栖读窗口，并确认书库文件夹可以访问。", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(24) } };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
