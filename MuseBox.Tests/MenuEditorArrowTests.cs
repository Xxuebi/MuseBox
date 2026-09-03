using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;
using Size = System.Windows.Size;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Cursor = System.Windows.Input.Cursor;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static void TextMenuGroupsAndTransitions() => WithDrawingBoard((window, _) =>
    {
        var actions = (StackPanel)window.FindName("TextMoreActions");
        Equal("CopyTextStyleButton,PasteTextStyleButton,ClearTextFormattingButton,TextLinksButton",
            string.Join(',', actions.Children.OfType<Button>().Select(x => x.Name)));
        Equal(2, actions.Children.OfType<Border>().Count(x => x.Width == 1 && x.Height >= 20));
        foreach (var name in new[] { "TextMorePopup", "DrawingShapesPopup", "DrawingSettingsPopup", "DrawingEraserPopup", "GifSpeedPopup" })
            True(PopupTransitions.GetEnabled((Popup)window.FindName(name)), $"缺少菜单动画：{name}");
        True(PopupTransitions.GetEnabled(((Grid)window.FindName("BoardSurface")).ContextMenu), "画板右键菜单缺少动画");
        SaveDrawingTestVisual((FrameworkElement)((Popup)window.FindName("TextMorePopup")).Child, "text-menu-grouped.png");
        SaveDrawingTestVisual((FrameworkElement)((Popup)window.FindName("DrawingSettingsPopup")).Child, "drawing-arrow-style.png");
    });

    private static void DrawerNativeToggle() => WithMainDrawerWindow((window, _) =>
    {
        window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show(); PumpDrawerAnimation(50);
        var button = (Button)MainDescendants((FrameworkElement)window.Content).First(x => x.Name == "DrawerSettingsButton");
        MainCall(window, "OnDrawerSettingsClick", button, new RoutedEventArgs());
        var menu = (DrawerMenuPopup)typeof(MainWindow).GetField("_drawerMenu", PrivateInstance)!.GetValue(window)!;
        PumpDrawerAnimation(210);
        True(menu.IsExpanded && menu.StaysOpen && Mouse.Captured is null, "菜单捕获鼠标，阻断按钮悬停");
        var root = (FrameworkElement)menu.Child;
        var direction = root.PointToScreen(new Point(0, root.ActualHeight / 2)).Y <
            button.PointToScreen(new Point(0, button.ActualHeight / 2)).Y ? -1 : 1;
        var hwnd = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(root)).Handle;
        MainCall(window, "OnDrawerSettingsPress", button, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
        True(menu.IsExpanded, "鼠标按下就提前关闭，导致点击状态丢失");
        MainCall(window, "OnDrawerSettingsClick", button, new RoutedEventArgs());
        True(!menu.IsExpanded && menu.IsOpen, "关闭没有保留原弹窗完成淡出");
        Equal(hwnd, ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(root)).Handle);
        PumpDrawerAnimation(65);
        True(root.Opacity is > 0 and < 1 && ((TranslateTransform)root.RenderTransform).Y * direction < 0,
            "下拉菜单没有向上淡出");
        MainCall(window, "OnDrawerSettingsClick", button, new RoutedEventArgs());
        PumpDrawerAnimation(210);
        True(menu.IsExpanded && menu.IsOpen, "淡出中再次点击未平滑反向打开");
        Equal(1d, root.Opacity, .001);
        MainCall(window, "OnDrawerSettingsClick", button, new RoutedEventArgs());
        PumpDrawerAnimation(190);
        True(!menu.IsOpen && Mouse.Captured is null, "淡出后原生弹窗或捕获残留");
    });

    private static void PopupAnimationLifecycle()
    {
        var root = new Border { Width = 190, Height = 85, Background = System.Windows.Media.Brushes.White,
            CornerRadius = new CornerRadius(10), Child = new TextBlock { Text = "菜单动画", Margin = new Thickness(14) } };
        var host = new Window { Width = 220, Height = 120, Content = root, Opacity = 0, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true };
        try
        {
            host.Show();
            host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            PopupTransitions.ShowPanel(root);
            PumpDrawerAnimation(200);
            True(root.RenderTransform is TransformGroup, "展开没有方向性位移动画");
            Equal(1d, root.Opacity, .001);
            PopupTransitions.HidePanel(root);
            var property = (DependencyProperty)typeof(PopupTransitions).GetField("StateProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var state = root.GetValue(property);
            var ghostField = state.GetType().GetField("_ghost", PrivateInstance)!;
            var ghost = (Popup?)ghostField.GetValue(state);
            True(ghost is { IsOpen: true, IsHitTestVisible: false, Focusable: false }, "退出动画未创建非交互快照");
            True(ghost!.Child is Image { Source: BitmapSource }, "退出不是稳定的缓存快照");
            PumpDrawerAnimation(170);
            True(ghostField.GetValue(state) is null && !ghost.IsOpen, "退出后残留弹出窗口");
            PopupTransitions.ShowPanel(root);
            PopupTransitions.HidePanel(root);
            PumpDrawerAnimation(30);
            True(root.RenderTransform is not TransformGroup, "快速开关后应用了过期的打开动画");
            var popup = new Popup { PlacementTarget = root, Placement = PlacementMode.Bottom, AllowsTransparency = true,
                Child = new Border { Width = 190, Height = 70, Background = System.Windows.Media.Brushes.White,
                    CornerRadius = new CornerRadius(10), Child = new TextBlock { Text = "下拉菜单", Margin = new Thickness(12) } } };
            PopupTransitions.SetEnabled(popup, true);
            var menu = new ContextMenu();
            menu.SetResourceReference(FrameworkElement.StyleProperty, "RoundedContextMenu");
            menu.PlacementTarget = root;
            menu.Placement = PlacementMode.Bottom;
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "重命名" });
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "删除抽屉" });
            try
            {
                foreach (var native in new DependencyObject[] { popup, menu })
                {
                    if (native is Popup p) p.IsOpen = true; else menu.IsOpen = true;
                    PumpDrawerAnimation(220);
                    True(native is Popup check ? check.IsOpen : menu.IsOpen, "原生菜单在验证关闭前意外消失");
                    if (native is Popup p2) p2.IsOpen = false; else menu.IsOpen = false;
                    PumpDrawerAnimation(20);
                    var nativeState = native.GetValue(property);
                    True(TransitionField<bool>(native, "_preparedBeforeNativeHide"),
                        $"{native.GetType().Name} 仍在原生窗口消失后才建立退出画面");
                    var nativeGhost = (Popup?)ghostField.GetValue(nativeState);
                    if (native is ContextMenu)
                    {
                        True(nativeGhost is null, "右键菜单关闭仍创建快照窗口，会在消失时闪现");
                    }
                    else
                    {
                        True(nativeGhost is { IsOpen: true }, "Popup 关闭时缺少淡出动画");
                        var snapshot = (BitmapSource)((Image)nativeGhost!.Child).Source;
                        var pixels = new byte[snapshot.PixelWidth * snapshot.PixelHeight * 4];
                        snapshot.CopyPixels(pixels, snapshot.PixelWidth * 4, 0);
                        var visiblePixels = Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 32);
                        True(visiblePixels > pixels.Length / 16, "Popup 关闭动画截取到了空内容");
                        PumpDrawerAnimation(140);
                        True(ghostField.GetValue(nativeState) is null, "Popup 动画窗口未释放");
                    }
                }
            }
            finally { popup.IsOpen = false; menu.IsOpen = false; }
        }
        finally { host.Close(); }
    }

    private static void RoundedPromptLayout()
    {
        var window = new PromptWindow("删除抽屉", "确定删除抽屉 E 及其中的 8 项内容吗？此操作不会影响其他抽屉。", "删除抽屉");
        try
        {
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(430, double.PositiveInfinity));
            root.Arrange(new Rect(-root.Margin.Left, -root.Margin.Top, 430, root.DesiredSize.Height));
            root.UpdateLayout();
            SaveDrawingTestVisual(root, "rounded-confirm-prompt.png", false);
            var chrome = (Border)window.FindName("PromptChrome");
            True(chrome.CornerRadius.TopLeft >= 12, "确认弹窗仍是直角系统样式");
            Equal("删除抽屉", ((Button)window.FindName("PromptConfirm")).Content.ToString()!);
            True(!((Button)window.FindName("PromptConfirm")).IsDefault, "破坏性确认成为默认回车动作");
            Equal(Visibility.Visible, ((Button)window.FindName("PromptCancel")).Visibility);
            var message = (TextBlock)window.FindName("PromptMessage");
            Equal(TextWrapping.Wrap, message.TextWrapping);
        }
        finally { window.Close(); }
        var info = new PromptWindow("提示", "资料库未发生变化。", confirmation: false);
        Equal(Visibility.Collapsed, ((Button)info.FindName("PromptCancel")).Visibility);
        info.Close();
    }

    private static void ImageOpacityWorkflow() => WithDrawingBoard((window, _) =>
    {
        using var source = new Bitmap(3, 2);
        source.SetPixel(1, 1, Color.FromArgb(128, 120, 60, 30));
        using var half = ImageEditService.Adjust(source, 0, 1, 1, 0, .5);
        Equal(64d, half.GetPixel(1, 1).A, 1);
        using var zero = ImageEditService.Adjust(source, .1, 1.2, 1.5, 30, 0);
        Equal((byte)0, zero.GetPixel(1, 1).A);
        Equal((byte)128, source.GetPixel(1, 1).A);
        var item = AddEditableImage(window);
        var editor = new ImageEditorWindow(item.AssetPath);
        try
        {
            var slider = (Slider)editor.FindName("OpacitySlider");
            var input = (TextBox)editor.FindName("OpacityValue");
            input.Text = "50";
            EditorCall(editor, "CommitNumber", input);
            EditorCall(editor, "RefreshPreview");
            Equal(50d, slider.Value);
            var preview = (BitmapSource)((Image)editor.FindName("PreviewImage")).Source;
            var bytes = new byte[preview.PixelWidth * preview.PixelHeight * 4];
            preview.CopyPixels(bytes, preview.PixelWidth * 4, 0);
            Equal(127d, bytes[3], 1);
            True(((Grid)editor.FindName("PreviewHost")).Background is DrawingBrush { TileMode: TileMode.Tile }, "预览没有棋盘格");
            True((bool)EditorCall(editor, "PrepareResult", true)!, "透明度结果无法另存");
            Equal(127d, editor.ResultBitmap!.GetPixel(0, 0).A, 1);
            True(editor.SaveAsNewImage, "另存模式丢失");
            EditorCall(editor, "UndoEdit");
            Equal(100d, slider.Value);
            EditorCall(editor, "OnAdjustmentStart", slider, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            foreach (var value in new[] { 80d, 60, 40, 25 }) slider.Value = value;
            EditorCall(editor, "EndAdjustment");
            EditorCall(editor, "UndoEdit");
            Equal(100d, slider.Value);
            input.Text = "-10"; EditorCall(editor, "CommitNumber", input); Equal(0d, slider.Value);
            input.Text = "NaN"; EditorCall(editor, "CommitNumber", input); Equal(0d, slider.Value);
            input.Text = "50"; EditorCall(editor, "CommitNumber", input); EditorCall(editor, "RefreshPreview");
            var content = (FrameworkElement)editor.Content;
            content.Measure(new Size(920, 650)); content.Arrange(new Rect(0, 0, 920, 650)); content.UpdateLayout();
            SaveDrawingTestVisual(content, "image-editor-opacity-checkerboard.png", false);
            EditorCall(editor, "OnResetClick", editor, new RoutedEventArgs()); Equal(100d, slider.Value);
            EditorCall(editor, "UndoEdit"); Equal(50d, slider.Value);
        }
        finally { editor.ResultBitmap?.Dispose(); editor.Close(); }
    });

    private static void RotationCursorNativeDpi()
    {
        var cursorType = typeof(BoardWindow).Assembly.GetType("ScreenshotCollector.Services.BoardRotationCursor")!;
        foreach (var size in new[] { 32, 40, 48, 64, 96, 128 })
        {
            var bytes = (byte[])cursorType.GetMethod("CreateCursorData", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { size })!;
            Equal((ushort)2, BitConverter.ToUInt16(bytes, 2));
            Equal((byte)size, bytes[6]);
            Equal((ushort)(size / 2), BitConverter.ToUInt16(bytes, 10));
            Equal((ushort)(size / 2), BitConverter.ToUInt16(bytes, 12));
            Equal(22 + 40 + size * size * 4 + ((size + 31) / 32) * 4 * size, bytes.Length);
            using var stream = new MemoryStream(bytes);
            using var cursor = new Cursor(stream, false);
            True(cursor.ToString() is not null, "原生 CUR 无法解码");
        }
    }

    private static void CurveArrowWorkflow() => WithDrawingBoard((window, repository) =>
    {
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        CallDrawing(window, "OnDrawingDashClick", window.FindName("DrawingCurveArrowButton"), new RoutedEventArgs());
        SetDrawingField(window, "_drawingStrokeColor", "#FFFF1010");
        ((Slider)window.FindName("DrawingThicknessSlider")).Value = 12;
        var points = Enumerable.Range(0, 65).Select(i =>
        {
            var t = i * Math.PI / 64;
            return new Point(250 - Math.Cos(t) * 180, 210 - Math.Sin(t) * 125);
        }).ToArray();
        CallDrawing(window, "StartDrawing", points[0], 1d);
        foreach (var point in points.Skip(1)) CallDrawing(window, "UpdateDrawing", point, 1d);
        AwaitDrawing(window, "CompleteDrawingAsync");
        var group = LiveDrawings(window).Single();
        var stroke = DrawingGroupService.ToWorld(group).Single();
        Equal(BoardDrawingKind.CurveArrow, stroke.Kind);
        True(stroke.Points.Count > 10, "曲线箭头被保存为两点直线");
        Equal(1, UndoCount(window));
        var saved = repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single();
        Equal(BoardDrawingKind.CurveArrow, DrawingGroupService.Read(saved).Single().Kind);
        var visual = new BoardDrawingVisual { Item = group, Width = group.Width, Height = group.Height };
        SaveDrawingTestVisual(visual, "curve-arrow-render.png");
        var geometry = typeof(BoardWindow).Assembly.GetType("ScreenshotCollector.Services.ArrowGeometry")!;
        var head = (Point[])geometry.GetMethod("Head", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { stroke.Points.Select(p => new Point(p.X, p.Y)).ToArray(), 12d })!;
        EqualPoint(points[^1], head[1]);
        True(head[0].Y < head[1].Y && head[2].Y < head[1].Y, "箭头没有沿曲线末端朝下");
        foreach (var p in head) True(new Rect(group.X, group.Y, group.Width, group.Height).Contains(p), "箭头超出绘制边框");
        AwaitDrawing(window, "UndoAsync"); Equal(0, LiveDrawings(window).Count);
        AwaitDrawing(window, "RedoAsync"); Equal(BoardDrawingKind.CurveArrow, DrawingGroupService.Read(LiveDrawings(window).Single()).Single().Kind);
        CallDrawing(window, "ApplyEraserPath", new[] { new BoardStrokePoint(head[0].X, head[0].Y) }, 2d);
        Equal(0, LiveDrawings(window).Count);
        Equal(6, (int)BoardDrawingKind.Group);
        foreach (var angle in Enumerable.Range(0, 24).Select(i => i * Math.PI / 12))
        {
            var shortPath = new[] { new Point(30, 30), new Point(30 + Math.Cos(angle), 30 + Math.Sin(angle)) };
            var thick = new BoardDrawingStroke { Kind = BoardDrawingKind.CurveArrow, StrokeThickness = 40,
                Points = shortPath.Select(p => new BoardStrokePoint(p.X, p.Y)).ToList() };
            var shortGroup = new BoardDrawingItem();
            DrawingGroupService.SetWorldStrokes(shortGroup, new[] { thick });
            var shortHead = (Point[])geometry.GetMethod("Head", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { shortPath, 40d })!;
            var bounds = new Rect(shortGroup.X, shortGroup.Y, shortGroup.Width, shortGroup.Height);
            foreach (var p in shortHead)
                True(bounds.Contains(new Rect(p.X - 20, p.Y - 20, 40, 40)), "短粗箭头圆角超出边框");
        }
    });
}
