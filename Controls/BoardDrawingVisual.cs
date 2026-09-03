using System.Windows;
using System.Windows.Media;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector.Controls;

public sealed class BoardDrawingVisual : FrameworkElement
{
    private BoardDrawingItem? _item;
    private IReadOnlyList<BoardDrawingStroke> _strokes = Array.Empty<BoardDrawingStroke>();
    private DrawingGroup? _cachedDrawing;
    private Size _cachedSize;

    public BoardDrawingItem? Item
    {
        get => _item;
        set
        {
            _item = value;
            _strokes = value is null ? Array.Empty<BoardDrawingStroke>() : DrawingGroupService.Read(value);
            _cachedDrawing = null;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var size = new Size(ActualWidth, ActualHeight);
        if (_cachedDrawing is null || _cachedSize != size)
        {
            var group = new DrawingGroup();
            using (var drawing = group.Open())
                foreach (var stroke in _strokes) DrawStroke(drawing, stroke, size);
            group.Freeze();
            _cachedDrawing = group;
            _cachedSize = size;
        }
        context.DrawDrawing(_cachedDrawing);
    }

    private static void DrawStroke(DrawingContext context, BoardDrawingStroke stroke, Size size)
    {
        var points = stroke.Points.Select(p => new Point(p.X * size.Width, p.Y * size.Height)).ToArray();
        if (points.Length == 0) return;
        var brush = ParseBrush(stroke.StrokeColor, stroke.StrokeOpacity);
        var fill = ParseBrush(stroke.FillColor, 1);
        var thickness = Math.Max(.5, stroke.StrokeThickness);
        if (stroke.Kind == BoardDrawingKind.CurveArrow)
        {
            var pen = CreatePen(brush, thickness, false);
            if (points.Length == 1) { context.DrawEllipse(brush, null, points[0], thickness / 2, thickness / 2); return; }
            var geometry = new StreamGeometry();
            using (var writer = geometry.Open())
            {
                writer.BeginFigure(points[0], false, false);
                writer.PolyLineTo(points.Skip(1).ToArray(), true, false);
                var head = ArrowGeometry.Head(points, thickness);
                if (head.Length > 0)
                {
                    writer.BeginFigure(head[0], false, false);
                    writer.PolyLineTo(head.Skip(1).ToArray(), true, false);
                }
            }
            geometry.Freeze();
            context.DrawGeometry(null, pen, geometry);
            return;
        }
        if (stroke.Kind is BoardDrawingKind.Pen or BoardDrawingKind.Highlighter)
        {
            if (points.Length == 1)
            {
                var pressure = Math.Clamp(stroke.Points[0].Pressure, .2, 1);
                context.DrawEllipse(brush, null, points[0], thickness * pressure / 2, thickness * pressure / 2);
                return;
            }
            var pens = new Dictionary<int, Pen>();
            for (var index = 1; index < points.Length; index++)
            {
                var pressure = Math.Clamp((stroke.Points[index - 1].Pressure + stroke.Points[index].Pressure) / 2, .2, 1);
                var key = (int)Math.Round(pressure * 100);
                if (!pens.TryGetValue(key, out var pen))
                    pens[key] = pen = CreatePen(brush, thickness * key / 100, stroke.Dashed);
                context.DrawLine(pen, points[index - 1], points[index]);
            }
            return;
        }
        var shapePen = CreatePen(brush, thickness, stroke.Dashed);
        if (stroke.Kind is BoardDrawingKind.Line or BoardDrawingKind.Arrow)
        {
            context.DrawLine(shapePen, points[0], points[^1]);
            if (stroke.Kind == BoardDrawingKind.Arrow)
            {
                var vector = points[0] - points[^1];
                if (vector.Length < .1) return;
                vector.Normalize();
                var perpendicular = new Vector(-vector.Y, vector.X);
                var length = Math.Max(10, thickness * 3.2);
                context.DrawLine(shapePen, points[^1], points[^1] + vector * length + perpendicular * length * .45);
                context.DrawLine(shapePen, points[^1], points[^1] + vector * length - perpendicular * length * .45);
            }
            return;
        }
        if (points.Length < 4) return;
        if (stroke.Kind == BoardDrawingKind.Rectangle)
        {
            var geometry = new StreamGeometry();
            using (var writer = geometry.Open())
            {
                writer.BeginFigure(points[0], true, true);
                writer.PolyLineTo(points.Skip(1).ToArray(), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(fill, shapePen, geometry);
        }
        else if (stroke.Kind == BoardDrawingKind.Ellipse)
        {
            // Affine ellipse from four corners preserves rotation and nonuniform scaling.
            var xAxis = points[1] - points[0];
            var yAxis = points[3] - points[0];
            var geometry = new EllipseGeometry(new Rect(0, 0, 1, 1))
            {
                Transform = new MatrixTransform(xAxis.X, xAxis.Y, yAxis.X, yAxis.Y, points[0].X, points[0].Y)
            };
            geometry.Freeze();
            context.DrawGeometry(fill, shapePen, geometry);
        }
    }

    private static Pen CreatePen(Brush brush, double thickness, bool dashed)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round, DashStyle = dashed ? DashStyles.Dash : DashStyles.Solid
        };
        pen.Freeze();
        return pen;
    }

    private static Brush ParseBrush(string value, double opacity)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            color.A = (byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1));
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch { return Brushes.DeepSkyBlue; }
    }
}
