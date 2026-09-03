using System.Windows;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class BoardMath
{
    public static double NormalizeAngle(double angle)
    {
        angle %= 360;
        return angle < 0 ? angle + 360 : angle;
    }

    public static double NormalizeAngleDelta(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle < -180) angle += 360;
        return angle;
    }

    public static System.Windows.Point RotatePoint(
        System.Windows.Point point, System.Windows.Point center, double angle)
    {
        var radians = angle * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var x = point.X - center.X;
        var y = point.Y - center.Y;
        return new System.Windows.Point(
            center.X + x * cosine - y * sine,
            center.Y + x * sine + y * cosine);
    }

    public static void ResizeItem(
        BoardItem item, BoardResizeDirection direction, double dx, double dy, double minimum = 40)
    {
        var right = item.X + item.Width;
        var bottom = item.Y + item.Height;
        if (direction is BoardResizeDirection.West or BoardResizeDirection.NorthWest or BoardResizeDirection.SouthWest)
        {
            item.X = Math.Min(right - minimum, item.X + dx);
            item.Width = right - item.X;
        }
        if (direction is BoardResizeDirection.East or BoardResizeDirection.NorthEast or BoardResizeDirection.SouthEast)
            item.Width = Math.Max(minimum, item.Width + dx);
        if (direction is BoardResizeDirection.North or BoardResizeDirection.NorthWest or BoardResizeDirection.NorthEast)
        {
            item.Y = Math.Min(bottom - minimum, item.Y + dy);
            item.Height = bottom - item.Y;
        }
        if (direction is BoardResizeDirection.South or BoardResizeDirection.SouthWest or BoardResizeDirection.SouthEast)
            item.Height = Math.Max(minimum, item.Height + dy);
    }

    public static BoardItem ResizeFromSnapshot(
        BoardItem snapshot,
        BoardResizeDirection direction,
        double dx,
        double dy,
        bool preserveAspect,
        bool fromCenter,
        double minimum = 40)
    {
        var result = snapshot.Clone();
        var left = snapshot.X;
        var top = snapshot.Y;
        var right = snapshot.X + snapshot.Width;
        var bottom = snapshot.Y + snapshot.Height;
        var movesWest = direction is BoardResizeDirection.West or BoardResizeDirection.NorthWest or BoardResizeDirection.SouthWest;
        var movesEast = direction is BoardResizeDirection.East or BoardResizeDirection.NorthEast or BoardResizeDirection.SouthEast;
        var movesNorth = direction is BoardResizeDirection.North or BoardResizeDirection.NorthWest or BoardResizeDirection.NorthEast;
        var movesSouth = direction is BoardResizeDirection.South or BoardResizeDirection.SouthWest or BoardResizeDirection.SouthEast;

        if (movesWest) { left += dx; if (fromCenter) right -= dx; }
        if (movesEast) { right += dx; if (fromCenter) left -= dx; }
        if (movesNorth) { top += dy; if (fromCenter) bottom -= dy; }
        if (movesSouth) { bottom += dy; if (fromCenter) top -= dy; }

        var width = Math.Max(minimum, right - left);
        var height = Math.Max(minimum, bottom - top);
        if (preserveAspect)
        {
            var aspect = snapshot.Width / Math.Max(1, snapshot.Height);
            var horizontalEdgeOnly = direction is BoardResizeDirection.East or BoardResizeDirection.West;
            var verticalEdgeOnly = direction is BoardResizeDirection.North or BoardResizeDirection.South;
            if (horizontalEdgeOnly)
            {
                height = Math.Max(minimum, width / aspect);
                var centerY = snapshot.Y + snapshot.Height / 2;
                top = centerY - height / 2;
                bottom = centerY + height / 2;
            }
            else if (verticalEdgeOnly)
            {
                width = Math.Max(minimum, height * aspect);
                var centerX = snapshot.X + snapshot.Width / 2;
                left = centerX - width / 2;
                right = centerX + width / 2;
            }
            else
            {
                var widthChange = Math.Abs(width / snapshot.Width - 1);
                var heightChange = Math.Abs(height / snapshot.Height - 1);
                if (widthChange >= heightChange)
                {
                    height = Math.Max(minimum, width / aspect);
                    if (movesNorth) top = bottom - height;
                    else bottom = top + height;
                }
                else
                {
                    width = Math.Max(minimum, height * aspect);
                    if (movesWest) left = right - width;
                    else right = left + width;
                }
            }
        }

        if (fromCenter)
        {
            var centerX = snapshot.X + snapshot.Width / 2;
            var centerY = snapshot.Y + snapshot.Height / 2;
            left = centerX - width / 2;
            right = centerX + width / 2;
            top = centerY - height / 2;
            bottom = centerY + height / 2;
        }

        if (right - left < minimum)
        {
            if (movesWest) left = right - minimum;
            else right = left + minimum;
        }
        if (bottom - top < minimum)
        {
            if (movesNorth) top = bottom - minimum;
            else bottom = top + minimum;
        }
        result.X = left;
        result.Y = top;
        result.Width = right - left;
        result.Height = bottom - top;
        return result;
    }

    // dx/dy are board-space movement. Resize in the item's own axes, then
    // rotate the center displacement back so the opposite handle stays fixed.
    public static BoardItem ResizeRotatedFromSnapshot(
        BoardItem snapshot, BoardResizeDirection direction, double dx, double dy,
        bool preserveAspect, bool fromCenter, double minimum = 40)
    {
        var radians = snapshot.Rotation * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var localDx = dx * cosine + dy * sine;
        var localDy = -dx * sine + dy * cosine;
        var result = ResizeFromSnapshot(snapshot, direction, localDx, localDy, preserveAspect, fromCenter, minimum);
        var center = new System.Windows.Point(snapshot.X + snapshot.Width / 2, snapshot.Y + snapshot.Height / 2);
        var localCenter = new System.Windows.Point(result.X + result.Width / 2, result.Y + result.Height / 2);
        var rotatedCenter = RotatePoint(localCenter, center, snapshot.Rotation);
        result.X = rotatedCenter.X - result.Width / 2;
        result.Y = rotatedCenter.Y - result.Height / 2;
        return result;
    }

    public static (double PanX, double PanY) ZoomAt(
        System.Windows.Point screenPoint, double oldZoom, double newZoom, double panX, double panY)
    {
        newZoom = Math.Clamp(newZoom, 0.05, 8);
        var worldX = (screenPoint.X - panX) / oldZoom;
        var worldY = (screenPoint.Y - panY) / oldZoom;
        return (screenPoint.X - worldX * newZoom, screenPoint.Y - worldY * newZoom);
    }

    public static IReadOnlyList<BoardItem> ArrangeGrid(IReadOnlyList<BoardItem> source, double gap = 18)
    {
        if (source.Count == 0) return Array.Empty<BoardItem>();

        var totalArea = source.Sum(item =>
            (Math.Max(1, item.Width) + gap) * (Math.Max(1, item.Height) + gap));
        var targetWidth = Math.Max(
            source.Max(x => Math.Max(1, x.Width)),
            Math.Sqrt(totalArea) * 1.15);
        var placed = new List<Rect>(source.Count);
        var positions = new Dictionary<string, System.Windows.Point>(StringComparer.Ordinal);

        foreach (var item in source
                     .OrderByDescending(x => Math.Max(1, x.Width) * Math.Max(1, x.Height))
                     .ThenByDescending(x => x.Height))
        {
            var width = Math.Max(1, item.Width);
            var height = Math.Max(1, item.Height);
            var candidates = new[] { 0d }
                .Concat(placed.Select(rect => rect.Right + gap))
                .Distinct()
                .ToArray();
            var bestX = 0d;
            var bestY = double.PositiveInfinity;
            var bestScore = double.PositiveInfinity;
            foreach (var x in candidates)
            {
                var y = placed
                    .Where(rect => x < rect.Right + gap && x + width + gap > rect.Left)
                    .Select(rect => rect.Bottom + gap)
                    .DefaultIfEmpty(0)
                    .Max();
                // A small overflow is preferable to dropping a short item below a
                // tall image; this fills the usable side space instead of leaving a
                // large vertical hole. Larger overflow naturally starts a new row.
                var overflow = Math.Max(0, x + width - targetWidth);
                var score = y + height + overflow * .8;
                if (score < bestScore - .001 || Math.Abs(score - bestScore) < .001 && x < bestX)
                {
                    bestX = x;
                    bestY = y;
                    bestScore = score;
                }
            }
            if (double.IsPositiveInfinity(bestY)) bestY = 0;
            positions[item.Id] = new System.Windows.Point(bestX, bestY);
            placed.Add(new Rect(bestX, bestY, width, height));
        }

        return source.Select((item, index) =>
        {
            var clone = item.Clone();
            var position = positions[item.Id];
            clone.X = position.X;
            clone.Y = position.Y;
            clone.ZIndex = index;
            return clone;
        }).ToArray();
    }

    public static Rect GetBounds(IReadOnlyList<BoardItem> items)
    {
        if (items.Count == 0) return Rect.Empty;
        var left = items.Min(x => x.X);
        var top = items.Min(x => x.Y);
        var right = items.Max(x => x.X + x.Width);
        var bottom = items.Max(x => x.Y + x.Height);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public static IReadOnlyList<BoardItem> ScaleGroup(
        IReadOnlyList<BoardItem> snapshots, Rect originalBounds, Rect targetBounds)
    {
        if (snapshots.Count == 0 || originalBounds.IsEmpty) return Array.Empty<BoardItem>();
        var scaleX = targetBounds.Width / Math.Max(1, originalBounds.Width);
        var scaleY = targetBounds.Height / Math.Max(1, originalBounds.Height);
        return snapshots.Select(snapshot =>
        {
            var result = snapshot.Clone();
            result.X = targetBounds.X + (snapshot.X - originalBounds.X) * scaleX;
            result.Y = targetBounds.Y + (snapshot.Y - originalBounds.Y) * scaleY;
            result.Width = Math.Max(1, snapshot.Width * scaleX);
            result.Height = Math.Max(1, snapshot.Height * scaleY);
            return result;
        }).ToArray();
    }

    public static (double Width, double Height) FitSize(int pixelWidth, int pixelHeight, double maxEdge = 420)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return (320, 240);
        var scale = Math.Min(1, maxEdge / Math.Max(pixelWidth, pixelHeight));
        return (Math.Max(48, pixelWidth * scale), Math.Max(48, pixelHeight * scale));
    }

    public static IReadOnlyList<BoardItem> ShiftLayer(
        IReadOnlyList<BoardItem> source, IReadOnlySet<string> selectedIds, int direction)
    {
        var ordered = source.OrderBy(x => x.ZIndex).ToList();
        if (direction > 0)
        {
            for (var index = ordered.Count - 2; index >= 0; index--)
            {
                if (selectedIds.Contains(ordered[index].Id) && !selectedIds.Contains(ordered[index + 1].Id))
                    (ordered[index], ordered[index + 1]) = (ordered[index + 1], ordered[index]);
            }
        }
        else
        {
            for (var index = 1; index < ordered.Count; index++)
            {
                if (selectedIds.Contains(ordered[index].Id) && !selectedIds.Contains(ordered[index - 1].Id))
                    (ordered[index], ordered[index - 1]) = (ordered[index - 1], ordered[index]);
            }
        }
        for (var index = 0; index < ordered.Count; index++) ordered[index].ZIndex = index;
        return ordered;
    }

    public static IReadOnlyList<BoardItem> MoveToExtreme(
        IReadOnlyList<BoardItem> source, IReadOnlySet<string> selectedIds, bool toFront)
    {
        var ordered = source.OrderBy(x => x.ZIndex).ToArray();
        var selected = ordered.Where(x => selectedIds.Contains(x.Id));
        var unselected = ordered.Where(x => !selectedIds.Contains(x.Id));
        var result = (toFront ? unselected.Concat(selected) : selected.Concat(unselected)).ToArray();
        for (var index = 0; index < result.Length; index++) result[index].ZIndex = index;
        return result;
    }
}

public enum BoardResizeDirection
{
    NorthWest,
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West
}
