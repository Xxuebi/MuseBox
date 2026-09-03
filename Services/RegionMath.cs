using DrawingRectangle = System.Drawing.Rectangle;
using System.Windows;

namespace ScreenshotCollector.Services;

public static class RegionMath
{
    public static Rect Normalize(System.Windows.Point start, System.Windows.Point end, double width, double height)
    {
        var left = Math.Clamp(Math.Min(start.X, end.X), 0, width);
        var top = Math.Clamp(Math.Min(start.Y, end.Y), 0, height);
        var right = Math.Clamp(Math.Max(start.X, end.X), 0, width);
        var bottom = Math.Clamp(Math.Max(start.Y, end.Y), 0, height);

        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static DrawingRectangle ToPixelRectangle(
        Rect selection,
        double surfaceWidth,
        double surfaceHeight,
        int bitmapWidth,
        int bitmapHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || bitmapWidth <= 0 || bitmapHeight <= 0)
        {
            return DrawingRectangle.Empty;
        }

        var scaleX = bitmapWidth / surfaceWidth;
        var scaleY = bitmapHeight / surfaceHeight;

        var left = Math.Clamp((int)Math.Floor(selection.Left * scaleX), 0, bitmapWidth);
        var top = Math.Clamp((int)Math.Floor(selection.Top * scaleY), 0, bitmapHeight);
        var right = Math.Clamp((int)Math.Ceiling(selection.Right * scaleX), 0, bitmapWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(selection.Bottom * scaleY), 0, bitmapHeight);

        return DrawingRectangle.FromLTRB(left, top, right, bottom);
    }
}
