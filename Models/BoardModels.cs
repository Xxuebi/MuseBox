namespace ScreenshotCollector.Models;

public sealed record Drawer(string Id, int SortOrder, DateTime CreatedUtc, string DisplayName)
{
    public DrawerCover? Cover { get; init; }
    public string? ScenePath { get; init; }
    public bool HasUnsavedScene { get; init; }
}

public sealed record AssetRecord(
    string Id,
    string Hash,
    string Extension,
    string FileName,
    int PixelWidth,
    int PixelHeight,
    DateTime CreatedUtc);

public interface IBoardElement
{
    string Id { get; }
    string DrawerId { get; }
    double X { get; set; }
    double Y { get; set; }
    double Width { get; set; }
    double Height { get; set; }
    double Rotation { get; set; }
    int ZIndex { get; set; }
    DateTime CreatedUtc { get; }
}

public abstract class BoardElement : IBoardElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DrawerId { get; set; } = "A";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 240;
    public double Rotation { get; set; }
    public int ZIndex { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string LayerName { get; set; } = string.Empty;
    // The immediate parent in the layer tree. Empty means a root layer.
    public string GroupId { get; set; } = string.Empty;
    public string GroupBackgroundColor { get; set; } = "#52FFFFFF";
    public string GroupBorderColor { get; set; } = "#807A7A7A";
    public double GroupBorderThickness { get; set; } = 1.2;
    public double GroupFramePadding { get; set; } = 14;
    public bool GroupBackgroundVisible { get; set; } = true;
    public bool GroupLocked { get; set; } = true;
    public bool GroupAutoMembership { get; set; }

    public abstract BoardElement CloneElement();
}

public sealed class BoardGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DrawerId { get; set; } = "A";
    public string ParentGroupId { get; set; } = string.Empty;
    public string LayerName { get; set; } = string.Empty;
    public string BackgroundColor { get; set; } = "#52FFFFFF";
    public string BorderColor { get; set; } = "#807A7A7A";
    public double BorderThickness { get; set; } = 1.2;
    public double FramePadding { get; set; } = 14;
    public bool BackgroundVisible { get; set; } = true;
    public bool Locked { get; set; } = true;
    public bool AutoMembership { get; set; }

    public BoardGroup Clone() => (BoardGroup)MemberwiseClone();
}

public sealed class BoardItem : BoardElement
{
    public string AssetId { get; set; } = string.Empty;
    public string AssetPath { get; set; } = string.Empty;
    public string WebLink { get; set; } = string.Empty;
    public string FileLink { get; set; } = string.Empty;

    public BoardItem Clone() => (BoardItem)MemberwiseClone();
    public override BoardElement CloneElement() => Clone();
}

public sealed class BoardTextItem : BoardElement
{
    public string DocumentData { get; set; } = string.Empty;
    public string BackgroundColor { get; set; } = "#00FFFFFF";
    public string WebLink { get; set; } = string.Empty;
    public string FileLink { get; set; } = string.Empty;

    public BoardTextItem Clone() => (BoardTextItem)MemberwiseClone();
    public override BoardElement CloneElement() => Clone();
}

public enum BoardDrawingKind
{
    Pen,
    Highlighter,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Group,
    CurveArrow
}

public sealed record BoardStrokePoint(double X, double Y, double Pressure = 1);

// Points are normalized to the containing drawing item's bounds. Shapes store
// four corners so appending after a rotated/non-uniform transform is lossless.
public sealed record BoardDrawingStroke
{
    public BoardDrawingKind Kind { get; init; }
    public List<BoardStrokePoint> Points { get; init; } = new();
    public string StrokeColor { get; init; } = "#FF55A6C9";
    public string FillColor { get; init; } = "#00000000";
    public double StrokeThickness { get; init; } = 4;
    public double StrokeOpacity { get; init; } = 1;
    public bool Dashed { get; init; }
}

public sealed class BoardDrawingItem : BoardElement
{
    public BoardDrawingKind Kind { get; set; } = BoardDrawingKind.Pen;
    public string PointsJson { get; set; } = "[]";
    public string StrokeColor { get; set; } = "#FF55A6C9";
    public string FillColor { get; set; } = "#00000000";
    public double StrokeThickness { get; set; } = 4;
    public double StrokeOpacity { get; set; } = 1;
    public bool Dashed { get; set; }

    public BoardDrawingItem Clone() => (BoardDrawingItem)MemberwiseClone();
    public override BoardElement CloneElement() => Clone();
}

public sealed class BoardViewport
{
    public string DrawerId { get; set; } = "A";
    public double PanX { get; set; }
    public double PanY { get; set; }
    public double Zoom { get; set; } = 1;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 760;
    public bool Topmost { get; set; }
    public string BackgroundColor { get; set; } = "#7A7A7A";
    public double WindowOpacity { get; set; } = 1;
    public bool OpacityAffectsImages { get; set; }
    public bool ShowWindowFrame { get; set; } = true;
}

public sealed record ImportedAsset(AssetRecord Asset, string FullPath);
