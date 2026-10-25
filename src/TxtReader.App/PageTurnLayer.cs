using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace TxtReader.App;

/// <summary>Disposable imagery and motion; never owns committed reading positions.</summary>
internal sealed class PageTurnLayer : Grid
{
    private EventHandler? _rendering;
    private Action<bool>? _finish;
    private Action<Point>? _frame;
    private Point _pointer, _rest, _landing;
    private bool _dirty, _interactive;
    internal int Frames { get; private set; }
    internal double MaxFrameMs { get; private set; }
    internal Point? ProjectedCorner { get; private set; }
    internal double CurlRadius { get; private set; }
    internal bool IsRunning => _interactive || _finish is not null;
    internal void ResetFrames() { Frames = 0; MaxFrameMs = 0; }

    public PageTurnLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;
        Unloaded += (_, _) => Cancel();
    }

    internal static BitmapSource Capture(FrameworkElement view)
    {
        double scale = Math.Min(VisualTreeHelper.GetDpi(view).DpiScaleX, 2048 / Math.Max(1, view.ActualWidth));
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(view.ActualWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(view.ActualHeight * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        bitmap.Freeze();
        return bitmap;
    }

    internal void Hold(BitmapSource before)
    {
        Cancel();
        Children.Add(new Image { Source = before, Stretch = Stretch.Fill });
        Visibility = Visibility.Visible;
        UpdateLayout();
    }

    private void StopRendering()
    {
        if (_rendering is not null) CompositionTarget.Rendering -= _rendering;
        _rendering = null;
    }

    internal void Cancel()
    {
        _finish?.Invoke(false);
        StopRendering();
        _frame = null;
        _interactive = _dirty = false;
        Children.Clear();
        Visibility = Visibility.Collapsed;
    }

    internal void BeginDrag(BitmapSource before, BitmapSource after, string effect, int direction, bool spread, bool top, Point press)
    {
        Cancel();
        ResetFrames();
        Visibility = Visibility.Visible;
        UpdateLayout();
        double width = ActualWidth, height = ActualHeight;
        _rest = _pointer = press;
        double landingTravel = effect == "curl" ? (spread ? width : width * 2) : PageDragMath.Travel(width, spread ? 2 : 1);
        _landing = press + new Vector(-direction * landingTravel, 0);
        ProjectedCorner = null;
        CurlRadius = 0;
        if (effect == "curl") _frame = CreateCurl(before, after, width, height, direction, spread, top, press);
        else
        {
            Children.Add(new Image { Source = after, Stretch = Stretch.Fill });
            var old = new Image { Source = before, Stretch = Stretch.Fill };
            var transform = new TranslateTransform();
            old.RenderTransform = transform;
            Children.Add(old);
            _frame = point =>
            {
                double p = PageDragMath.Progress(press, point, PageDragMath.Travel(width, spread ? 2 : 1), direction);
                if (effect == "slide") transform.X = -direction * width * p;
                else old.Opacity = 1 - p * (effect == "none" ? .35 : 1);
            };
        }
        _interactive = true;
        Draw(_pointer);
        // Coalesce mouse moves: consume the most recent pointer once per rendered frame.
        _rendering = (_, _) => { if (_dirty) { _dirty = false; Draw(_pointer); } };
        CompositionTarget.Rendering += _rendering;
    }

    internal void UpdateDrag(Point pointer) { _pointer = pointer; _dirty = true; }

    private void Draw(Point point)
    {
        if (_frame is null) return;
        long start = Stopwatch.GetTimestamp();
        _frame(point);
        MaxFrameMs = Math.Max(MaxFrameMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        Frames++;
    }

    internal Task<bool> SettleDragAsync(bool complete, bool animate)
    {
        StopRendering();
        Draw(_pointer);
        double span = Math.Max(1, (_landing - _rest).Length);
        Point to = complete ? _landing : _rest;
        double duration = 180 + 140 * Math.Clamp((to - _pointer).Length / span, 0, 1);
        return AnimateAsync(_pointer, to, animate ? duration : 0);
    }

    private async Task<bool> AnimateAsync(Point from, Point to, double duration)
    {
        StopRendering();
        if (duration <= 0) { Draw(to); return true; }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        Action<bool> finish = result =>
        {
            StopRendering();
            _finish = null;
            completion.TrySetResult(result);
        };
        _finish = finish;
        _rendering = (_, _) =>
        {
            double t = Math.Clamp(watch.Elapsed.TotalMilliseconds / duration, 0, 1);
            Draw(from + (to - from) * (.5 - .5 * Math.Cos(t * Math.PI)));
            if (t >= 1) finish(true);
        };
        CompositionTarget.Rendering += _rendering;
        await Task.WhenAny(completion.Task, Task.Delay(2000));
        // Hidden windows can stop rendering. Never leave a pending commit behind.
        if (!completion.Task.IsCompleted) finish(false);
        return await completion.Task;
    }

    internal async Task PlayAsync(BitmapSource before, BitmapSource after, string effect, int direction, bool spread)
    {
        if (effect == "none") { Cancel(); return; }
        var press = new Point(direction > 0 ? ActualWidth : 0, ActualHeight);
        BeginDrag(before, after, effect, direction, spread, false, press);
        await AnimateAsync(_rest, _landing, effect == "curl" ? 580 : effect == "slide" ? 230 : 180);
        Cancel();
    }

    private Action<Point> CreateCurl(BitmapSource before, BitmapSource after, double width, double height,
        int direction, bool spread, bool top, Point press)
    {
        bool forward = direction > 0;
        double pageWidth = spread ? width / 2 : width;
        BitmapSource Portion(BitmapSource source, bool right)
        {
            if (!spread) return source;
            int half = source.PixelWidth / 2;
            var cropped = new CroppedBitmap(source, new Int32Rect(right ? half : 0, 0,
                right ? source.PixelWidth - half : half, source.PixelHeight));
            cropped.Freeze();
            return cropped;
        }
        if (spread)
        {
            var stationary = new Grid();
            stationary.ColumnDefinitions.Add(new()); stationary.ColumnDefinitions.Add(new());
            var left = new Image { Source = Portion(forward ? before : after, false), Stretch = Stretch.Fill };
            var right = new Image { Source = Portion(forward ? after : before, true), Stretch = Stretch.Fill };
            Grid.SetColumn(right, 1);
            stationary.Children.Add(left); stationary.Children.Add(right);
            Children.Add(stationary);
        }
        else Children.Add(new Image { Source = after, Stretch = Stretch.Fill });
        var shadow = new Path { Fill = Brushes.Black, Opacity = 0,
            Effect = new BlurEffect { Radius = 12, RenderingBias = RenderingBias.Performance } };
        Children.Add(shadow);
        var viewport = new Viewport3D
        {
            ClipToBounds = false,
            Camera = new OrthographicCamera(new Point3D(0, 0, width * 4), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), width)
        };
        var mesh = new MeshGeometry3D();
        const int columns = 64, rows = 40;
        var texture = new PointCollection((columns + 1) * (rows + 1));
        var triangles = new Int32Collection(columns * rows * 6);
        for (int row = 0; row <= rows; row++)
        for (int col = 0; col <= columns; col++)
            texture.Add(new Point(forward ? col / (double)columns : 1 - col / (double)columns, row / (double)rows));
        for (int row = 0; row < rows; row++)
        for (int col = 0; col < columns; col++)
        {
            int a = row * (columns + 1) + col, b = a + columns + 1;
            foreach (int index in forward ? new[] { a, b, a + 1, a + 1, b, b + 1 } : new[] { a, a + 1, b, a + 1, b + 1, b }) triangles.Add(index);
        }
        texture.Freeze(); triangles.Freeze();
        mesh.TextureCoordinates = texture; mesh.TriangleIndices = triangles;
        Material MaterialFor(BitmapSource image, bool mirrored)
        {
            var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
            if (mirrored) brush.RelativeTransform = new ScaleTransform(-1, 1, .5, .5);
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            return material;
        }
        var model = new GeometryModel3D(mesh, MaterialFor(Portion(before, forward), false))
            { BackMaterial = MaterialFor(Portion(after, !forward), true) };
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(222, 222, 222)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(28, 28, 28), new Vector3D(-.4, .2, -1)));
        scene.Children.Add(model);
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        Children.Add(viewport);
        var outline = new List<int>();
        for (int col = 0; col <= columns; col++) outline.Add(col);
        for (int row = 1; row <= rows; row++) outline.Add(row * (columns + 1) + columns);
        for (int col = columns - 1; col >= 0; col--) outline.Add(rows * (columns + 1) + col);
        for (int row = rows - 1; row > 0; row--) outline.Add(row * (columns + 1));
        double sign = direction, hinge = spread ? 0 : -sign * width / 2;
        return point =>
        {
            // A body press pulls the outer edge at the same height. Preserve the
            // grip offset so the page does not jump to a corner on first movement.
            // Moving back beyond the press point must flatten, not turn the other way.
            double horizontal = Math.Min(0, direction * (point.X - press.X));
            double gripY = Math.Clamp(press.Y, 0, height);
            var target = new Point(pageWidth + horizontal, gripY + (horizontal < 0 ? point.Y - press.Y : 0));
            var curl = new PaperCurl(pageWidth, height, gripY, target);
            CurlRadius = curl.Radius;
            ProjectedCorner = new Point(hinge + sign * curl.Target.X + width / 2, curl.Target.Y);
            var positions = new Point3DCollection((columns + 1) * (rows + 1));
            for (int row = 0; row <= rows; row++)
            for (int col = 0; col <= columns; col++)
            {
                var p = curl.Map(col * pageWidth / columns, row * height / rows);
                positions.Add(new Point3D(hinge + sign * p.X, height / 2 - p.Y, p.Z));
            }
            mesh.Positions = positions;
            var cast = new StreamGeometry();
            using (var path = cast.Open())
            {
                bool first = true;
                foreach (int index in outline)
                {
                    var p = positions[index];
                    var projected = new Point(p.X + width / 2 + p.Z * .10, height / 2 - p.Y + p.Z * .06);
                    if (first) { path.BeginFigure(projected, true, true); first = false; }
                    else path.LineTo(projected, true, false);
                }
            }
            shadow.Data = cast;
            shadow.Opacity = .20 * Math.Min(1, curl.Radius / Math.Max(1, pageWidth * .04));
        };
    }
}
