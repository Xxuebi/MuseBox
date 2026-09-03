using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class SceneValidation
{
    public const int MaxElements = 100000;
    public const int MaxAssets = 10000;
    public const int MaxGroups = 100000;
    public static void Validate(SceneDocument scene)
    {
        if (scene.Format is not ("MuseBox.Scene" or "InspirationCollector.Scene") || scene.Version is not (1 or 2))
            throw new InvalidDataException("无法打开此场景版本，请使用支持该版本的 MuseBox。");
        SceneMigration.UpgradeToCurrent(scene);
        Require(scene.Name is { Length: > 0 and <= 30 } && !scene.Name.Any(char.IsControl), "画板名称无效");
        Require(scene.Images is not null && scene.Texts is not null && scene.Drawings is not null && scene.Groups is not null && scene.Assets is not null &&
            scene.Gifs is not null && scene.Viewport is not null, "场景缺少内容");
        var elements = scene.Images!.Cast<BoardElement>().Concat(scene.Texts!).Concat(scene.Drawings!).ToList();
        var sceneGroups = scene.Groups!;
        Require(elements.Count <= MaxElements && sceneGroups.Count <= MaxGroups && scene.Assets!.Count <= MaxAssets, "场景内容超出安全上限");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in elements)
        {
            Require(item is not null && Key(item.Id) && ids.Add(item.Id), "对象编号无效或重复");
            var element = item!;
            Require(Finite(element.X) && Finite(element.Y) && Range(element.Width, .001, 1e7) &&
                Range(element.Height, .001, 1e7) && Finite(element.Rotation), "对象坐标或尺寸无效");
            Require((element.GroupId == "" || Key(element.GroupId)) && ValidLayerName(element.LayerName), "图层名称或父级无效");
        }
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in sceneGroups)
        {
            Require(candidate is not null, "组合数据无效");
            var group = candidate!;
            Require(group is not null && Key(group.Id) && groupIds.Add(group.Id) &&
                (group.ParentGroupId.Length == 0 || Key(group.ParentGroupId)) && ValidLayerName(group.LayerName) &&
                ValidColor(group.BackgroundColor) && ValidColor(group.BorderColor) &&
                Range(group.BorderThickness, 0, 10000) && Range(group.FramePadding, 0, 10000), "组合数据无效");
        }
        BoardLayerTreeService.Validate(sceneGroups, elements);
        var assets = new Dictionary<string, SceneAsset>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in scene.Assets!)
        {
            Require(a is not null && Key(a.Id) && !assets.ContainsKey(a.Id), "资源编号无效或重复");
            Require(Regex.IsMatch(a!.Hash ?? "", "^[a-f0-9]{64}$") && hashes.Add(a.Hash!) &&
                new[] { ".png", ".jpg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(a.Extension), "资源格式无效或重复");
            Require(a.Width > 0 && a.Height > 0 && (long)a.Width * a.Height <= 100_000_000, "图片尺寸超出安全上限");
            assets.Add(a.Id, a);
        }
        var referenced = new HashSet<string>();
        foreach (var image in scene.Images!)
        {
            Require(assets.ContainsKey(image.AssetId ?? ""), "场景缺少图片资源");
            Require(image.AssetPath == "", "图片路径无效");
            referenced.Add(image.AssetId!);
            ValidateLinks(image.WebLink, image.FileLink);
        }
        foreach (var text in scene.Texts!)
        {
            Require(ValidColor(text.BackgroundColor), "注释背景颜色无效");
            ValidateLinks(text.WebLink, text.FileLink);
            ValidateRichText(text.DocumentData);
        }
        long points = 0;
        foreach (var drawing in scene.Drawings!)
        {
            Require(Enum.IsDefined(drawing.Kind) && ValidColor(drawing.StrokeColor) && ValidColor(drawing.FillColor) &&
                Range(drawing.StrokeThickness, 0, 10000) && Range(drawing.StrokeOpacity, 0, 1), "笔迹样式无效");
            Require(drawing.PointsJson is { Length: <= 32_000_000 }, "笔迹数据过大");
            if (drawing.Kind == BoardDrawingKind.Group)
            {
                var strokes = JsonSerializer.Deserialize<List<BoardDrawingStroke>>(drawing.PointsJson!) ?? throw new InvalidDataException("笔迹为空");
                foreach (var stroke in strokes)
                {
                    Require(stroke is not null && Enum.IsDefined(stroke.Kind) && stroke.Kind != BoardDrawingKind.Group &&
                        ValidColor(stroke.StrokeColor) && ValidColor(stroke.FillColor) &&
                        Range(stroke.StrokeThickness, 0, 10000) && Range(stroke.StrokeOpacity, 0, 1), "分组笔迹样式无效");
                    ValidatePoints(stroke!.Points, ref points);
                }
            }
            else ValidatePoints(JsonSerializer.Deserialize<List<BoardStrokePoint>>(drawing.PointsJson!)!, ref points);
        }
        var v = scene.Viewport!;
        Require(Finite(v.PanX) && Finite(v.PanY) && Range(v.Zoom, .05, 8) &&
            (!v.WindowLeft.HasValue || Finite(v.WindowLeft.Value)) && (!v.WindowTop.HasValue || Finite(v.WindowTop.Value)) &&
            Range(v.WindowWidth, 1, 100000) && Range(v.WindowHeight, 1, 100000) &&
            ValidColor(v.BackgroundColor) && Range(v.WindowOpacity, 0, 1), "画板设置无效");
        if (scene.Cover is { } cover)
        {
            Require(assets.ContainsKey(cover.SourceAssetId) && assets.ContainsKey(cover.PreviewAssetId) &&
                cover.SourcePath == "" && cover.PreviewPath == "" && cover.Crop is not null, "封面资源无效");
            Require(cover.Crop!.QuarterTurns is >= 0 and <= 3 && Range(cover.Crop.Zoom, 1, 8) &&
                Finite(cover.Crop.PanX) && Finite(cover.Crop.PanY), "封面裁切参数无效");
            referenced.Add(cover.SourceAssetId); referenced.Add(cover.PreviewAssetId);
        }
        Require(referenced.SetEquals(assets.Keys), "场景包含未引用的资源");
        var gifs = new HashSet<string>();
        foreach (var gif in scene.Gifs!)
        {
            var item = scene.Images.FirstOrDefault(i => i.Id == gif?.ItemId);
            Require(gif is not null && item is not null && assets[item.AssetId].Extension == ".gif" &&
                gifs.Add(gif.ItemId) && Range(gif.Speed, .25, 4) && gif.FrameIndex is >= 0 and <= 1000000, "GIF 状态无效");
        }
    }

    private static void ValidatePoints(List<BoardStrokePoint>? points, ref long count)
    {
        Require(points is not null && (count += points.Count) <= 2_000_000, "笔迹点数量超出安全上限");
        foreach (var p in points!) Require(p is not null && Finite(p.X) && Finite(p.Y) && Range(p.Pressure, 0, 10), "笔迹坐标无效");
    }
    private static bool Key(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Finite(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e9;
    private static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    private static bool ValidColor(string? value) => value is not null && (Regex.IsMatch(value, "^#(?:[a-fA-F0-9]{8}|[a-fA-F0-9]{6}|[a-fA-F0-9]{4}|[a-fA-F0-9]{3})$") ||
        Enum.TryParse<System.Drawing.KnownColor>(value, true, out _));
    private static bool ValidLayerName(string? value) => value is not null && value.Length <= BoardLayerNameService.MaxNameLength && !value.Any(char.IsControl);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message + "。"); }
    private static void ValidateLinks(string web, string file)
    {
        Require(web is not null && web.Length <= 8192 && file is not null && file.Length <= 32768, "链接过长或无效");
        if (web!.Length > 0) ImageLinkService.NormalizeWeb(web);
        if (file!.Length > 0) ImageLinkService.NormalizeFile(file);
    }

    // Do not instantiate any WPF objects from a file until its XML has passed
    // this allow-list. No markup extensions, external fonts, resources or events.
    public static void ValidateRichText(string data)
    {
        Require(data is not null && data.Length <= 12_000_000, "注释内容过大");
        if (data!.Length == 0) return;
        var bytes = Convert.FromBase64String(data);
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8_000_000 });
        var xml = XDocument.Load(reader);
        const string ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var tags = new HashSet<string>("Section Paragraph Span Run LineBreak Bold Italic Underline List ListItem Table TableRowGroup TableRow TableCell TableColumn".Split(' '));
        var attributes = new HashSet<string>(("FontFamily FontSize FontWeight FontStyle FontStretch Foreground Background TextDecorations " +
            "TextAlignment FlowDirection Language Margin Padding TextIndent LineHeight LineStackingStrategy KeepTogether KeepWithNext " +
            "BreakPageBefore BreakColumnBefore BaselineAlignment HasTrailingParagraphBreakOnPaste MarkerStyle StartIndex MarkerOffset " +
            "BorderBrush BorderThickness ColumnSpan RowSpan CellSpacing Width IsHyphenationEnabled").Split(' '));
        // WPF's own TextRange writer emits these built-in typography attached
        // properties even when the user only types plain text.
        foreach (var property in typeof(System.Windows.Documents.Typography).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(System.Windows.DependencyProperty) && f.Name.EndsWith("Property", StringComparison.Ordinal)))
            attributes.Add("Typography." + property.Name[..^8]);
        attributes.UnionWith(new[] { "NumberSubstitution.CultureSource", "NumberSubstitution.Substitution", "NumberSubstitution.CultureOverride" });
        Require(xml.Root is not null && xml.Descendants().Take(200001).Count() <= 200000, "注释结构过大");
        foreach (var element in xml.Descendants())
        {
            Require(element.Ancestors().Take(65).Count() <= 64, "注释嵌套层级过深");
            Require(element.Name.NamespaceName == ns && tags.Contains(element.Name.LocalName), "注释包含不支持或不安全的类型");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    Require(attribute.Value == ns || attribute.Value == XNamespace.Xml.NamespaceName, "注释包含外部命名空间");
                    continue;
                }
                if (attribute.Name == XNamespace.Xml + "space" || attribute.Name == XNamespace.Xml + "lang") continue;
                var name = attribute.Name.LocalName;
                Require(attribute.Name.NamespaceName == "" && attributes.Contains(name) &&
                    !attribute.Value.Contains('{') && !attribute.Value.Contains('}'), $"注释包含不支持或不安全的属性：{name}");
                if (name == "FontFamily") Require(attribute.Value.IndexOfAny(new[] { '/', '\\', ':', '#' }) < 0, "注释引用了外部字体文件");
                if (name is "Foreground" or "Background" or "BorderBrush") Require(ValidColor(attribute.Value), "注释颜色无效");
            }
        }
    }
}
