using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object? CallDrawing(BoardWindow window, string method, params object[] arguments) =>
        typeof(BoardWindow).GetMethod(method, PrivateInstance | BindingFlags.Public)!.Invoke(window, arguments);

    private static void AwaitDrawing(BoardWindow window, string method, params object[] arguments) =>
        ((Task)CallDrawing(window, method, arguments)!).GetAwaiter().GetResult();

    private static List<BoardDrawingItem> LiveDrawings(BoardWindow window) =>
        (List<BoardDrawingItem>)typeof(BoardWindow).GetField("_drawingItems", PrivateInstance)!.GetValue(window)!;

    private static void SetDrawingField(BoardWindow window, string field, object value) =>
        typeof(BoardWindow).GetField(field, PrivateInstance)!.SetValue(window, value);

    private static BoardDrawingItem SampleDrawing(BoardDrawingKind kind = BoardDrawingKind.Pen) => new()
    {
        Id = Guid.NewGuid().ToString("N"), DrawerId = "A", Kind = kind,
        X = 10, Y = 20, Width = 200, Height = 100, StrokeThickness = 4,
        StrokeColor = "#FF3284AA", FillColor = "#44224466", StrokeOpacity = .7,
        PointsJson = JsonSerializer.Serialize(new[]
        {
            new BoardStrokePoint(.1, .2, .4), new BoardStrokePoint(.9, .8, .9)
        })
    };

    private static void AssertSameWorld(IReadOnlyList<BoardDrawingStroke> expected,
        IReadOnlyList<BoardDrawingStroke> actual)
    {
        True(actual.Count >= expected.Count, "续画丢失了已有笔迹");
        for (var i = 0; i < expected.Count; i++)
        {
            Equal(expected[i].Kind, actual[i].Kind);
            Equal(expected[i].StrokeColor, actual[i].StrokeColor);
            Equal(expected[i].StrokeOpacity, actual[i].StrokeOpacity);
            Equal(expected[i].Points.Count, actual[i].Points.Count);
            for (var j = 0; j < expected[i].Points.Count; j++)
            {
                Equal(expected[i].Points[j].X, actual[i].Points[j].X, .00001);
                Equal(expected[i].Points[j].Y, actual[i].Points[j].Y, .00001);
                Equal(expected[i].Points[j].Pressure, actual[i].Points[j].Pressure, .00001);
            }
        }
    }

    private static void DrawingGroupTransforms()
    {
        foreach (var kind in new[] { BoardDrawingKind.Pen, BoardDrawingKind.Highlighter,
                     BoardDrawingKind.Line, BoardDrawingKind.Arrow, BoardDrawingKind.Rectangle, BoardDrawingKind.Ellipse })
        {
            var legacy = SampleDrawing(kind);
            legacy.Rotation = 31;
            var expected = DrawingGroupService.ToWorld(legacy);
            var group = new BoardDrawingItem { Id = "group", DrawerId = "A", Kind = BoardDrawingKind.Group };
            DrawingGroupService.Append(group, legacy);
            Equal(BoardDrawingKind.Group, group.Kind);
            Equal("group", group.Id);
            AssertSameWorld(expected, DrawingGroupService.ToWorld(group));
            if (kind is BoardDrawingKind.Rectangle or BoardDrawingKind.Ellipse)
                Equal(4, DrawingGroupService.Read(group).Single().Points.Count);

            group.X += 70;
            group.Y -= 35;
            group.Width *= 1.6;
            group.Height *= .7;
            group.Rotation = 63;
            var transformed = DrawingGroupService.ToWorld(group);
            var next = SampleDrawing();
            next.X = -250;
            next.StrokeColor = "#CCEF2244";
            next.StrokeOpacity = .4;
            DrawingGroupService.Append(group, next);
            Equal(2, DrawingGroupService.Read(group).Count);
            AssertSameWorld(transformed, DrawingGroupService.ToWorld(group));
            Equal("#CCEF2244", DrawingGroupService.Read(group)[1].StrokeColor);
            Equal(0d, group.Rotation);
            Equal("group", group.Id);
        }
    }

    private static void WithDrawingBoard(Action<BoardWindow, BoardRepository> test)
    {
        var directory = CreateTempDirectory();
        BoardWindow? window = null;
        try
        {
            var paths = new AppDataPaths(directory);
            var repository = new BoardRepository(paths);
            repository.InitializeAsync().GetAwaiter().GetResult();
            var imports = new BoardImportService(new AssetLibraryService(paths, repository), repository);
            window = new BoardWindow("A", repository, imports);
            window.ReloadAsync().GetAwaiter().GetResult();
            window.Measure(new Size(800, 600));
            window.Arrange(new Rect(0, 0, 800, 600));
            window.UpdateLayout();
            test(window, repository);
        }
        finally
        {
            if (window is not null)
            {
                AwaitDrawing(window, "FlushPendingDrawingAsync");
                CallDrawing(window, "CloseDrawingPopups");
                SetDrawingField(window, "_closeAfterDrawingSave", true);
                window.Close();
            }
            // Flush deferred native finalizers on the first retry, then allow a
            // bounded release interval. Failure to clean up still fails the test.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(directory, true); break; }
                catch (IOException) when (attempt < 40)
                {
                    if (attempt == 0)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                    Thread.Sleep(50);
                }
            }
        }
    }

    private static void Stroke(BoardWindow window, Point from, Point to)
    {
        CallDrawing(window, "StartDrawing", from, .5d);
        CallDrawing(window, "UpdateDrawing", to, .9d);
        AwaitDrawing(window, "CompleteDrawingAsync");
    }

    private static void DrawingSessionLifecycle() => WithDrawingBoard((window, repository) =>
    {
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        Stroke(window, new Point(10, 20), new Point(210, 20));
        SetDrawingField(window, "_drawingStrokeColor", "#FFFF0000");
        ((Slider)window.FindName("DrawingThicknessSlider")).Value = 9;
        Stroke(window, new Point(10, 60), new Point(210, 60));
        var first = LiveDrawings(window).Single();
        var firstId = first.Id;
        Equal(2, DrawingGroupService.Read(first).Count);
        Equal(4d, DrawingGroupService.Read(first)[0].StrokeThickness);
        Equal(9d, DrawingGroupService.Read(first)[1].StrokeThickness);
        Equal(1, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Count);

        // Tool changes inside the session must not split the group.
        CallDrawing(window, "SetToolMode", BoardToolMode.Rectangle);
        Stroke(window, new Point(240, 20), new Point(240, 20));
        Equal(2, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        Stroke(window, new Point(240, 20), new Point(290, 90));
        Equal(3, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        AwaitDrawing(window, "UndoAsync");
        Equal(2, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        AwaitDrawing(window, "RedoAsync");
        Equal(3, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);

        // Explicitly leaving drawing ends a session, even when the next stroke overlaps it.
        CallDrawing(window, "SetToolMode", BoardToolMode.Select);
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        Stroke(window, new Point(10, 20), new Point(210, 20));
        Equal(2, LiveDrawings(window).Count);
        var secondId = LiveDrawings(window).Single(item => item.Id != firstId).Id;

        // Double-click enters this same continuation routine, using its transformed world geometry.
        CallDrawing(window, "SetToolMode", BoardToolMode.Select);
        first = LiveDrawings(window).Single(item => item.Id == firstId);
        first.X += 50;
        first.Width *= 1.5;
        first.Rotation = 24;
        var beforeResume = DrawingGroupService.ToWorld(first);
        var doubleClick = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(doubleClick, 2);
        CallDrawing(window, "OnItemMouseDown", first, doubleClick);
        True(doubleClick.Handled, "双击笔迹没有进入续画模式");
        Equal(Visibility.Visible, ((Border)window.FindName("DrawingPalette")).Visibility);
        Equal(2, LiveDrawings(window).Count);
        Stroke(window, new Point(320, 70), new Point(390, 90));
        Equal(2, LiveDrawings(window).Count);
        first = LiveDrawings(window).Single(item => item.Id == firstId);
        Equal(4, DrawingGroupService.Read(first).Count);
        AssertSameWorld(beforeResume, DrawingGroupService.ToWorld(first));
        Equal(1, DrawingGroupService.Read(LiveDrawings(window).Single(item => item.Id == secondId)).Count);
        AwaitDrawing(window, "UndoAsync");
        Equal(3, DrawingGroupService.Read(LiveDrawings(window).Single(item => item.Id == firstId)).Count);
        AwaitDrawing(window, "RedoAsync");
        Equal(4, DrawingGroupService.Read(LiveDrawings(window).Single(item => item.Id == firstId)).Count);

        // Closing the tool with a pointer gesture still in progress commits its last stroke.
        CallDrawing(window, "StartDrawing", new Point(500, 50), 1d);
        CallDrawing(window, "UpdateDrawing", new Point(580, 80), 1d);
        CallDrawing(window, "SetToolMode", BoardToolMode.Select);
        AwaitDrawing(window, "FlushPendingDrawingAsync");
        Equal(5, DrawingGroupService.Read(LiveDrawings(window).Single(item => item.Id == firstId)).Count);
        window.ReloadAsync().GetAwaiter().GetResult();
        Equal(2, LiveDrawings(window).Count);
        Equal(5, DrawingGroupService.Read(LiveDrawings(window).Single(item => item.Id == firstId)).Count);
        var saved = repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult();
        Equal(2, saved.Count);
        Equal(5, DrawingGroupService.Read(saved.Single(item => item.Id == firstId)).Count);

        // Window-close flush has the same guarantee without first changing tools.
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        CallDrawing(window, "StartDrawing", new Point(600, 50), 1d);
        CallDrawing(window, "UpdateDrawing", new Point(650, 80), 1d);
        AwaitDrawing(window, "FlushPendingDrawingAsync");
        Equal(3, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Count);
    });

    private static void DrawingGroupErasing() => WithDrawingBoard((window, repository) =>
    {
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        Stroke(window, new Point(0, 50), new Point(200, 50));
        Stroke(window, new Point(0, 150), new Point(200, 150));
        var id = LiveDrawings(window).Single().Id;
        CallDrawing(window, "SetToolMode", BoardToolMode.Rectangle);
        Stroke(window, new Point(300, 20), new Point(360, 90));
        CallDrawing(window, "SetToolMode", BoardToolMode.Eraser);
        Stroke(window, new Point(100, 0), new Point(100, 100));
        Equal(1, LiveDrawings(window).Count);
        Equal(id, LiveDrawings(window).Single().Id);
        var split = DrawingGroupService.ToWorld(LiveDrawings(window).Single());
        Equal(4, split.Count); // two halves + untouched pen + rectangle
        True(split.Take(2).SelectMany(part => part.Points).All(p => Math.Abs(p.X - 100) > 7),
            "局部擦除没有从原笔画中切出间隙");
        Stroke(window, new Point(300, 45), new Point(300, 70));
        Equal(3, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        True(DrawingGroupService.Read(LiveDrawings(window).Single()).All(s => s.Kind == BoardDrawingKind.Pen),
            "擦除形状没有只移除组内被命中的对象");
        AwaitDrawing(window, "UndoAsync");
        Equal(4, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        AwaitDrawing(window, "UndoAsync");
        Equal(3, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        Equal(id, LiveDrawings(window).Single().Id);
        Equal(1, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Count);
    });

    private static void DrawingToolbarLayout() => WithDrawingBoard((window, _) =>
    {
        Equal("1.1.15", typeof(BoardWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        True(!Enum.GetNames<BoardToolMode>().Contains("Highlighter"), "仍然有第二种笔工具");
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        var palette = (Border)window.FindName("DrawingPalette");
        Equal(new CornerRadius(13), palette.CornerRadius);
        palette.Measure(new Size(700, 200));
        palette.Arrange(new Rect(palette.DesiredSize));
        True(palette.ActualWidth <= 380, "绘制工具栏不够紧凑");
        foreach (var name in new[] { "DrawingShapesPopup", "DrawingSettingsPopup" })
        {
            var popup = (Popup)window.FindName(name);
            Equal(PlacementMode.Top, popup.Placement);
            True(popup.AllowsTransparency && popup.StaysOpen, "绘制小菜单没有使用受控开关，按钮可能重复打开菜单");
        }
        var shapes = (Popup)window.FindName("DrawingShapesPopup");
        var shapeButtons = ((StackPanel)((Border)((StackPanel)((Border)shapes.Child).Child).Children[0]).Child)
            .Children.OfType<Button>().ToArray();
        Equal(4, shapeButtons.Length);
        True(shapeButtons.All(button => button.Tag is "Line" or "Arrow" or "Rectangle" or "Ellipse"),
            "形状没有收纳在同一小菜单");
        SetDrawingField(window, "_drawingStrokeColor", "#FFEF3344");
        SetDrawingField(window, "_drawingFillColor", "#883399EE");
        CallDrawing(window, "UpdateDrawingToolbarState");
        Equal(Color.FromArgb(255, 239, 51, 68),
            ((SolidColorBrush)((Border)window.FindName("DrawingStrokePreview")).Background).Color);
        Equal(VerticalAlignment.Bottom, ((Border)window.FindName("DrawingFillPreview")).VerticalAlignment);
        SaveDrawingTestVisual(palette, "drawing-toolbar.png");
        SaveDrawingTestVisual((FrameworkElement)shapes.Child, "drawing-shapes-menu.png");
        SaveDrawingTestVisual((FrameworkElement)((Popup)window.FindName("DrawingSettingsPopup")).Child,
            "drawing-line-menu.png");
    });

    private static void DrawingGroupRendering()
    {
        var group = new BoardDrawingItem { Kind = BoardDrawingKind.Group };
        var parts = Enumerable.Range(0, 300).Select(i => new BoardDrawingStroke
        {
            Kind = BoardDrawingKind.Pen, StrokeThickness = 2, StrokeOpacity = .65,
            Points = new() { new(i % 30 * 8, i / 30 * 10), new(i % 30 * 8 + 6, i / 30 * 10 + 7, .4) }
        }).ToList();
        DrawingGroupService.SetWorldStrokes(group, parts);
        DrawingGroupService.Append(group, SampleDrawing(BoardDrawingKind.Ellipse));
        DrawingGroupService.Append(group, SampleDrawing(BoardDrawingKind.Rectangle));
        var visual = new BoardDrawingVisual { Width = group.Width, Height = group.Height, Item = group };
        SaveDrawingTestVisual(visual, "drawing-group.png");
        var cacheField = typeof(BoardDrawingVisual).GetField("_cachedDrawing", PrivateInstance)!;
        var cache = cacheField.GetValue(visual) as DrawingGroup;
        True(cache is { IsFrozen: true }, "笔迹没有冻结并缓存矢量渲染");
        visual.InvalidateVisual();
        SaveDrawingTestVisual(visual, "drawing-group.png");
        True(ReferenceEquals(cache, cacheField.GetValue(visual)), "没有修改笔迹时仍重复生成几何");
        visual.Width *= 1.5;
        SaveDrawingTestVisual(visual, "drawing-group-resized.png");
        True(!ReferenceEquals(cache, cacheField.GetValue(visual)), "缩放后没有刷新矢量几何");
    }

    private static void SaveDrawingTestVisual(FrameworkElement visual, string filename, bool measure = true)
    {
        if (measure)
        {
            visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            visual.Arrange(new Rect(new Point(-visual.Margin.Left, -visual.Margin.Top), visual.DesiredSize));
        }
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * 2)),
            Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * 2)), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, filename));
        encoder.Save(stream);
    }
}
