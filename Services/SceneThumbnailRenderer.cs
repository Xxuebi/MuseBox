using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Xml.Linq;
using ScreenshotCollector.Models;
using DrawingColor = System.Drawing.Color;

namespace ScreenshotCollector.Services;

public static class SceneThumbnailRenderer
{
    public const int Edge = 512;
    private const float CanvasPadding = 24;

    public static byte[] Render(SceneSnapshot snapshot)
    {
        using var bitmap = new Bitmap(Edge, Edge, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.Clear(DrawingColor.Transparent);

        var elements = snapshot.Document.Images.Cast<BoardElement>()
            .Concat(snapshot.Document.Texts)
            .Concat(snapshot.Document.Drawings)
            .OrderBy(element => element.ZIndex)
            .ThenBy(element => element.Id, StringComparer.Ordinal)
            .ToArray();
        var bounds = ContentBounds(elements, snapshot.Document.Groups, snapshot.Document.Viewport);
        var scale = Math.Min((Edge - CanvasPadding * 2) / (float)bounds.Width,
            (Edge - CanvasPadding * 2) / (float)bounds.Height);
        if (!float.IsFinite(scale) || scale <= 0) scale = 1;
        var left = (Edge - (float)bounds.Width * scale) / 2;
        var top = (Edge - (float)bounds.Height * scale) / 2;

        using (var background = new SolidBrush(BoardColor(snapshot.Document.Viewport.BackgroundColor,
                   snapshot.Document.Viewport.WindowOpacity)))
            graphics.FillRectangle(background, 0, 0, Edge, Edge);

        var world = graphics.Save();
        graphics.TranslateTransform(left, top);
        graphics.ScaleTransform(scale, scale);
        graphics.TranslateTransform((float)-bounds.Left, (float)-bounds.Top);
        DrawGroupBackgrounds(graphics, elements, snapshot.Document.Groups);
        foreach (var element in elements)
        {
            try
            {
                if (element is BoardItem image) DrawImage(graphics, image, snapshot, snapshot.Document.Viewport);
                else if (element is BoardTextItem text) DrawText(graphics, text);
                else if (element is BoardDrawingItem drawing) DrawDrawing(graphics, drawing);
            }
            catch (Exception error) when (error is ArgumentException or IOException or OutOfMemoryException)
            {
                // A single unreadable visual must not prevent a portable scene from being saved.
            }
        }
        graphics.Restore(world);
        DrawBadge(graphics);

        using var encoded = new MemoryStream();
        bitmap.Save(encoded, ImageFormat.Png);
        return encoded.ToArray();
    }

    private static void DrawGroupBackgrounds(Graphics graphics, IReadOnlyList<BoardElement> elements,
        IReadOnlyList<BoardGroup> groups)
    {
        var map = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        int Depth(BoardGroup group)
        {
            var depth = 0;
            var current = group;
            while (current.ParentGroupId.Length > 0 && map.TryGetValue(current.ParentGroupId, out current!)) depth++;
            return depth;
        }
        foreach (var group in groups.OrderBy(group => BoardLayerTreeService.DescendantElements(group.Id, groups, elements)
                         .Select(element => element.ZIndex).DefaultIfEmpty(int.MaxValue).Min()).ThenBy(Depth))
        {
            if (!group.BackgroundVisible) continue;
            var rectangle = GroupRectangle(group.Id, groups, elements);
            if (rectangle.IsEmpty) continue;
            using var fill = new SolidBrush(BoardColor(group.BackgroundColor, 1));
            using var pen = new Pen(BoardColor(group.BorderColor, 1),
                (float)Math.Clamp(group.BorderThickness, 0, 10000));
            using var path = RoundedRectangle(rectangle, 9);
            graphics.FillPath(fill, path);
            if (pen.Width > 0) graphics.DrawPath(pen, path);
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        var arc = new RectangleF(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static RectangleF ContentBounds(IReadOnlyList<BoardElement> elements,
        IReadOnlyList<BoardGroup> groups, BoardViewport viewport)
    {
        if (elements.Count == 0)
        {
            var zoom = Math.Clamp(viewport.Zoom, .05, 8);
            return new RectangleF((float)(-viewport.PanX / zoom), (float)(-viewport.PanY / zoom),
                (float)Math.Max(320, viewport.WindowWidth / zoom),
                (float)Math.Max(240, viewport.WindowHeight / zoom));
        }
        var points = elements.SelectMany(RotatedCorners).ToList();
        foreach (var group in groups)
        {
            var rectangle = GroupRectangle(group.Id, groups, elements);
            if (rectangle.IsEmpty) continue;
            points.AddRange(new[] { new PointF(rectangle.Left, rectangle.Top), new PointF(rectangle.Right, rectangle.Top),
                new PointF(rectangle.Right, rectangle.Bottom), new PointF(rectangle.Left, rectangle.Bottom) });
        }
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        var width = Math.Max(16, right - left);
        var height = Math.Max(16, bottom - top);
        var padding = Math.Max(12, Math.Max(width, height) * .06);
        return new RectangleF((float)(left - padding), (float)(top - padding),
            (float)(width + padding * 2), (float)(height + padding * 2));
    }

    private static RectangleF GroupRectangle(string groupId, IReadOnlyList<BoardGroup> groups,
        IReadOnlyList<BoardElement> elements)
    {
        var group = groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null) return RectangleF.Empty;
        var points = elements.Where(element => element.GroupId == groupId).SelectMany(RotatedCorners).ToList();
        foreach (var child in groups.Where(candidate => candidate.ParentGroupId == groupId))
        {
            var rectangle = GroupRectangle(child.Id, groups, elements);
            if (rectangle.IsEmpty) continue;
            points.AddRange(new[] { new PointF(rectangle.Left, rectangle.Top), new PointF(rectangle.Right, rectangle.Top),
                new PointF(rectangle.Right, rectangle.Bottom), new PointF(rectangle.Left, rectangle.Bottom) });
        }
        if (points.Count == 0) return RectangleF.Empty;
        var padding = (float)Math.Clamp(group.FramePadding, 0, 10000);
        return RectangleF.FromLTRB(points.Min(point => point.X) - padding, points.Min(point => point.Y) - padding,
            points.Max(point => point.X) + padding, points.Max(point => point.Y) + padding);
    }

    private static IEnumerable<PointF> RotatedCorners(BoardElement item)
    {
        var centerX = item.X + item.Width / 2;
        var centerY = item.Y + item.Height / 2;
        var radians = item.Rotation * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        foreach (var point in new[]
        {
            new PointF(0, 0), new PointF((float)item.Width, 0),
            new PointF((float)item.Width, (float)item.Height), new PointF(0, (float)item.Height)
        })
        {
            var x = point.X - item.Width / 2;
            var y = point.Y - item.Height / 2;
            yield return new PointF((float)(centerX + x * cosine - y * sine),
                (float)(centerY + x * sine + y * cosine));
        }
    }

    private static GraphicsState BeginElement(Graphics graphics, BoardElement item)
    {
        var state = graphics.Save();
        graphics.TranslateTransform((float)(item.X + item.Width / 2), (float)(item.Y + item.Height / 2));
        graphics.RotateTransform((float)item.Rotation);
        graphics.TranslateTransform((float)-item.Width / 2, (float)-item.Height / 2);
        return state;
    }

    private static void DrawImage(Graphics graphics, BoardItem item, SceneSnapshot snapshot, BoardViewport viewport)
    {
        if (!snapshot.AssetPaths.TryGetValue(item.AssetId, out var path) || !File.Exists(path)) return;
        using var source = Image.FromFile(path);
        var state = BeginElement(graphics, item);
        try
        {
            var target = new RectangleF(0, 0, (float)item.Width, (float)item.Height);
            if (viewport.OpacityAffectsImages && viewport.WindowOpacity < .999)
            {
                using var attributes = new ImageAttributes();
                var matrix = new ColorMatrix { Matrix33 = (float)Math.Clamp(viewport.WindowOpacity, 0, 1) };
                attributes.SetColorMatrix(matrix);
                graphics.DrawImage(source, Rectangle.Round(target), 0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel, attributes);
            }
            else graphics.DrawImage(source, target);
        }
        finally { graphics.Restore(state); }
    }

    private static void DrawText(Graphics graphics, BoardTextItem item)
    {
        var state = BeginElement(graphics, item);
        try
        {
            using var background = new SolidBrush(BoardColor(item.BackgroundColor, 1));
            graphics.FillRectangle(background, 0, 0, (float)item.Width, (float)item.Height);
            var text = PlainText(item.DocumentData);
            if (string.IsNullOrWhiteSpace(text)) return;
            var fontSize = (float)Math.Clamp(item.Height * .18, 9, 28);
            using var font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(ContrastText(BoardColor(item.BackgroundColor, 1)));
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
            graphics.DrawString(text, font, brush,
                new RectangleF(5, 4, (float)Math.Max(1, item.Width - 10), (float)Math.Max(1, item.Height - 8)), format);
        }
        finally { graphics.Restore(state); }
    }

    private static string PlainText(string encoded)
    {
        try
        {
            var xml = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var document = XDocument.Parse(xml, LoadOptions.None);
            return string.Join(" ", document.DescendantNodes().OfType<XText>()
                .Select(node => node.Value.Trim()).Where(value => value.Length > 0));
        }
        catch { return string.Empty; }
    }

    private static void DrawDrawing(Graphics graphics, BoardDrawingItem item)
    {
        var state = BeginElement(graphics, item);
        try
        {
            foreach (var stroke in DrawingGroupService.Read(item))
            {
                var points = stroke.Points.Select(point =>
                    new PointF((float)(point.X * item.Width), (float)(point.Y * item.Height))).ToArray();
                if (points.Length == 0) continue;
                using var pen = new Pen(BoardColor(stroke.StrokeColor, stroke.StrokeOpacity),
                    (float)Math.Max(.5, stroke.StrokeThickness))
                {
                    StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
                    DashStyle = stroke.Dashed ? DashStyle.Dash : DashStyle.Solid
                };
                using var fill = new SolidBrush(BoardColor(stroke.FillColor, 1));
                if (stroke.Kind is BoardDrawingKind.Pen or BoardDrawingKind.Highlighter or
                    BoardDrawingKind.Line or BoardDrawingKind.Arrow or BoardDrawingKind.CurveArrow)
                {
                    if (points.Length == 1)
                    {
                        using var pointBrush = new SolidBrush(pen.Color);
                        graphics.FillEllipse(pointBrush, points[0].X - pen.Width / 2,
                            points[0].Y - pen.Width / 2, pen.Width, pen.Width);
                    }
                    else graphics.DrawLines(pen, points);
                    if (stroke.Kind is BoardDrawingKind.Arrow or BoardDrawingKind.CurveArrow)
                        DrawArrowHead(graphics, pen, points);
                }
                else if (points.Length >= 4 && stroke.Kind == BoardDrawingKind.Rectangle)
                {
                    graphics.FillPolygon(fill, points);
                    graphics.DrawPolygon(pen, points);
                }
                else if (points.Length >= 4 && stroke.Kind == BoardDrawingKind.Ellipse)
                {
                    var rectangle = RectangleF.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y),
                        points.Max(p => p.X), points.Max(p => p.Y));
                    graphics.FillEllipse(fill, rectangle);
                    graphics.DrawEllipse(pen, rectangle);
                }
            }
        }
        finally { graphics.Restore(state); }
    }

    private static void DrawArrowHead(Graphics graphics, Pen pen, IReadOnlyList<PointF> points)
    {
        if (points.Count < 2) return;
        var end = points[^1];
        var previous = points[^2];
        var angle = Math.Atan2(end.Y - previous.Y, end.X - previous.X);
        var length = Math.Max(10, pen.Width * 3.2);
        var left = new PointF((float)(end.X - Math.Cos(angle - .48) * length),
            (float)(end.Y - Math.Sin(angle - .48) * length));
        var right = new PointF((float)(end.X - Math.Cos(angle + .48) * length),
            (float)(end.Y - Math.Sin(angle + .48) * length));
        graphics.DrawLine(pen, end, left);
        graphics.DrawLine(pen, end, right);
    }

    private static DrawingColor BoardColor(string value, double opacity)
    {
        try
        {
            var text = value.Trim().TrimStart('#');
            var number = Convert.ToUInt32(text, 16);
            var color = text.Length == 8
                ? DrawingColor.FromArgb((byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number)
                : DrawingColor.FromArgb(255, (byte)(number >> 16), (byte)(number >> 8), (byte)number);
            return DrawingColor.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color);
        }
        catch { return DrawingColor.Transparent; }
    }

    private static DrawingColor ContrastText(DrawingColor background)
    {
        if (background.A < 80) return DrawingColor.White;
        var luminance = .2126 * background.R + .7152 * background.G + .0722 * background.B;
        return luminance > 150 ? DrawingColor.FromArgb(230, 24, 28, 31) : DrawingColor.White;
    }

    private static void DrawBadge(Graphics graphics)
    {
        const int iconEdge = 68;
        const int margin = 14;
        var x = Edge - iconEdge - margin;
        var y = Edge - iconEdge - margin;
        using var shadow = new SolidBrush(DrawingColor.FromArgb(100, 0, 0, 0));
        graphics.FillEllipse(shadow, x - 5, y - 5, iconEdge + 10, iconEdge + 10);
        try
        {
            var executable = Path.Combine(
                Path.GetDirectoryName(typeof(SceneThumbnailRenderer).Assembly.Location) ?? AppContext.BaseDirectory,
                "MuseBox.exe");
            using var icon = File.Exists(executable) ? Icon.ExtractAssociatedIcon(executable) : null;
            using var image = icon?.ToBitmap();
            if (image is not null) graphics.DrawImage(image, x, y, iconEdge, iconEdge);
            else DrawFallbackBadge(graphics, x, y, iconEdge);
        }
        catch { DrawFallbackBadge(graphics, x, y, iconEdge); }
    }

    private static void DrawFallbackBadge(Graphics graphics, int x, int y, int edge)
    {
        using var background = new SolidBrush(DrawingColor.FromArgb(255, 14, 100, 125));
        using var foreground = new SolidBrush(DrawingColor.FromArgb(255, 119, 243, 181));
        graphics.FillEllipse(background, x, y, edge, edge);
        graphics.FillPolygon(foreground, new[]
        {
            new PointF(x + edge * .5f, y + edge * .18f), new PointF(x + edge * .62f, y + edge * .42f),
            new PointF(x + edge * .82f, y + edge * .5f), new PointF(x + edge * .62f, y + edge * .58f),
            new PointF(x + edge * .5f, y + edge * .82f), new PointF(x + edge * .38f, y + edge * .58f),
            new PointF(x + edge * .18f, y + edge * .5f), new PointF(x + edge * .38f, y + edge * .42f)
        });
    }
}
