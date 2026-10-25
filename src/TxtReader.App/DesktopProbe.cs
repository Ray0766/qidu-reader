using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TxtReader.App;

/// <summary>Explicit, isolated desktop QA mode. Normal reading never installs these hooks.</summary>
internal static class DesktopProbe
{
    internal sealed record DpiEvidence(double WpfScale, uint NativeDpi, double? ExpectedScale, int ProcessId,
        double WindowWidth, double WindowHeight, double WorkWidth, double WorkHeight);
    private sealed record Baseline(string BookId, int Anchor, string Excerpt, double FontSize, string Layout,
        string Action, int ProcessId, DpiEvidence Dpi);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    internal static string? Folder(string[] args) => Option(args, "--desktop-probe") ?? Option(args, "--desktop-resume");
    internal static DpiEvidence Dpi(Window window)
    {
        string? expected = Option(Environment.GetCommandLineArgs(), "--expect-dpi");
        return new(VisualTreeHelper.GetDpi(window).DpiScaleX, GetDpiForWindow(new WindowInteropHelper(window).Handle),
            expected is null ? null : double.Parse(expected, CultureInfo.InvariantCulture), Environment.ProcessId,
            window.ActualWidth, window.ActualHeight, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
    }
    private static void Write(string folder, string file, object value) =>
        File.WriteAllText(Path.Combine(folder, file), JsonSerializer.Serialize(value, JsonOptions));
    private static void Capture(ReaderSurface reader, string folder, string file)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(PageTurnLayer.Capture(reader)));
        using var output = File.Create(Path.Combine(folder, file));
        encoder.Save(output);
    }
    private static uint ForegroundProcess()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid;
    }

    internal static bool StartFocusReceiver(string[] args)
    {
        if (Option(args, "--focus-receiver") is not { } folder) return false;
        // This companion owns its own window. It receives no arbitrary commands or user files.
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var target = new Window { Title = "TXT Reader · 切换应用验证窗口", Width = 550, Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock { Text = "正在验证切换应用后取消拖页。\n测试结束后此窗口会自动关闭。", Margin = new Thickness(30), FontSize = 18 } };
        Application.Current.MainWindow = target;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        bool shown = false;
        timer.Tick += (_, _) =>
        {
            if (File.Exists(Path.Combine(folder, "focus-stop.signal"))) { timer.Stop(); target.Close(); Application.Current.Shutdown(); return; }
            if (!shown && File.Exists(Path.Combine(folder, "focus-activate.signal")))
            {
                shown = true;
                target.Show(); target.Activate();
                Write(folder, "focus-shown.json", new { pid = Environment.ProcessId, active = target.IsActive, foreground = ForegroundProcess() });
            }
        };
        // Allocate the HWND, layout and render resources before the timed interruption.
        // A cold Show() can otherwise arrive after the 230 ms landing has already committed.
        target.ShowActivated = false;
        target.Show();
        target.Dispatcher.InvokeAsync(async () =>
        {
            target.UpdateLayout();
            await Task.Delay(100);
            target.Hide();
            target.ShowActivated = true;
            timer.Start();
            Write(folder, "focus-ready.json", new { pid = Environment.ProcessId });
        }, DispatcherPriority.ContextIdle);
        return true;
    }

    internal static async Task RunAsync(MainWindow window, string[] args)
    {
        string folder = Path.GetFullPath(Folder(args)!);
        if (!string.Equals(Path.GetFullPath(window.Store.Folder), Path.Combine(folder, "state"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Desktop QA requires --data-dir <report folder>\\state; normal reader data is forbidden.");
        Directory.CreateDirectory(folder);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var reader = window.ReadingSurface;
        var initialDpi = Dpi(window);
        var checks = new List<object>();
        var events = new List<object>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool finished = false;
        var clock = Stopwatch.StartNew();
        void Check(bool ok, string name, object? detail = null)
        {
            checks.Add(new { passed = ok, name, detail });
            Write(folder, "in-progress.json", checks);
            if (!ok) throw new InvalidOperationException(name);
        }
        void Event(string name)
        {
            events.Add(new { name, ms = clock.Elapsed.TotalMilliseconds, reader.Anchor, reader.CornerDragActive,
                capture = reader.IsMouseCaptureWithin, pressed = Mouse.LeftButton == MouseButtonState.Pressed,
                active = window.IsActive, state = window.WindowState.ToString(), foreground = ForegroundProcess() });
            Write(folder, args.Contains("--desktop-resume") ? "resume-events.json" : "events.json", events);
        }
        window.Deactivated += (_, _) => Event("Deactivated");
        window.StateChanged += (_, _) => Event("StateChanged");
        window.Closing += (_, _) => Event("Closing");
        window.Closed += (_, _) => { Event("Closed"); closed.TrySetResult(); };
        async Task FinishAsync(Exception? error = null)
        {
            if (finished) return;
            finished = true;
            File.WriteAllText(Path.Combine(folder, "focus-stop.signal"), "stop");
            Write(folder, args.Contains("--desktop-resume") ? "resume-report.json" : "report.json",
                new { passed = error is null, processId = Environment.ProcessId, dpi = initialDpi, checks, events, error = error?.ToString() });
            if (!closed.Task.IsCompleted) { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            Application.Current.Shutdown(error is null ? 0 : 1);
        }
        try
        {
            var dpi = Dpi(window);
            Check(dpi.ExpectedScale is not null && Math.Abs(dpi.WpfScale - dpi.ExpectedScale.Value) < .01 &&
                Math.Abs(dpi.NativeDpi / 96d - dpi.ExpectedScale.Value) < .01, "real HWND and WPF DPI match requested scaling", dpi);
            if (args.Contains("--desktop-resume"))
            {
                var baseline = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(Path.Combine(folder, "baseline.json")))!;
                Check(baseline.ProcessId != Environment.ProcessId, "resume is a fresh process");
                Check(reader.Book?.ContentId == baseline.BookId && reader.Anchor == baseline.Anchor && reader.Snapshot().AnchorVisible &&
                    reader.Book.Excerpt(reader.Anchor) == baseline.Excerpt && reader.ReadingFontSize == baseline.FontSize,
                    "fresh process restores exact source position, text and font", reader.Snapshot());
                Check(window.Appearance.PageLayout == baseline.Layout, "resume restores layout");
                Capture(reader, folder, "reopened.png");
                await FinishAsync();
                return;
            }
            string action = Option(args, "--probe-action") ?? throw new ArgumentException("Missing probe action.");
            Check(action is "deactivate" or "minimize" or "close", "known interruption action", action);
            string layout = Option(args, "--probe-layout") ?? "double";
            string phase = Option(args, "--probe-phase") ?? "mouse";
            Check(phase is "mouse" or "prepared" or "settling", "known drag phase", phase);
            await window.SetReadingViewAsync(ReadingMode.Page, layout);
            await window.ApplyAppearanceAsync(new("KaiTi", "paper", 0, layout, "curl", false));
            await reader.SetFontAsync(26);
            int anchor = reader.Book!.ClampOffset((int)(reader.Book.Text.Length * .61));
            await reader.NavigateAsync(anchor);
            await reader.FlushPendingInputAsync();
            window.SavePosition(true);
            var before = new Baseline(reader.Book.ContentId, anchor, reader.Book.Excerpt(anchor), reader.ReadingFontSize,
                layout, action, Environment.ProcessId, Dpi(window));
            Write(folder, "baseline.json", before);
            Capture(reader, folder, "before.png");
            ProcessStartInfo FocusProcess()
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--focus-receiver"); start.ArgumentList.Add(folder);
                return start;
            }
            if (action == "deactivate" && phase != "mouse")
            {
                var start = FocusProcess();
                Process.Start(start);
                for (int i = 0; i < 100 && !File.Exists(Path.Combine(folder, "focus-ready.json")); i++) await Task.Delay(20);
                Check(File.Exists(Path.Combine(folder, "focus-ready.json")), "separate focus process is ready");
            }
            window.Activate();
            window.Title = $"[QA {action} · {dpi.WpfScale:P0}] 请拖动页角 — TXT Reader";
            Write(folder, "ready.json", new { action, layout, dpi = Dpi(window), before.Anchor,
                pageBounds = new Rect(reader.TranslatePoint(new Point(), window), reader.RenderSize) });
            bool triggered = false;
            var inputSamples = new List<object>();
            Task settlement = Task.CompletedTask;
            Window? focusWindow = null;
            async Task InterruptAsync(bool nativeInput)
            {
                triggered = true;
                try
                {
                    if (nativeInput) Check(reader.IsMouseCaptureWithin && window.IsActive && Mouse.LeftButton == MouseButtonState.Pressed,
                        "interruption begins with actual mouse held and captured");
                    else Check(reader.CornerDragActive && reader.DragPreviewReady && reader.DragProgress >= .5,
                        "programmatic gesture reaches requested asynchronous phase");
                    if (action == "deactivate") Check(window.IsActive && ForegroundProcess() == Environment.ProcessId,
                        "reader owns foreground before focus interruption");
                    Write(folder, "trigger.json", new { phase, nativeInput, button = Mouse.LeftButton.ToString(), capture = reader.IsMouseCaptureWithin,
                        reader.CornerDragActive, reader.DragPreviewReady, reader.DragProgress, reader.Anchor, dpi = Dpi(window), foreground = ForegroundProcess() });
                    Write(folder, "mouse-input.json", inputSamples);
                    // Exercise real window lifecycle, never call the cancellation method directly.
                    if (action == "close") window.Close();
                    else if (action == "minimize") window.WindowState = WindowState.Minimized;
                    else if (nativeInput)
                    {
                        // Synchronous window activation observes a genuinely held mouse.
                        // Separate-process switching is covered by prepared/settling cases;
                        // process startup can outlast a short native input stroke.
                        focusWindow = new Window { Title = "TXT Reader · 拖动中窗口失焦验证", Width = 440, Height = 170,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen,
                            Content = new TextBlock { Text = "正在验证鼠标按住时窗口失焦。", Margin = new Thickness(24), FontSize = 18 } };
                        focusWindow.Show(); focusWindow.Activate();
                    }
                    else File.WriteAllText(Path.Combine(folder, "focus-activate.signal"), "activate");
                    if (action == "close") await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(2400); // Longer than settlement timeout and the normal autosave interval.
                    await settlement.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!reader.CornerDragActive && !reader.AnimationLayerVisible && reader.Anchor == before.Anchor,
                        "window interruption cancels preview and cannot commit late", new { reader.Anchor, reader.CornerDragActive, reader.AnimationLayerVisible });
                    if (action == "minimize") Check(window.WindowState == WindowState.Minimized, "window really minimized");
                    if (action == "deactivate")
                    {
                        if (nativeInput) Check(!window.IsActive && focusWindow!.IsActive, "held-mouse focus moved to another real window");
                        else Check(!window.IsActive && ForegroundProcess() != Environment.ProcessId && File.Exists(Path.Combine(folder, "focus-shown.json")),
                            "foreground really moved to another process", ForegroundProcess());
                    }
                    using (var saved = new SessionStore(window.Store.Folder))
                        Check(saved.Read(before.BookId)?.Offset == before.Anchor, "SQLite retains committed position after interruption");
                    if (action != "close")
                    {
                        File.WriteAllText(Path.Combine(folder, "focus-stop.signal"), "stop");
                        focusWindow?.Close();
                        window.WindowState = WindowState.Normal; window.Activate();
                        await reader.RefreshLayoutAsync();
                        Check(reader.Snapshot().AnchorVisible && reader.Anchor == before.Anchor && reader.PageViewsReady,
                            "restored window shows original text", reader.Snapshot());
                        Capture(reader, folder, "restored.png");
                        await window.SetReadingViewAsync(ReadingMode.Scroll);
                        await reader.SetFontAsync(30);
                        Check(reader.Anchor == before.Anchor && reader.Snapshot().AnchorVisible, "scroll and larger font preserve interrupted position");
                        await window.SetReadingViewAsync(ReadingMode.Page, before.Layout);
                        await reader.SetFontAsync(before.FontSize);
                        Check(reader.Anchor == before.Anchor && reader.Snapshot().AnchorVisible, "page and original font preserve interrupted position");
                    }
                    await FinishAsync();
                }
                catch (Exception ex) { focusWindow?.Close(); await FinishAsync(ex); }
            }
            async void OnDragMove(object sender, MouseEventArgs input)
            {
                if (!reader.CornerDragActive || triggered) return;
                inputSamples.Add(new { ms = clock.Elapsed.TotalMilliseconds, reader.DragPreviewReady, reader.DragProgress,
                    pressed = input.LeftButton == MouseButtonState.Pressed, capture = reader.IsMouseCaptureWithin });
                Write(folder, "mouse-input.json", inputSamples);
                if (reader.DragProgress >= .5 && input.LeftButton == MouseButtonState.Pressed) await InterruptAsync(true);
            }
            // The production handler marks the tunneling event handled. Observe that same
            // element after its handler; WPF can omit the corresponding bubbling event.
            if (phase == "mouse") reader.Children[0].AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnDragMove), true);
            else
            {
                await Task.Delay(300);
                Point start = new(reader.ActualWidth * .65, reader.ActualHeight * .45);
                Check(reader.BeginPagePointer(start), "programmatic gesture begins inside body text");
                Point pull = start + new Vector(-reader.DragTravel * .75, 25);
                reader.MovePagePointer(pull);
                await reader.WaitForDragPreviewAsync();
                Capture(reader, folder, "prepared.png");
                if (phase == "settling") settlement = reader.EndPagePointerAsync(pull);
                await InterruptAsync(false);
                return;
            }
            _ = Task.Delay(TimeSpan.FromMinutes(4)).ContinueWith(_ => window.Dispatcher.InvokeAsync(async () =>
            {
                if (!finished) { Write(folder, "mouse-input.json", inputSamples); await FinishAsync(new TimeoutException("No real held mouse drag reached the interruption threshold.")); }
            }));
        }
        catch (Exception ex) { await FinishAsync(ex); }
    }
}
