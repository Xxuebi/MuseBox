using System.Windows;

namespace ScreenshotCollector.Services;

internal static class ArrowGeometry
{
    // Include diagonal arrow arms and rounded caps, even on short, thick strokes.
    internal static double Padding(double thickness) => Math.Max(14, Math.Max(.5, thickness) * 4.1);

    internal static Point[] Head(IReadOnlyList<Point> points, double thickness)
    {
        if (points.Count < 2) return Array.Empty<Point>();
        var tip = points[^1];
        var behind = tip;
        var travelled = 0d;
        var directionDistance = Math.Max(6, thickness * 1.5);
        for (var i = points.Count - 2; i >= 0; i--)
        {
            var length = (points[i] - behind).Length;
            if (travelled + length >= directionDistance && length > .001)
            {
                behind += (points[i] - behind) * ((directionDistance - travelled) / length);
                break;
            }
            travelled += length;
            behind = points[i];
        }
        var tangent = behind - tip;
        if (tangent.Length < .1) return Array.Empty<Point>();
        tangent.Normalize();
        var normal = new Vector(-tangent.Y, tangent.X);
        var headLength = Math.Max(10, thickness * 3.2);
        return new[] { tip + tangent * headLength + normal * headLength * .48, tip,
            tip + tangent * headLength - normal * headLength * .48 };
    }
}
