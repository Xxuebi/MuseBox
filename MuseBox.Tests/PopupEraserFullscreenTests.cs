using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static int UndoCount(BoardWindow window) =>
        (int)typeof(BoardWindow).GetField("_undo", PrivateInstance)!.GetValue(window)!.GetType()
            .GetProperty("Count")!.GetValue(typeof(BoardWindow).GetField("_undo", PrivateInstance)!.GetValue(window))!;

    private static void AddPopupTestNote(BoardWindow window, BoardRepository repository)
    {
        repository.AddTextItemsAsync(new[] { new BoardTextItem
        {
            Id = "popup-note", DrawerId = "A", X = 350, Y = 300, Width = 180, Height = 40,
            DocumentData = RichTextDocumentService.Save(RichTextDocumentService.CreateDefault())
        } }).GetAwaiter().GetResult();
        window.ReloadAsync().GetAwaiter().GetResult();
        BoardSelection(window).Add("popup-note");
        CallDrawing(window, "UpdateSelectionVisuals");
        ArrangeBoardSurface(window);
    }

    private static void PressPopupOpener(BoardWindow window, string buttonName)
    {
        var button = (Button)window.FindName(buttonName);
        var source = button.Content as DependencyObject ?? button;
        CallDrawing(window, "DismissToolPopupsOutside", source);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void ToolPopupToggling() => WithDrawingBoard((window, repository) =>
    {
        // Use a real, invisible owner so WPF does not defer IsOpen until Loaded.
        // Skip production startup here: the fixture already loaded its temporary database.
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(BoardWindow).GetMethod("OnLoaded", PrivateInstance | System.Reflection.BindingFlags.DeclaredOnly)!);
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        AddPopupTestNote(window, repository);
        var pairs = new[]
        {
            ("TextMoreButton", "TextMorePopup"),
            ("DrawingShapesButton", "DrawingShapesPopup"),
            ("DrawingSettingsButton", "DrawingSettingsPopup"),
            ("DrawingEraserSizeButton", "DrawingEraserPopup")
        };
        foreach (var (buttonName, popupName) in pairs)
        {
            if (popupName != "TextMorePopup") CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
            ArrangeBoardSurface(window);
            var popup = (Popup)window.FindName(popupName);
            ((FrameworkElement)popup.Child).Opacity = 0;
            True(popup.StaysOpen, "弹窗仍会在鼠标按下时被系统提前关闭");
            for (var i = 0; i < 3; i++)
            {
                if (popupName == "TextMorePopup" && i == 2)
                    ((TranslateTransform)window.FindName("TextPaletteTranslate")).Y = 530;
                PressPopupOpener(window, buttonName);
                True(popup.IsOpen, $"{popupName} 第一次点击未打开");
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var arrowName = popupName switch
                {
                    "TextMorePopup" => ((FrameworkElement)window.FindName("TextMoreArrowUp")).Visibility == Visibility.Visible
                        ? "TextMoreArrowUp" : "TextMoreArrowDown",
                    "DrawingShapesPopup" => "DrawingShapesArrow",
                    "DrawingSettingsPopup" => "DrawingSettingsArrow",
                    _ => "DrawingEraserArrow"
                };
                var arrow = (FrameworkElement)window.FindName(arrowName);
                var opener = (FrameworkElement)popup.PlacementTarget;
                var tipX = arrow.PointToScreen(new Point(arrow.ActualWidth / 2, 0)).X;
                var openerX = opener.PointToScreen(new Point(opener.ActualWidth / 2, 0)).X;
                True(Math.Abs(tipX - openerX) <= 1, $"{popupName} 的尖角偏移了 {tipX-openerX} 屏幕像素");
                CallDrawing(window, "DismissToolPopupsOutside", popup.Child);
                True(popup.IsOpen, "点击弹窗内部错误关闭了弹窗");
                PressPopupOpener(window, buttonName);
                True(!popup.IsOpen, $"{popupName} 再次点击没有关闭");
            }
            PressPopupOpener(window, buttonName);
            CallDrawing(window, "DismissToolPopupsOutside", window.FindName("BoardSurface"));
            True(!popup.IsOpen, "点击画板空白处没有关闭小菜单");
        }
        PressPopupOpener(window, "DrawingShapesButton");
        PressPopupOpener(window, "DrawingSettingsButton");
        True(!((Popup)window.FindName("DrawingShapesPopup")).IsOpen &&
             ((Popup)window.FindName("DrawingSettingsPopup")).IsOpen, "不同小菜单未保持互斥");
        CallDrawing(window, "CloseToolPopups");
        True(!((Popup)window.FindName("DrawingSettingsPopup")).IsOpen, "退出或失去激活时没有关闭菜单");
    });

    private static void ToolPopupSpacing() => WithDrawingBoard((window, repository) =>
    {
        AddPopupTestNote(window, repository);
        var textPopup = (Popup)window.FindName("TextMorePopup");
        CallDrawing(window, "PositionToolPopup", textPopup);
        Equal(PlacementMode.Bottom, textPopup.Placement);
        Equal(Visibility.Visible, ((FrameworkElement)window.FindName("TextMoreArrowUp")).Visibility);
        AssertPopupClearance(window, textPopup, (Border)window.FindName("TextPalette"));
        SavePopupPair(textPopup, (Border)window.FindName("TextPalette"), "text-popup-spacing.png");
        var translation = (TranslateTransform)window.FindName("TextPaletteTranslate");
        translation.Y = 530;
        CallDrawing(window, "PositionToolPopup", textPopup);
        Equal(PlacementMode.Top, textPopup.Placement);
        Equal(Visibility.Visible, ((FrameworkElement)window.FindName("TextMoreArrowDown")).Visibility);
        Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("TextMoreArrowUp")).Visibility);
        AssertPopupClearance(window, textPopup, (Border)window.FindName("TextPalette"));

        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        ArrangeBoardSurface(window);
        foreach (var name in new[] { "DrawingShapesPopup", "DrawingEraserPopup", "DrawingSettingsPopup" })
        {
            var popup = (Popup)window.FindName(name);
            CallDrawing(window, "PositionToolPopup", popup);
            Equal(PlacementMode.Top, popup.Placement);
            AssertPopupClearance(window, popup, (Border)window.FindName("DrawingPalette"));
            SavePopupPair(popup, (Border)window.FindName("DrawingPalette"), name + "-spacing.png");
        }
    });

    private static void AssertPopupClearance(BoardWindow window, Popup popup, Border palette)
    {
        var target = (FrameworkElement)popup.PlacementTarget;
        var top = target.TranslatePoint(new Point(), palette).Y;
        if (popup.Placement == PlacementMode.Top)
            True(top + popup.VerticalOffset <= -6, "弹窗底部仍压在工具栏上，尖角会被遮住");
        else
            True(top + target.ActualHeight + popup.VerticalOffset >= palette.ActualHeight + 6,
                "文字菜单顶部没有离开工具栏");
        Equal((target.ActualWidth - popup.Child.DesiredSize.Width) / 2, popup.HorizontalOffset, .001);
    }

    private static void SavePopupPair(Popup popup, Border palette, string filename)
    {
        var child = (FrameworkElement)popup.Child;
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        child.Arrange(new Rect(child.DesiredSize));
        child.UpdateLayout();
        var target = (FrameworkElement)popup.PlacementTarget;
        var targetPosition = target.TranslatePoint(new Point(), palette);
        var popupX = targetPosition.X + popup.HorizontalOffset + child.Margin.Left;
        var popupY = popup.Placement == PlacementMode.Top
            ? targetPosition.Y + popup.VerticalOffset - child.DesiredSize.Height + child.Margin.Top
            : targetPosition.Y + target.ActualHeight + popup.VerticalOffset + child.Margin.Top;
        var origin = new Point(20 - Math.Min(0, popupX), 20 - Math.Min(0, popupY));
        var width = (int)Math.Ceiling(origin.X + Math.Max(palette.ActualWidth, popupX + child.ActualWidth) + 20);
        var height = (int)Math.Ceiling(origin.Y + Math.Max(palette.ActualHeight, popupY + child.ActualHeight) + 20);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(194, 196, 198)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(SnapshotBrush(palette), null, new Rect(origin, new Size(palette.ActualWidth, palette.ActualHeight)));
            dc.DrawRectangle(SnapshotBrush(child), null,
                new Rect(origin.X + popupX, origin.Y + popupY, child.ActualWidth, child.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, filename));
        encoder.Save(stream);
    }

    private static void RenderEraserFrame(BoardWindow window) =>
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

    private static void RealtimeEraserPreview() => WithDrawingBoard((window, repository) =>
    {
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        Stroke(window, new Point(0, 50), new Point(200, 50));
        Stroke(window, new Point(0, 150), new Point(200, 150));
        var id = LiveDrawings(window).Single().Id;
        var original = repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single().PointsJson;
        var undoBefore = UndoCount(window);
        CallDrawing(window, "SetToolMode", BoardToolMode.Eraser);
        CallDrawing(window, "StartDrawing", new Point(100, 0), 1d);
        var operation = typeof(BoardWindow).GetField("_eraserPreviewOperation", PrivateInstance)!.GetValue(window);
        CallDrawing(window, "UpdateDrawing", new Point(100, 30), 1d);
        CallDrawing(window, "UpdateDrawing", new Point(100, 80), 1d);
        True(ReferenceEquals(operation, typeof(BoardWindow).GetField("_eraserPreviewOperation", PrivateInstance)!.GetValue(window)),
            "同一帧内的橡皮擦输入没有合并");
        RenderEraserFrame(window);
        Equal(3, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        Equal(original, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single().PointsJson);
        Equal(undoBefore, UndoCount(window));
        CallDrawing(window, "UpdateDrawing", new Point(100, 180), 1d);
        RenderEraserFrame(window);
        Equal(4, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
        Equal(id, LiveDrawings(window).Single().Id);
        Equal(original, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single().PointsJson);
        AwaitDrawing(window, "CompleteDrawingAsync");
        Equal(undoBefore + 1, UndoCount(window));
        Equal(4, DrawingGroupService.Read(repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single()).Count);
        AwaitDrawing(window, "UndoAsync");
        Equal(original, LiveDrawings(window).Single().PointsJson);
        AwaitDrawing(window, "RedoAsync");
        Equal(4, DrawingGroupService.Read(LiveDrawings(window).Single()).Count);
    });

    private static void RealtimeEraserCompletion() => WithDrawingBoard((window, repository) =>
    {
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        Stroke(window, new Point(100, 100), new Point(100, 100));
        var id = LiveDrawings(window).Single().Id;
        CallDrawing(window, "SetToolMode", BoardToolMode.Eraser);
        CallDrawing(window, "StartDrawing", new Point(100, 100), 1d);
        RenderEraserFrame(window);
        Equal(0, LiveDrawings(window).Count);
        True(!((Canvas)window.FindName("WorldCanvas")).Children.OfType<Border>().Any(b => Equals(b.Tag, id)),
            "整组擦完后视觉元素没有实时消失");
        Equal(1, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Count);
        AwaitDrawing(window, "CompleteDrawingAsync");
        Equal(0, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Count);
        AwaitDrawing(window, "UndoAsync");
        Equal(id, LiveDrawings(window).Single().Id);
        var undoBefore = UndoCount(window);
        CallDrawing(window, "StartDrawing", new Point(500, 500), 1d);
        RenderEraserFrame(window);
        AwaitDrawing(window, "CompleteDrawingAsync");
        Equal(undoBefore, UndoCount(window));
        CallDrawing(window, "StartDrawing", new Point(100, 100), 1d);
        // Closing/switching before the queued render runs must still apply the final input.
        AwaitDrawing(window, "FlushPendingDrawingAsync");
        Equal(0, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Count);
        True(typeof(BoardWindow).GetField("_eraserPreviewOperation", PrivateInstance)!.GetValue(window) is null,
            "完成手势后仍有待执行的擦除帧");
    });

    private static void FullscreenRoundTrip() => WithDrawingBoard((window, repository) =>
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 100;
        window.Top = 110;
        window.Width = 900;
        window.Height = 620;
        var original = new Rect(window.Left, window.Top, window.Width, window.Height);
        var originalResize = window.ResizeMode;
        var originalTopmost = window.Topmost;
        var button = (Button)window.FindName("BoardFullscreenButton");
        True(button.Content is System.Windows.Shapes.Path, "标题栏全屏按钮图标缺失");
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var monitor = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        CallDrawing(window, "ToggleFullScreen");
        True((bool)typeof(BoardWindow).GetField("_isFullScreen", PrivateInstance)!.GetValue(window)!, "没有进入全屏状态");
        Equal(ResizeMode.NoResize, window.ResizeMode);
        Equal(originalTopmost, window.Topmost);
        True(GetTestWindowRect(handle, out var rect), "无法读取全屏窗口范围");
        Equal(monitor.Left, rect.Left);
        Equal(monitor.Top, rect.Top);
        Equal(monitor.Right, rect.Right);
        Equal(monitor.Bottom, rect.Bottom);
        AwaitDrawing(window, "SaveViewportAsync");
        var saved = repository.GetViewportAsync("A").GetAwaiter().GetResult();
        Equal(original.Width, saved.WindowWidth);
        Equal(original.Height, saved.WindowHeight);
        True(button.ToolTip.ToString()!.Contains("退出"), "全屏按钮没有切换为退出提示");
        CallDrawing(window, "ToggleFullScreen");
        Equal(originalResize, window.ResizeMode);
        Equal(original.Left, window.Left, .001);
        Equal(original.Top, window.Top, .001);
        Equal(original.Width, window.Width, .001);
        Equal(original.Height, window.Height, .001);
        Equal(WindowState.Normal, window.WindowState);
        SaveDrawingTestVisual((Border)window.FindName("Toolbar"), "board-fullscreen-button.png");
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct TestWindowRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTestWindowRect(IntPtr handle, out TestWindowRect rectangle);
}
