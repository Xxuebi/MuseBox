using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static object TransitionState(DependencyObject owner)
    {
        var property = (DependencyProperty)typeof(PopupTransitions).GetField("StateProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return owner.GetValue(property);
    }
    private static Popup? ExitPopup(DependencyObject owner) =>
        (Popup?)TransitionState(owner).GetType().GetField("_ghost", PrivateInstance)!.GetValue(TransitionState(owner));
    private static T TransitionField<T>(DependencyObject owner, string name) =>
        (T)TransitionState(owner).GetType().GetField(name, PrivateInstance)!.GetValue(TransitionState(owner))!;

    private static void ReversePopupAnimations()
    {
        var anchor = new Border { Width = 40, Height = 30, Background = System.Windows.Media.Brushes.White };
        var host = new Window { Width = 700, Height = 480, Content = anchor, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0, WindowStyle = WindowStyle.None, AllowsTransparency = true };
        try
        {
            host.Show(); PumpDrawerAnimation(50);
            foreach (var placement in new[] { PlacementMode.Bottom, PlacementMode.Top, PlacementMode.Right, PlacementMode.Left })
            {
                var root = new Border { Width = 180, Height = 68, Margin = new Thickness(7), CornerRadius = new CornerRadius(10),
                    Background = System.Windows.Media.Brushes.White, Child = new TextBlock { Text = "菜单", Margin = new Thickness(12) } };
                var popup = new Popup { Child = root, PlacementTarget = anchor, Placement = placement, AllowsTransparency = true };
                PopupTransitions.SetEnabled(popup, true);
                var hiddenOnOpened = false;
                popup.Opened += (_, _) => hiddenOnOpened = root.Opacity <= .01;
                try
                {
                    popup.IsOpen = true;
                    PumpDrawerAnimation(220);
                    True(hiddenOnOpened, "原生菜单首帧未隐藏，会先闪出完整菜单");
                    var opening = TransitionField<Vector>(popup, "_direction");
                    var expected = placement switch { PlacementMode.Top => new Vector(0, -6), PlacementMode.Left => new Vector(-6, 0),
                        PlacementMode.Right => new Vector(6, 0), _ => new Vector(0, 6) };
                    Equal(expected, opening);
                    root.BeginAnimation(UIElement.OpacityProperty, null); root.Opacity = .4;
                    var before = root.PointToScreen(new Point());
                    popup.IsOpen = false;
                    True(TransitionField<bool>(popup, "_preparedBeforeNativeHide"), "等原窗口隐藏后才建立淡出画面，会出现空帧");
                    var ghost = ExitPopup(popup);
                    True(ghost is { IsOpen: true }, "缺少收回动画");
                    var image = (Image)ghost!.Child;
                    var captured = (BitmapSource)image.Source;
                    var pixel = new byte[4];
                    captured.CopyPixels(new Int32Rect(captured.PixelWidth / 2, captured.PixelHeight / 2, 1, 1), pixel, 4, 0);
                    Equal(102d, pixel[3], 2);
                    var dpi = VisualTreeHelper.GetDpi(root);
                    var after = image.PointToScreen(new Point(8, 8));
                    True((before - after).Length < 2 * dpi.DpiScaleX, $"淡出首帧移动了 {(before - after).Length:F2} 像素");
                    PumpDrawerAnimation(85);
                    var offset = (TranslateTransform)image.RenderTransform;
                    True(offset.X * expected.X + offset.Y * expected.Y < -.1, "关闭没有沿展开方向反向收回");
                    True(image.Opacity is > 0 and < 1, "关闭未逐渐淡出");
                    PumpDrawerAnimation(110);
                    True(ExitPopup(popup) is null, "关闭后残留动画窗口");
                    True(TransitionField<System.Windows.Interop.HwndSource?>(popup, "_source") is null, "关闭后仍订阅原生窗口");
                }
                finally { popup.IsOpen = false; }
            }
        }
        finally { host.Close(); }
    }

    private static void ToolMenusFollowZoom() => WithDrawingBoard((window, repository) =>
    {
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(BoardWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show(); PumpDrawerAnimation(40);
        AddPopupTestNote(window, repository);
        PressPopupOpener(window, "TextMoreButton"); PumpDrawerAnimation(220);
        var popup = (Popup)window.FindName("TextMorePopup");
        var target = (FrameworkElement)popup.PlacementTarget;
        var initialPosition = ((FrameworkElement)popup.Child).PointToScreen(new Point());
        var moved = false;
        foreach (var delta in new[] { 120, 120, -120, -120, 120, -120 })
        {
            typeof(BoardWindow).GetMethod("OnPreviewMouseWheel", PrivateInstance | BindingFlags.DeclaredOnly)!
                .Invoke(window, new object[] { window, new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
                    { RoutedEvent = UIElement.PreviewMouseWheelEvent } });
            PumpDrawerAnimation(55);
            True(popup.IsOpen, "缩放时不应强行关闭子菜单");
            var arrow = (FrameworkElement)window.FindName(popup.Placement == PlacementMode.Bottom ? "TextMoreArrowUp" : "TextMoreArrowDown");
            var arrowTip = arrow.PointToScreen(new Point(arrow.ActualWidth / 2, 0));
            var anchor = target.PointToScreen(new Point(target.ActualWidth / 2, 0));
            True(Math.Abs(arrowTip.X - anchor.X) <= 2, $"缩放后尖角与按钮相差 {arrowTip.X-anchor.X:F2} 像素");
            var child = (FrameworkElement)popup.Child;
            var childTop = child.PointToScreen(new Point()).Y;
            var childBottom = child.PointToScreen(new Point(0, child.ActualHeight)).Y;
            var expectedEdge = popup.Placement == PlacementMode.Bottom
                ? target.PointToScreen(new Point(0, target.ActualHeight + popup.VerticalOffset)).Y
                : target.PointToScreen(new Point(0, popup.VerticalOffset)).Y;
            True(Math.Abs((popup.Placement == PlacementMode.Bottom ? childTop : childBottom) - expectedEdge) < 16,
                "缩放后菜单没有跟随工具条的上下位置");
            moved |= (child.PointToScreen(new Point()) - initialPosition).Length > 2;
        }
        True(moved, "测试没有实际移动菜单");
        SavePopupPair(popup, (Border)window.FindName("TextPalette"), "text-popup-after-zoom.png");
        popup.IsOpen = false; PumpDrawerAnimation(170);

        // A render-only translation must also move a native combo-like popup.
        var combo = (ComboBox)window.FindName("TextFontCombo");
        combo.IsDropDownOpen = true; PumpDrawerAnimation(220);
        var fontPopup = (Popup)combo.Template.FindName("PART_Popup", combo);
        var fontContent = (FrameworkElement)fontPopup.Child;
        var before = fontContent.PointToScreen(new Point());
        var translation = (TranslateTransform)window.FindName("TextPaletteTranslate");
        translation.X -= 35; translation.Y += 15;
        PumpDrawerAnimation(70);
        var after = fontContent.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(combo);
        Equal(-35 * dpi.DpiScaleX, after.X - before.X, 2);
        Equal(15 * dpi.DpiScaleY, after.Y - before.Y, 2);
        combo.IsDropDownOpen = false; PumpDrawerAnimation(170);
    });

    private static void ImageEditButtonShadow() => WithDrawingBoard((window, _) =>
    {
        AddEditableImage(window);
        var surface = ArrangeBoardSurface(window);
        CallDrawing(window, "UpdateImageToolbar");
        var button = (Button)window.FindName("ImageEditToggle");
        True(button.Effect is DropShadowEffect { BlurRadius: >= 8 and <= 14, Opacity: > .1 and < .3, ShadowDepth: <= 3 },
            "图片编辑按钮没有轻量阴影");
        surface.Background = System.Windows.Media.Brushes.White;
        SaveDrawingTestVisual(surface, "image-edit-button-shadow.png", false);
    });

    private static void PromptCloseAnimationResult()
    {
        foreach (var accepted in new[] { false, true })
        {
            var prompt = new PromptWindow("确认测试", "临时测试，不修改用户资料。");
            prompt.Opacity = 0; prompt.ShowActivated = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ((Button)prompt.FindName(accepted ? "PromptConfirm" : "PromptCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                True(prompt.IsVisible, "确认框没有等待反向淡出就立即关闭");
            };
            prompt.Loaded += (_, _) => timer.Start();
            try { Equal(accepted, prompt.ShowDialog() == true); }
            finally { timer.Stop(); prompt.Close(); }
        }
    }

    private static void ReverseScaleAnimations()
    {
        var root = new Border { Width = 260, Height = 64, RenderTransformOrigin = new Point(.5, .5),
            Background = System.Windows.Media.Brushes.White, CornerRadius = new CornerRadius(12) };
        var host = new Window { Width = 400, Height = 240, Content = root, Opacity = 0, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true };
        try
        {
            host.Show(); PumpDrawerAnimation(30);
            PopupTransitions.ShowScalePanel(root, .2, .65);
            PumpDrawerAnimation(80);
            var scale = TransitionField<ScaleTransform>(root, "_scale");
            True(scale.ScaleX is > .2 and < 1, "放大没有中间帧");
            var beforeWidth = root.ActualWidth * scale.ScaleX;
            var alpha = root.Opacity;
            PopupTransitions.HidePanel(root);
            var ghost = ExitPopup(root);
            True(ghost is { IsOpen: true }, "放大淡入没有对应的缩小淡出");
            var image = (Image)ghost!.Child;
            Equal(beforeWidth + 16, image.Width, 2);
            var snapshot = (BitmapSource)image.Source;
            var pixel = new byte[4];
            snapshot.CopyPixels(new Int32Rect(snapshot.PixelWidth / 2, snapshot.PixelHeight / 2, 1, 1), pixel, 4, 0);
            Equal(alpha * 255, pixel[3], 3);
            PumpDrawerAnimation(100);
            True(image.RenderTransform is ScaleTransform { ScaleX: > 0 and < 1, ScaleY: > 0 and < 1 },
                "图片工具栏关闭变成了平移，而非缩小");
            PumpDrawerAnimation(120);
            True(ExitPopup(root) is null, "缩小动画结束后未清理");
            PopupTransitions.ShowScalePanel(root, .2, .65);
            PopupTransitions.HidePanel(root);
            PumpDrawerAnimation(40);
            True(root.RenderTransform is not TransformGroup && ExitPopup(root) is null, "快速开关触发过期动画");
        }
        finally { host.Close(); }
    }
}
