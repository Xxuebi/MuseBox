using System.Windows;

namespace ScreenshotCollector.Models;

public enum BoardLayoutOperation
{
    AutoArrange, AlignLeft, AlignRight, AlignTop, AlignBottom,
    DistributeHorizontal, DistributeVertical,
    ArrangeLeft, ArrangeRight, ArrangeTop, ArrangeBottom
}

public enum BoardElementKind { Image, Text, Drawing }

public sealed record BoardElementPosition(string Id, BoardElementKind Kind, double X, double Y);

public sealed record BoardLayoutUnit(string Id, Rect Bounds, int ZIndex);
