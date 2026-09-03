using System.Text.Json;
using System.Windows;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class DrawingGroupService
{
    public static IReadOnlyList<BoardDrawingStroke> Read(BoardDrawingItem item)
    {
        try
        {
            if (item.Kind == BoardDrawingKind.Group)
                return (JsonSerializer.Deserialize<List<BoardDrawingStroke>>(item.PointsJson) ?? new())
                    .Where(stroke => stroke is not null && stroke.Kind != BoardDrawingKind.Group &&
                        stroke.Points is { Count: > 0 } &&
                        stroke.Points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)))
                    .ToArray();

            var points = JsonSerializer.Deserialize<List<BoardStrokePoint>>(item.PointsJson) ?? new();
            if (points.Count == 0) return Array.Empty<BoardDrawingStroke>();
            if (item.Kind is BoardDrawingKind.Rectangle or BoardDrawingKind.Ellipse && points.Count >= 2)
            {
                var a = points[0];
                var b = points[^1];
                var w = Math.Max(1, item.Width);
                var h = Math.Max(1, item.Height);
                var inset = item.StrokeThickness / 2;
                var left = Math.Min(a.X, b.X) * w + inset;
                var top = Math.Min(a.Y, b.Y) * h + inset;
                var right = left + Math.Max(1, Math.Abs(a.X - b.X) * w - item.StrokeThickness);
                var bottom = top + Math.Max(1, Math.Abs(a.Y - b.Y) * h - item.StrokeThickness);
                points = new()
                {
                    new(left / w, top / h), new(right / w, top / h),
                    new(right / w, bottom / h), new(left / w, bottom / h)
                };
            }
            return new[]
            {
                new BoardDrawingStroke
                {
                    Kind = item.Kind, Points = points,
                    StrokeColor = item.StrokeColor, FillColor = item.FillColor,
                    StrokeThickness = item.StrokeThickness, StrokeOpacity = item.StrokeOpacity,
                    Dashed = item.Dashed
                }
            };
        }
        catch (JsonException) { return Array.Empty<BoardDrawingStroke>(); }
    }

    public static IReadOnlyList<BoardDrawingStroke> ToWorld(BoardDrawingItem item)
    {
        var center = new Point(item.X + item.Width / 2, item.Y + item.Height / 2);
        return Read(item).Select(stroke => stroke with
        {
            Points = stroke.Points.Select(p =>
            {
                var world = BoardMath.RotatePoint(
                    new Point(item.X + p.X * item.Width, item.Y + p.Y * item.Height),
                    center, item.Rotation);
                return new BoardStrokePoint(world.X, world.Y, p.Pressure);
            }).ToList()
        }).ToArray();
    }

    public static void Append(BoardDrawingItem group, BoardDrawingItem next)
    {
        var parts = ToWorld(group).Concat(ToWorld(next)).ToArray();
        SetWorldStrokes(group, parts);
    }

    public static void SetWorldStrokes(BoardDrawingItem group, IReadOnlyList<BoardDrawingStroke> world)
    {
        var points = world.SelectMany(part => part.Points).ToArray();
        if (points.Length == 0) throw new ArgumentException("A drawing group requires at least one point.", nameof(world));
        var padding = world.Max(part => part.Kind is BoardDrawingKind.Arrow or BoardDrawingKind.CurveArrow
            ? ArrowGeometry.Padding(part.StrokeThickness) : Math.Max(2, part.StrokeThickness / 2 + 1));
        var left = points.Min(p => p.X) - padding;
        var top = points.Min(p => p.Y) - padding;
        var width = Math.Max(4, points.Max(p => p.X) + padding - left);
        var height = Math.Max(4, points.Max(p => p.Y) + padding - top);
        var normalized = world.Select(part => part with
        {
            Points = part.Points.Select(p => new BoardStrokePoint(
                (p.X - left) / width, (p.Y - top) / height, p.Pressure)).ToList()
        }).ToArray();
        group.Kind = BoardDrawingKind.Group;
        group.X = left;
        group.Y = top;
        group.Width = width;
        group.Height = height;
        // The old rotation is baked into every stroke, including shape corners.
        group.Rotation = 0;
        group.PointsJson = JsonSerializer.Serialize(normalized);
        var last = world[^1];
        group.StrokeColor = last.StrokeColor;
        group.FillColor = last.FillColor;
        group.StrokeThickness = last.StrokeThickness;
        group.StrokeOpacity = last.StrokeOpacity;
        group.Dashed = last.Dashed;
    }
}
