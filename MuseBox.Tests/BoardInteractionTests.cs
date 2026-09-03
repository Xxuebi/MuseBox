using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Size = System.Windows.Size;
using Cursors = System.Windows.Input.Cursors;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static HashSet<string> BoardSelection(BoardWindow window) =>
        (HashSet<string>)typeof(BoardWindow).GetField("_selected", PrivateInstance)!.GetValue(window)!;

    private static Border DrawingBorder(BoardWindow window, string id) =>
        ((Canvas)window.FindName("WorldCanvas")).Children.OfType<Border>().Single(border => Equals(border.Tag, id));

    private static bool HasFrame(Border border) => border.BorderBrush is SolidColorBrush brush && brush.Color.A > 0;

    private static void ClickBoardItem(BoardWindow window, BoardElement item, int count)
    {
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(click, count);
        CallDrawing(window, "OnItemMouseDown", item, click);
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.MouseUpEvent };
        CallDrawing(window, "OnSurfaceMouseUp", window.FindName("BoardSurface"), up);
    }

    private static Grid ArrangeBoardSurface(BoardWindow window, double width = 800, double height = 600)
    {
        var surface = (Grid)window.FindName("BoardSurface");
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        return surface;
    }

    private static void DrawingSelectionFrames() => WithDrawingBoard((window, _) =>
    {
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        Stroke(window, new Point(40, 40), new Point(160, 90));
        var group = LiveDrawings(window).Single();
        Equal(0, BoardSelection(window).Count);
        True(!HasFrame(DrawingBorder(window, group.Id)), "抬笔后自动出现了边框");
        Stroke(window, new Point(50, 90), new Point(140, 40));
        CallDrawing(window, "SetToolMode", BoardToolMode.Select);
        True(!HasFrame(DrawingBorder(window, group.Id)), "关闭绘制后自动选中了笔迹");
        ClickBoardItem(window, group, 1);
        True(HasFrame(DrawingBorder(window, group.Id)), "单击笔迹没有显示选择框");
        ClickBoardItem(window, group, 2);
        True(HasFrame(DrawingBorder(window, group.Id)), "双击笔迹没有显示续画对象的边框");
        CallDrawing(window, "StartDrawing", new Point(200, 100), 1d);
        CallDrawing(window, "UpdateDrawing", new Point(220, 130), 1d);
        True(!HasFrame(DrawingBorder(window, group.Id)), "续画时没有隐藏已有笔迹边框");
        Equal(0, BoardSelection(window).Count);
        AwaitDrawing(window, "CompleteDrawingAsync");
        CallDrawing(window, "SetToolMode", BoardToolMode.Select);
        True(!HasFrame(DrawingBorder(window, group.Id)), "续画完成后仍出现边框");
        Equal(3, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        ClickBoardItem(window, LiveDrawings(window).Single(), 1);
        True(HasFrame(DrawingBorder(window, group.Id)), "绘制后无法重新选择整个分组");
    });

    private static void EraserSizeAndCursor() => WithDrawingBoard((window, _) =>
    {
        ArrangeBoardSurface(window);
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        Stroke(window, new Point(100, 100), new Point(100, 100));
        Stroke(window, new Point(200, 100), new Point(200, 100));
        CallDrawing(window, "SetToolMode", BoardToolMode.Eraser);
        var size = (Slider)window.FindName("EraserDiameterSlider");
        var penSize = (Slider)window.FindName("DrawingThicknessSlider");
        var cursor = (Grid)window.FindName("EraserCursorOverlay");
        Equal(8d, size.Minimum);
        Equal(160d, size.Maximum);
        size.Value = 16;
        CallDrawing(window, "UpdateEraserCursor", new Point(120, 100), true);
        Equal(Visibility.Visible, cursor.Visibility);
        Equal(16d, cursor.Width);
        Equal(112d, Canvas.GetLeft(cursor));
        Equal(92d, Canvas.GetTop(cursor));
        True(!cursor.IsHitTestVisible, "橡皮擦范围覆盖层会阻挡画板输入");
        var query = new QueryCursorEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.QueryCursorEvent };
        DrawingBorder(window, LiveDrawings(window).Single().Id).RaiseEvent(query);
        Equal(Cursors.None, query.Cursor);
        True(query.Handled, "笔迹自己的鼠标样式盖过了橡皮擦范围指示");
        SaveDrawingTestVisual((Grid)window.FindName("BoardSurface"), "drawing-eraser-cursor.png", measure: false);
        penSize.Value = 48;
        Equal(16d, size.Value);
        Stroke(window, new Point(120, 100), new Point(120, 100));
        Equal(2, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        size.Value = 64;
        Equal(64d, cursor.Width);
        Equal("64", ((System.Windows.Controls.TextBox)window.FindName("EraserDiameterText")).Text);
        Equal(48d, penSize.Value);

        SetDrawingField(window, "_viewZoom", 2d);
        Stroke(window, new Point(120, 100), new Point(120, 100));
        Equal(2, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        Equal(64d, cursor.Width);
        SetDrawingField(window, "_viewZoom", .5d);
        Stroke(window, new Point(120, 100), new Point(120, 100));
        Equal(1, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        AwaitDrawing(window, "UndoAsync");
        Equal(2, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);

        // A zoom during the gesture scales the ring along with its captured world radius.
        SetDrawingField(window, "_viewZoom", 1d);
        CallDrawing(window, "StartDrawing", new Point(500, 400), 1d);
        SetDrawingField(window, "_viewZoom", 2d);
        CallDrawing(window, "RefreshEraserCursor");
        Equal(128d, cursor.Width);
        AwaitDrawing(window, "CompleteDrawingAsync");
        Equal(64d, cursor.Width);

        CallDrawing(window, "UpdateEraserCursor", new Point(120, 100), false);
        Equal(Visibility.Collapsed, cursor.Visibility);
        CallDrawing(window, "UpdateEraserCursor", new Point(-10, 100), true);
        Equal(Visibility.Collapsed, cursor.Visibility);
        CallDrawing(window, "UpdateEraserCursor", new Point(120, 100), true);
        SetDrawingField(window, "_spaceDown", true);
        CallDrawing(window, "RefreshEraserCursor");
        Equal(Visibility.Collapsed, cursor.Visibility);
        SetDrawingField(window, "_spaceDown", false);
        CallDrawing(window, "RefreshEraserCursor");
        Equal(Visibility.Visible, cursor.Visibility);
        CallDrawing(window, "SetToolMode", BoardToolMode.Select);
        Equal(Visibility.Collapsed, cursor.Visibility);

        var icon = (Grid)window.FindName("DrawingLineStyleIcon");
        Equal(3, icon.Children.Count);
        var lines = icon.Children.OfType<System.Windows.Shapes.Path>().ToArray();
        True(lines.Select(line => line.StrokeThickness).SequenceEqual(new[] { 1d, 2d, 3d }),
            "线条菜单图标没有使用粗细递增的横线");
        SaveDrawingTestVisual((FrameworkElement)((System.Windows.Controls.Primitives.Popup)
            window.FindName("DrawingEraserPopup")).Child, "drawing-eraser-menu.png");
    });

    private static void TextToolbarPositioning() => WithDrawingBoard((window, repository) =>
    {
        var document = RichTextDocumentService.CreateDefault();
        document.Blocks.Clear();
        document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("文字工具栏定位测试")));
        var note = new BoardTextItem
        {
            Id = "lower-note", DrawerId = "A", X = 390, Y = 460, Width = 180, Height = 36,
            DocumentData = RichTextDocumentService.Save(document)
        };
        repository.AddTextItemsAsync(new[] { note }).GetAwaiter().GetResult();
        window.ReloadAsync().GetAwaiter().GetResult();
        var surface = ArrangeBoardSurface(window);
        BoardSelection(window).Add(note.Id);
        CallDrawing(window, "UpdateSelectionVisuals");
        var palette = (Border)window.FindName("TextPalette");
        var offset = (TranslateTransform)window.FindName("TextPaletteTranslate");
        Equal(Visibility.Visible, palette.Visibility);
        var left = offset.X;
        var top = offset.Y;
        True(top > 350 && top < note.Y, "文字工具栏没有紧贴所选注释上方");
        Equal(note.Y - palette.DesiredSize.Height - 12, top, .001);
        for (var i = 0; i < 30; i++)
        {
            CallDrawing(window, "UpdateSelectionVisuals");
            surface.UpdateLayout();
            CallDrawing(window, "PositionTextPalette");
            Equal(left, offset.X, .001);
            Equal(top, offset.Y, .001);
            Equal(new Thickness(0), palette.Margin);
        }
        SaveDrawingTestVisual(surface, "text-toolbar-position.png", measure: false);
        var live = ((List<BoardTextItem>)typeof(BoardWindow).GetField("_textItems", PrivateInstance)!
            .GetValue(window)!).Single();
        live.X = 730;
        live.Y = 4;
        CallDrawing(window, "PositionTextPalette");
        Equal(live.Y + live.Height + 12, offset.Y, .001);
        True(offset.X + palette.DesiredSize.Width <= surface.ActualWidth - 8.001 + .01,
            "文字工具栏超出了窗口右边缘");

        live.X = 300;
        live.Y = 300;
        live.Rotation = 90;
        CallDrawing(window, "PositionTextPalette");
        Equal(live.Y + live.Height / 2 - live.Width / 2 - palette.DesiredSize.Height - 12, offset.Y, .001);
        SetDrawingField(window, "_viewZoom", 1.5d);
        SetDrawingField(window, "_viewPanX", -60d);
        SetDrawingField(window, "_viewPanY", 40d);
        CallDrawing(window, "PositionTextPalette");
        Equal((live.Y + live.Height / 2 - live.Width / 2) * 1.5 + 40 - palette.DesiredSize.Height - 12,
            offset.Y, .001);

        live.Y = 1000;
        CallDrawing(window, "PositionTextPalette");
        True(offset.Y + palette.DesiredSize.Height <= surface.ActualHeight - 8 + .001,
            "文字工具栏超出了窗口底边");
        ArrangeBoardSurface(window, 700, 400);
        True(offset.Y + palette.DesiredSize.Height <= 400 - 8 + .001,
            "窗口缩小后文字工具栏没有重新定位");
        BoardSelection(window).Clear();
        CallDrawing(window, "UpdateSelectionVisuals");
        Equal(Visibility.Collapsed, palette.Visibility);
    });
}
