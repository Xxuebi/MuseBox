using System.Windows;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

// Pure geometry: the caller owns group expansion, persistence and undo history.
public static class BoardLayoutService
{
    public const double Gap = 18;

    public static int MinimumUnits(BoardLayoutOperation operation) => 2;

    public static IReadOnlyDictionary<string, Vector> Calculate(
        IReadOnlyList<BoardLayoutUnit> units, BoardLayoutOperation operation)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (units.Select(unit => unit.Id).Distinct(StringComparer.Ordinal).Count() != units.Count ||
            units.Any(unit => unit.Bounds.IsEmpty || !double.IsFinite(unit.Bounds.X) ||
                !double.IsFinite(unit.Bounds.Y) || !double.IsFinite(unit.Bounds.Width) ||
                !double.IsFinite(unit.Bounds.Height)))
            throw new ArgumentException("排列单位必须具有唯一标识和有限边界。", nameof(units));
        var result = new Dictionary<string, Vector>(StringComparer.Ordinal);
        if (units.Count < MinimumUnits(operation)) return result;
        var bounds = Rect.Empty;
        foreach (var unit in units) bounds.Union(unit.Bounds);
        if (operation == BoardLayoutOperation.AutoArrange)
        {
            var boxes = units.OrderBy(unit => unit.ZIndex).ThenBy(unit => unit.Id, StringComparer.Ordinal)
                .Select(unit => new BoardItem { Id = unit.Id, Width = unit.Bounds.Width, Height = unit.Bounds.Height }).ToArray();
            var original = units.ToDictionary(unit => unit.Id, StringComparer.Ordinal);
            foreach (var box in BoardMath.ArrangeGrid(boxes, Gap))
                result[box.Id] = new Vector(bounds.Left + box.X - original[box.Id].Bounds.Left,
                    bounds.Top + box.Y - original[box.Id].Bounds.Top);
        }
        else if (operation is BoardLayoutOperation.DistributeHorizontal or BoardLayoutOperation.DistributeVertical
                 or BoardLayoutOperation.ArrangeLeft or BoardLayoutOperation.ArrangeRight
                 or BoardLayoutOperation.ArrangeTop or BoardLayoutOperation.ArrangeBottom)
        {
            var horizontal = operation is BoardLayoutOperation.DistributeHorizontal
                or BoardLayoutOperation.ArrangeTop or BoardLayoutOperation.ArrangeBottom;
            var ordered = units.OrderBy(unit => horizontal ? unit.Bounds.Left : unit.Bounds.Top)
                .ThenBy(unit => unit.ZIndex).ThenBy(unit => unit.Id, StringComparer.Ordinal).ToArray();
            var position = horizontal ? bounds.Left : bounds.Top;
            foreach (var unit in ordered)
            {
                var cross = operation switch
                {
                    BoardLayoutOperation.ArrangeLeft => bounds.Left,
                    BoardLayoutOperation.ArrangeRight => bounds.Right - unit.Bounds.Width,
                    BoardLayoutOperation.ArrangeTop => bounds.Top,
                    BoardLayoutOperation.ArrangeBottom => bounds.Bottom - unit.Bounds.Height,
                    BoardLayoutOperation.DistributeHorizontal => bounds.Top + (bounds.Height - unit.Bounds.Height) / 2,
                    _ => bounds.Left + (bounds.Width - unit.Bounds.Width) / 2
                };
                result[unit.Id] = horizontal
                    ? new Vector(position - unit.Bounds.Left, cross - unit.Bounds.Top)
                    : new Vector(cross - unit.Bounds.Left, position - unit.Bounds.Top);
                position += (horizontal ? unit.Bounds.Width : unit.Bounds.Height) + Gap;
            }
        }
        else
        {
            foreach (var unit in units)
                result[unit.Id] = operation switch
                {
                    BoardLayoutOperation.AlignLeft => new Vector(bounds.Left - unit.Bounds.Left, 0),
                    BoardLayoutOperation.AlignRight => new Vector(bounds.Right - unit.Bounds.Right, 0),
                    BoardLayoutOperation.AlignTop => new Vector(0, bounds.Top - unit.Bounds.Top),
                    BoardLayoutOperation.AlignBottom => new Vector(0, bounds.Bottom - unit.Bounds.Bottom),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                };
        }
        return result;
    }
}
