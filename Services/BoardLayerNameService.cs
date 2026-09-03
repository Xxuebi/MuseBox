using System.Text;
using System.Xml.Linq;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class BoardLayerNameService
{
    public const int MaxNameLength = 80;

    public static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).Where(character => !char.IsControl(character))).Trim() is { } text
            ? text[..Math.Min(text.Length, MaxNameLength)] : string.Empty;

    public static void EnsureNames(IEnumerable<BoardElement> elements, IEnumerable<BoardGroup> groups)
    {
        var groupIndex = 1;
        foreach (var group in groups.OrderBy(group => group.Id, StringComparer.Ordinal))
            if (string.IsNullOrWhiteSpace(group.LayerName)) group.LayerName = $"组合 {groupIndex++}";
        foreach (var element in elements)
        {
            if (string.IsNullOrWhiteSpace(element.LayerName)) element.LayerName = DefaultName(element);
            else if (element is BoardItem && IsGenericClipboardName(element.LayerName))
                element.LayerName = ClipboardName(element.LayerName, element.CreatedUtc);
        }
    }

    public static string ClipboardName(string? description, DateTime createdUtc)
    {
        var name = Normalize(description);
        if (name.Length == 0) name = "剪贴板图片";
        return $"{name} {createdUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss}";
    }

    private static bool IsGenericClipboardName(string value) => value is
        "剪贴板图片" or "网页复制图片" or "GIF 动图" or "网页 GIF 动图" or "复制的图片文件" or "GIF 图片文件";

    public static string DefaultName(BoardElement element) => element switch
    {
        BoardItem image => ImageName(image),
        BoardTextItem text => TextName(text),
        BoardDrawingItem drawing => DrawingName(drawing),
        _ => "图层"
    };

    private static string ImageName(BoardItem image)
    {
        var name = Path.GetFileNameWithoutExtension(image.AssetPath);
        if (!string.IsNullOrWhiteSpace(name) && !(name.Length == 64 && name.All(Uri.IsHexDigit))) return Normalize(name);
        return $"图片 {image.CreatedUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss}";
    }

    private static string TextName(BoardTextItem text)
    {
        try
        {
            if (text.DocumentData.Length > 0)
            {
                var xml = Encoding.UTF8.GetString(Convert.FromBase64String(text.DocumentData));
                var plain = string.Join(' ', XDocument.Parse(xml).DescendantNodes().OfType<XText>()
                    .Select(node => node.Value.Trim()).Where(value => value.Length > 0));
                if (plain.Length > 0) return Normalize(plain);
            }
        }
        catch (Exception error) when (error is FormatException or System.Xml.XmlException) { }
        return $"文字 {text.CreatedUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss}";
    }

    private static string DrawingName(BoardDrawingItem drawing) => drawing.Kind switch
    {
        BoardDrawingKind.Line => "直线",
        BoardDrawingKind.Arrow or BoardDrawingKind.CurveArrow => "箭头",
        BoardDrawingKind.Rectangle => "矩形",
        BoardDrawingKind.Ellipse => "椭圆",
        BoardDrawingKind.Highlighter => "荧光笔",
        _ => "绘制"
    };
}
