using System.Windows;
using System.Windows.Media;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Controls;

public sealed class BoardGridVisual : FrameworkElement
{
    private const double MinimumScreenStep = 12;
    private const double MaximumScreenStep = 24;
    private const int MajorLineInterval = 5;

    private BoardGridStyle _style;
    private double _spacing = 32;
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private Color _background = Color.FromRgb(122, 122, 122);

    public void SetViewport(BoardGridStyle style, double spacing, double zoom,
        double panX, double panY, Color background)
    {
        _style = Enum.IsDefined(style) ? style : BoardGridStyle.None;
        _spacing = double.IsFinite(spacing) && spacing > 0 ? spacing : 32;
        _zoom = double.IsFinite(zoom) && zoom > 0 ? zoom : 1;
        _panX = double.IsFinite(panX) ? panX : 0;
        _panY = double.IsFinite(panY) ? panY : 0;
        _background = background;
        Visibility = _style == BoardGridStyle.None ? Visibility.Collapsed : Visibility.Visible;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (_style == BoardGridStyle.None || ActualWidth <= 0 || ActualHeight <= 0) return;

        var worldStep = CalculateVisibleWorldSpacing(_spacing, _zoom);
        var step = worldStep * _zoom;
        if (!double.IsFinite(step) || step <= 0) return;
        var startX = PositiveModulo(_panX, step);
        var startY = PositiveModulo(_panY, step);
        var luminance = .2126 * _background.R + .7152 * _background.G + .0722 * _background.B;
        var useLightInk = luminance < 128;
        var minorBrush = CreateGridBrush(useLightInk, 28, 20);
        var majorBrush = CreateGridBrush(useLightInk, 50, 36);

        if (_style == BoardGridStyle.Lines)
        {
            var minorPen = CreateGridPen(minorBrush, .45);
            var majorPen = CreateGridPen(majorBrush, .7);
            for (var x = startX; x <= ActualWidth; x += step)
                context.DrawLine(IsMajorLine(x, _panX, step) ? majorPen : minorPen,
                    new Point(x, 0), new Point(x, ActualHeight));
            for (var y = startY; y <= ActualHeight; y += step)
                context.DrawLine(IsMajorLine(y, _panY, step) ? majorPen : minorPen,
                    new Point(0, y), new Point(ActualWidth, y));
            return;
        }

        var radius = Math.Clamp(step / 36, .32, .58);
        for (var x = startX; x <= ActualWidth; x += step)
            for (var y = startY; y <= ActualHeight; y += step)
            {
                var brush = IsMajorLine(x, _panX, step) && IsMajorLine(y, _panY, step)
                    ? majorBrush : minorBrush;
                context.DrawEllipse(brush, null, new Point(x, y), radius, radius);
            }
    }

    internal static double CalculateVisibleWorldSpacing(double spacing, double zoom)
    {
        spacing = double.IsFinite(spacing) && spacing > 0 ? spacing : 32;
        zoom = double.IsFinite(zoom) && zoom > 0 ? zoom : 1;
        var worldStep = spacing;
        var screenStep = worldStep * zoom;
        var minimumWorldStep = spacing / 4096;
        var maximumWorldStep = spacing * 4096;
        while (screenStep > MaximumScreenStep && worldStep > minimumWorldStep)
        {
            worldStep /= 2;
            screenStep /= 2;
        }
        while (screenStep < MinimumScreenStep && worldStep < maximumWorldStep)
        {
            worldStep *= 2;
            screenStep *= 2;
        }
        return worldStep;
    }

    private static bool IsMajorLine(double screenCoordinate, double pan, double step)
    {
        var gridIndex = (long)Math.Round((screenCoordinate - pan) / step);
        return gridIndex % MajorLineInterval == 0;
    }

    private static SolidColorBrush CreateGridBrush(bool useLightInk, byte lightAlpha, byte darkAlpha)
    {
        var color = useLightInk
            ? Color.FromArgb(lightAlpha, 255, 255, 255)
            : Color.FromArgb(darkAlpha, 0, 0, 0);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateGridPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static double PositiveModulo(double value, double divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }
}
