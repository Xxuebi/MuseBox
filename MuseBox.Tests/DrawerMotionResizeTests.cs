using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Size = System.Windows.Size;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static Point ImageAnchor(BoardItem item, double x, double y) =>
        BoardMath.RotatePoint(new Point(item.X + item.Width * x, item.Y + item.Height * y),
            new Point(item.X + item.Width / 2, item.Y + item.Height / 2), item.Rotation);

    private static void EqualPoint(Point expected, Point actual)
    {
        Equal(expected.X, actual.X, .00001);
        Equal(expected.Y, actual.Y, .00001);
    }

    private static Point RotateDelta(double dx, double dy, double angle) =>
        BoardMath.RotatePoint(new Point(dx, dy), new Point(), angle);

    private static void RotatedResizeAnchors()
    {
        foreach (var angle in new[] { 0d, 37, 45, 90, 135, 227, -37, 359 })
        foreach (var direction in Enum.GetValues<BoardResizeDirection>())
        foreach (var proportional in new[] { false, true })
        foreach (var centered in new[] { false, true })
        {
            var west = direction is BoardResizeDirection.West or BoardResizeDirection.NorthWest or BoardResizeDirection.SouthWest;
            var east = direction is BoardResizeDirection.East or BoardResizeDirection.NorthEast or BoardResizeDirection.SouthEast;
            var north = direction is BoardResizeDirection.North or BoardResizeDirection.NorthWest or BoardResizeDirection.NorthEast;
            var south = direction is BoardResizeDirection.South or BoardResizeDirection.SouthWest or BoardResizeDirection.SouthEast;
            var snapshot = new BoardItem { X = -120, Y = 80, Width = 140, Height = 320, Rotation = angle };
            // Side handles ignore tangential movement, including while Shift is down.
            var dx = west ? -35 : east ? 35 : 120;
            var dy = north ? -27 : south ? 27 : 90;
            var world = RotateDelta(dx, dy, angle);
            var result = BoardMath.ResizeRotatedFromSnapshot(snapshot, direction, world.X, world.Y, proportional, centered);
            var local = BoardMath.ResizeFromSnapshot(snapshot, direction, dx, dy, proportional, centered);
            Equal(local.Width, result.Width, .00001);
            Equal(local.Height, result.Height, .00001);
            Equal(angle, result.Rotation);
            var anchorX = centered ? .5 : west ? 1 : east ? 0 : .5;
            var anchorY = centered ? .5 : north ? 1 : south ? 0 : .5;
            EqualPoint(ImageAnchor(snapshot, anchorX, anchorY), ImageAnchor(result, anchorX, anchorY));
            Equal(-120d, snapshot.X);
            Equal(320d, snapshot.Height);
            if (proportional) Equal(snapshot.Width / snapshot.Height, result.Width / result.Height, .00001);
            if (!proportional && !centered)
            {
                var handleX = west ? 0 : east ? 1 : .5;
                var handleY = north ? 0 : south ? 1 : .5;
                var expectedDelta = RotateDelta(west || east ? dx : 0, north || south ? dy : 0, angle);
                EqualPoint(ImageAnchor(snapshot, handleX, handleY) + (Vector)expectedDelta,
                    ImageAnchor(result, handleX, handleY));
            }
        }
    }

    private static void RotatedResizeMinimum()
    {
        foreach (var angle in new[] { 45d, 90, 228 })
        foreach (var direction in Enum.GetValues<BoardResizeDirection>())
        {
            var west = direction is BoardResizeDirection.West or BoardResizeDirection.NorthWest or BoardResizeDirection.SouthWest;
            var east = direction is BoardResizeDirection.East or BoardResizeDirection.NorthEast or BoardResizeDirection.SouthEast;
            var north = direction is BoardResizeDirection.North or BoardResizeDirection.NorthWest or BoardResizeDirection.NorthEast;
            var south = direction is BoardResizeDirection.South or BoardResizeDirection.SouthWest or BoardResizeDirection.SouthEast;
            var snapshot = new BoardItem { X = 200, Y = 100, Width = 130, Height = 310, Rotation = angle };
            var delta = RotateDelta(west ? 10000 : east ? -10000 : 0, north ? 10000 : south ? -10000 : 0, angle);
            var result = BoardMath.ResizeRotatedFromSnapshot(snapshot, direction, delta.X, delta.Y, false, false);
            True(result.Width >= 40 && result.Height >= 40, "越过对边后尺寸小于限制");
            EqualPoint(ImageAnchor(snapshot, west ? 1 : east ? 0 : .5, north ? 1 : south ? 0 : .5),
                ImageAnchor(result, west ? 1 : east ? 0 : .5, north ? 1 : south ? 0 : .5));
            var centered = BoardMath.ResizeRotatedFromSnapshot(snapshot, direction, delta.X, delta.Y, false, true);
            EqualPoint(ImageAnchor(snapshot, .5, .5), ImageAnchor(centered, .5, .5));
        }
    }

    private static void RotatedResizeInteraction() => WithDrawingBoard((window, repository) =>
    {
        var item = AddEditableImage(window);
        item.X = 270; item.Y = 180; item.Width = 120; item.Height = 300; item.Rotation = 37;
        CallDrawing(window, "UpdateItemVisual", item);
        CallDrawing(window, "UpdateSelectionVisuals");
        ArrangeBoardSurface(window);
        var snapshot = item.Clone();
        SetDrawingField(window, "_viewZoom", 1.7d);
        var handle = ((Dictionary<BoardResizeDirection, Thumb>)typeof(BoardWindow).GetField("_resizeHandles", PrivateInstance)!.GetValue(window)!)[BoardResizeDirection.North];
        CallDrawing(window, "OnResizeStarted", handle, new DragStartedEventArgs(0, 0));
        var start = new Point(90, 70);
        SetDrawingField(window, "_resizeStartMouse", start);
        var delta = RotateDelta(0, -60, item.Rotation);
        CallDrawing(window, "ResizeSelectionFromPointer", BoardResizeDirection.North, start + (Vector)delta * 1.7, ModifierKeys.Shift);
        Equal(120d, item.Width, .00001); Equal(360d, item.Height, .00001);
        EqualPoint(ImageAnchor(snapshot, .5, 1), ImageAnchor(item, .5, 1));
        var transformed = item.Clone();
        AwaitImageUiTask(window, "CompleteResizeAsync");
        var saved = repository.GetItemsAsync("A").GetAwaiter().GetResult().Single();
        EqualPoint(ImageAnchor(transformed, .5, 1), ImageAnchor(saved, .5, 1));
        Equal(1, UndoCount(window));
        AwaitImageUiTask(window, "UndoAsync");
        Equal(300d, LiveImages(window).Single().Height, .00001);
        EqualPoint(ImageAnchor(snapshot, .5, .5), ImageAnchor(LiveImages(window).Single(), .5, .5));
        AwaitImageUiTask(window, "RedoAsync");
        Equal(360d, LiveImages(window).Single().Height, .00001);
        EqualPoint(ImageAnchor(transformed, .5, 1), ImageAnchor(LiveImages(window).Single(), .5, 1));
        SetDrawingField(window, "_viewZoom", 1d);
        CallDrawing(window, "ApplyViewportTransform");
        CallDrawing(window, "UpdateSelectionVisuals");
        SaveDrawingTestVisual((FrameworkElement)window.FindName("BoardSurface"), "rotated-shift-resize.png", false);
    });

    private static void DrawerEmbeddedMenu() => WithMainDrawerWindow((window, _) =>
    {
        var content = ArrangeMain(window, 360, 500);
        foreach (var root in MainDescendants(content).Where(x => x.Name == "DrawerRoot"))
        {
            var open = (Button)MainDescendants(root).Single(x => x.Name == "DrawerOpenButton");
            var menu = (Button)MainDescendants(root).Single(x => x.Name == "DrawerSettingsButton");
            var origin = menu.TranslatePoint(new Point(), open);
            True(origin.X > 0 && origin.X + menu.ActualWidth < open.ActualWidth, "菜单没有嵌入抽屉按钮内侧");
            True(origin.Y >= 0 && origin.Y + menu.ActualHeight <= open.ActualHeight, "菜单超出抽屉按钮");
            True(open.Padding.Right >= menu.ActualWidth, "名称未给菜单留出空间");
            True(!MainDescendants(menu).OfType<TextBlock>().Any(), "仍使用旧齿轮字体图标");
            Equal(2, MainDescendants(menu).OfType<System.Windows.Shapes.Path>().Count());
            // Overlay siblings share the same surface, without a nested Button
            // whose routed click would accidentally open the board.
            True(ReferenceEquals(open.Parent, menu.Parent), "抽屉菜单点击会冒泡到打开按钮");
        }
        SaveDrawingTestVisual(content, "drawer-embedded-menu.png", false);
    });

    private static void PumpDrawerAnimation(double milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static void DrawerSmoothReorder() => WithMainDrawerWindow((window, _) =>
    {
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        var content = ArrangeMain(window, 360, 500);
        var models = MainDrawers(window);
        var list = (ItemsControl)window.FindName("DrawerList");
        var scroll = (ScrollViewer)window.FindName("DrawerScroll");
        var overlay = (Canvas)window.FindName("DrawerDragLayer");
        var menu = MainDescendants((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0)).Single(x => x.Name == "DrawerSettingsButton");
        var grab = menu.TranslatePoint(new Point(menu.ActualWidth / 2, menu.ActualHeight / 2), scroll);
        var order = models.Select(x => x.Id).ToArray();
        MainCall(window, "StartDrawerDragVisual", "A", grab);
        var preview = overlay.Children.OfType<Border>().Single();
        var motion = (TranslateTransform)preview.RenderTransform;
        var initial = new Point(motion.X, motion.Y);
        typeof(MainWindow).GetField("_draggingDrawerId", PrivateInstance)!.SetValue(window, "A");
        typeof(MainWindow).GetField("_drawerOrderBeforeDrag", PrivateInstance)!.SetValue(window, order);
        models[0].IsDragging = true;
        MainCall(window, "UpdateDrawerDragVisual", grab + new Vector(18, 24));
        EqualPoint(initial + new Vector(18, 24), new Point(motion.X, motion.Y));
        var target = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(3);
        var targetPoint = (Point)MainCall(window, "DrawerLayoutOrigin", target)! +
            new Vector(target.ActualWidth / 2, target.ActualHeight / 2);
        MainCall(window, "UpdateDrawerReorderAt", targetPoint);
        Equal("B,C,D,A", string.Join(',', models.Select(x => x.Id)));
        var neighbour = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
        True(neighbour.RenderTransform is TranslateTransform { HasAnimatedProperties: true }, "相邻抽屉直接跳位");
        True(ReferenceEquals(preview, overlay.Children[0]), "拖动时反复生成预览");
        PumpDrawerAnimation(35);
        var neighbourMotion = (TranslateTransform)neighbour.RenderTransform;
        Console.WriteLine($"INFO  抽屉让位动画中间偏移：{neighbourMotion.X:F2}, {neighbourMotion.Y:F2}");
        SaveDrawingTestVisual(content, "drawer-drag-in-motion.png", false);
        // Repeated pointer events during animation should not swap back and forth.
        for (var i = 0; i < 8; i++) MainCall(window, "UpdateDrawerReorderAt", targetPoint);
        Equal("B,C,D,A", string.Join(',', models.Select(x => x.Id)));
        AwaitMainTask(window, "FinishDrawerReorderAsync", false);
        Equal("A,B,C,D", string.Join(',', models.Select(x => x.Id)));
        Equal(0, overlay.Children.Count);
        True(models.All(x => !x.IsDragging), "取消后留下拖动透明度");
        PumpDrawerAnimation(240);
        foreach (var model in models)
        {
            var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(model);
            Equal(0d, ((TranslateTransform)container.RenderTransform).X, .0001);
            Equal(0d, ((TranslateTransform)container.RenderTransform).Y, .0001);
        }
    });
}
