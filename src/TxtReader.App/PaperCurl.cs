using System.Windows;
using System.Windows.Media.Media3D;

namespace TxtReader.App;

/// <summary>A cylindrical bend in leaf coordinates: spine x=0, outer edge x=width.</summary>
internal sealed class PaperCurl
{
    private readonly Vector _normal;
    private readonly Point _fold;
    private readonly double _radius;
    public Point Target { get; }
    public double Radius => _radius;

    public PaperCurl(double width, double height, bool top, Point target)
        : this(width, height, top ? 0 : height, target) { }

    public PaperCurl(double width, double height, double gripY, Point target)
    {
        var corner = new Point(width, RuntimeCompat.Clamp(gripY, 0, height));
        var nearHinge = new Point(0, 0);
        var farHinge = new Point(0, height);
        for (int i = 0; i < 8; i++)
        {
            target = Constrain(target, nearHinge, (corner - nearHinge).Length);
            target = Constrain(target, farHinge, (corner - farHinge).Length);
        }
        Target = target;
        var delta = corner - target;
        double length = delta.Length;
        if (length < .00001) { _normal = new Vector(1, 0); _fold = corner; return; }
        _normal = delta / length;
        var middle = new Point((corner.X + target.X) / 2, (corner.Y + target.Y) / 2);
        double clearance = Math.Min(Vector.Multiply(middle - nearHinge, _normal), Vector.Multiply(middle - farHinge, _normal));
        _radius = Math.Min(Math.Min(width * .075, length * .15), Math.Max(0, clearance) * 2 / Math.PI);
        // Compensate for the rounded fold so the grip projects exactly to Target.
        _fold = middle - _normal * (_radius * Math.PI / 2);
    }

    private static Point Constrain(Point point, Point center, double radius)
    {
        var v = point - center;
        return v.Length > radius ? center + v * (radius / v.Length) : point;
    }

    public Point3D Map(double x, double y)
    {
        var point = new Point(x, y);
        double d = Vector.Multiply(point - _fold, _normal);
        if (d <= 0) return new(x, y, 0);
        double along, z;
        if (_radius < .00001) { along = -d; z = 0; }
        else if (d < Math.PI * _radius)
        {
            double angle = d / _radius;
            along = _radius * Math.Sin(angle);
            z = _radius * (1 - Math.Cos(angle));
        }
        else { along = Math.PI * _radius - d; z = 2 * _radius; }
        point += _normal * (along - d);
        return new(point.X, point.Y, z);
    }
}

internal static class PageDragMath
{
    internal static (int Direction, bool Top)? Corner(Point point, Size size, int pages)
    {
        if (point.X < 0 || point.Y < 0 || point.X > size.Width || point.Y > size.Height) return null;
        double x = Math.Min(48, size.Width / pages * .15), y = Math.Min(48, size.Height * .15);
        if (point.Y > y && point.Y < size.Height - y) return null;
        if (point.X <= x) return (-1, point.Y <= y);
        if (point.X >= size.Width - x) return (1, point.Y <= y);
        return null;
    }

    // Same distance at every press location, measured in DIPs. Wide monitors must
    // not require dragging the pointer across the entire screen to turn one page.
    internal static double Travel(double width, int pages) => 2 * RuntimeCompat.Clamp(width / pages * .35, 60, 180);

    internal static double Progress(Point start, Point current, double travel, int direction) =>
        RuntimeCompat.Clamp(direction * (start.X - current.X) / Math.Max(1, travel), 0, 1);
}
