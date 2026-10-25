using System.IO;
using System.Windows;

namespace TxtReader.App;

public partial class App : Application
{
    // Installer checks existence only. Keep the handle alive through process exit,
    // so upgrading/uninstalling cannot replace files while reading data is saving.
    private Mutex? _installerMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (DesktopProbe.StartFocusReceiver(e.Args)) return;
        if (!e.Args.Contains("--self-test") && !e.Args.Contains("--data-dir") && !e.Args.Contains("--package-probe") && DesktopProbe.Folder(e.Args) is null)
        {
            _installerMutex = new Mutex(false, @"Local\TXTReader.Desktop.Running", out bool firstWindow);
            if (!firstWindow)
            {
                MessageBox.Show("请先关闭已打开的 TXT Reader 或栖读窗口，再启动此版本。\n这能避免两个版本同时保存不同的阅读进度。", "栖读");
                Shutdown();
                return;
            }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            if (PackageProbe.ReportStartupFailure(e.Args, args.Exception))
            { args.Handled = true; Shutdown(1); return; }
            if (DesktopProbe.Folder(e.Args) is { } probeFolder)
            {
                Directory.CreateDirectory(probeFolder);
                File.WriteAllText(Path.Combine(probeFolder, "unhandled.txt"), args.Exception.ToString());
                args.Handled = true;
                Shutdown(1);
                return;
            }
            int testIndex = Array.IndexOf(e.Args, "--self-test");
            if (testIndex >= 0)
            {
                Directory.CreateDirectory(e.Args[testIndex + 1]);
                File.WriteAllText(Path.Combine(e.Args[testIndex + 1], "unhandled.txt"), args.Exception.ToString());
                args.Handled = true;
                Shutdown(1);
                return;
            }
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxtReaderPreview");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "errors.log"), $"{DateTimeOffset.Now:O}\n{args.Exception}\n");
            MessageBox.Show("操作未完成，错误信息已保存。\n" + args.Exception.Message, "栖读");
            args.Handled = true;
        };
        try
        {
            var window = new MainWindow(e.Args);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            if (PackageProbe.ReportStartupFailure(e.Args, error)) { Shutdown(1); return; }
            MessageBox.Show("无法打开书库。\n" + error.Message, "栖读", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }
}
