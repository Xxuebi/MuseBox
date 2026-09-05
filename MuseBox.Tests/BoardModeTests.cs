using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static BoardPresentationMode CurrentBoardMode(BoardWindow window) =>
        (BoardPresentationMode)typeof(BoardWindow).GetField("_presentationMode", PrivateInstance)!.GetValue(window)!;

    private static void BoardPresentationModeRoundTrip() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        BoardSelection(window).Add("image-0");
        CallDrawing(window, "UpdateSelectionVisuals");
        var toolbar = (Border)window.FindName("Toolbar");
        var overlay = (Canvas)window.FindName("OverlayCanvas");
        var surface = (Grid)window.FindName("BoardSurface");
        var ignore = (MenuItem)window.FindName("IgnoreMouseModeMenuItem");
        var transparent = (MenuItem)window.FindName("TransparentModeMenuItem");
        var smart = (MenuItem)window.FindName("SmartTopmostModeMenuItem");
        Equal(string.Join("|", new[] { "无视鼠标", "画板透明", "智能置顶" }),
            string.Join("|", new[] { ignore.Header, transparent.Header, smart.Header }));

        AwaitDrawing(window, "EnterPresentationModeAsync", BoardPresentationMode.IgnoreMouse);
        Equal(BoardPresentationMode.IgnoreMouse, CurrentBoardMode(window));
        True(window.Topmost && ignore.IsChecked, "无视鼠标没有强制置顶或激活菜单");
        Equal(Visibility.Collapsed, toolbar.Visibility);
        Equal(Visibility.Collapsed, overlay.Visibility);
        AwaitDrawing(window, "SaveViewportAsync");
        True(!repository.GetViewportAsync("A").GetAwaiter().GetResult().Topmost,
            "临时置顶污染了持久化置顶设置");

        AwaitDrawing(window, "EnterPresentationModeAsync", BoardPresentationMode.Transparent);
        Equal(BoardPresentationMode.Transparent, CurrentBoardMode(window));
        True(!ignore.IsChecked && transparent.IsChecked, "模式互斥状态不正确");
        Equal(Visibility.Visible, overlay.Visibility);
        Equal(Visibility.Collapsed, toolbar.Visibility);
        True(surface.IsHitTestVisible, "透明模式禁用了画板交互");
        Equal((byte)1, ((SolidColorBrush)surface.Background).Color.A);

        var toast = (Border)window.FindName("ModeToast");
        True(toast.MinWidth >= 440 && toast.Padding.Left >= 24 && toast.CornerRadius.TopLeft >= 14,
            "画板模式提示窗口仍然过小");
        True(((TextBlock)window.FindName("ModeToastTitle")).FontSize >= 16 &&
             ((TextBlock)window.FindName("ModeToastDetail")).FontSize >= 13,
            "画板模式提示文字仍然过小");
        var emptyClick = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent,
            Source = surface
        };
        CallDrawing(window, "OnSurfaceMouseDown", surface, emptyClick);
        Equal(0, BoardSelection(window).Count);

        var app = (App)System.Windows.Application.Current;
        var boards = (Dictionary<string, BoardWindow>)typeof(App).GetField("_boards", PrivateInstance)!.GetValue(app)!;
        boards["A"] = window;
        var tray = (ContextMenu)typeof(App).GetMethod("CreateTrayMenu", PrivateInstance)!
            .Invoke(app, new object[] { repository.GetDrawersAsync().GetAwaiter().GetResult() })!;
        var trayExit = (MenuItem)tray.Items[2];
        True(trayExit.IsEnabled && trayExit.Header.ToString()!.Contains("画板透明模式"),
            "托盘一级菜单没有显示当前画板模式退出按钮");
        trayExit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        boards.Remove("A");
        Equal(BoardPresentationMode.None, CurrentBoardMode(window));
        True(!ignore.IsChecked && !transparent.IsChecked && !smart.IsChecked, "退出后菜单仍激活");
        Equal(Visibility.Visible, toolbar.Visibility);
        Equal(Visibility.Visible, overlay.Visibility);
        True(!window.Topmost, "退出后没有恢复原置顶状态");
        Equal(Visibility.Visible, ((Border)window.FindName("ModeToast")).Visibility);
        True(((TextBlock)window.FindName("ModeToastTitle")).Text.Contains("已退出画板透明模式"),
            "缺少退出模式提示");
    });

    private static void BoardModeShortcutSafety()
    {
        var defaults = BoardShortcutCatalog.CreateDefaults();
        Equal("Ctrl+Shift+F12", defaults[BoardShortcutCatalog.ExitBoardMode]);
        True(BoardShortcutCatalog.TryParse(defaults[BoardShortcutCatalog.ExitBoardMode], out var parsed) &&
             parsed is { Modifiers: not System.Windows.Input.ModifierKeys.None }, "退出模式不是安全的组合快捷键");
        var window = new SettingsWindow(new AppSettings { BoardShortcutsEnabled = false });
        try
        {
            var rows = window.ShortcutGroups.SelectMany(group => group.Shortcuts).ToArray();
            var exit = rows.Single(row => row.Id == BoardShortcutCatalog.ExitBoardMode);
            var clear = typeof(SettingsWindow).GetMethod("OnClearBoardShortcutClick", PrivateInstance)!;
            clear.Invoke(window, new object[] { new System.Windows.Controls.Button { Tag = exit.Id }, new RoutedEventArgs() });
            Equal("Ctrl+Shift+F12", exit.Gesture);
            Equal("画板视图", window.ShortcutGroups.Single(group =>
                group.Shortcuts.Any(row => row.Id == BoardShortcutCatalog.ExitBoardMode)).DisplayName);
            True(((TextBlock)window.FindName("ShortcutConflictText")).Text.Contains("仍保持启用"),
                "快捷键总开关错误禁用了退出模式快捷键");
        }
        finally { window.Close(); }
    }

    private static void BoardModeNativeWindowBehavior() => WithDrawingBoard((window, _) =>
    {
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(BoardWindow).GetMethod("OnLoaded", PrivateInstance | System.Reflection.BindingFlags.DeclaredOnly)!);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        var boardHandle = new WindowInteropHelper(window).Handle;
        var getStyle = typeof(BoardWindow).GetMethod("GetWindowLongPtr",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        long Style(IntPtr handle) => ((IntPtr)getStyle.Invoke(null, new object[] { handle, -20 })!).ToInt64();

        AwaitDrawing(window, "EnterPresentationModeAsync", BoardPresentationMode.IgnoreMouse);
        True((Style(boardHandle) & 0x20) != 0, "无视鼠标没有设置原生鼠标穿透样式");
        window.WindowState = WindowState.Minimized;
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Equal(WindowState.Normal, window.WindowState);
        Equal(BoardPresentationMode.None, CurrentBoardMode(window));
        True((Style(boardHandle) & 0x20) == 0, "退出后没有清除鼠标穿透样式");

        var target = new Window { Width = 260, Height = 180, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            target.Show();
            var targetHandle = new WindowInteropHelper(target).Handle;
            AwaitDrawing(window, "EnterPresentationModeAsync", BoardPresentationMode.SmartTopmost);
            True(!(bool)CallDrawing(window, "CanUseSmartTarget", boardHandle)!, "允许选择 MuseBox 自身窗口");
            // The production picker intentionally rejects all windows from this test process.
            // Attach directly here to exercise Z-order and target lifetime behavior.
            CallDrawing(window, "AttachSmartTarget", targetHandle);
            Equal(targetHandle, (IntPtr)typeof(BoardWindow).GetField("_smartTarget", PrivateInstance)!.GetValue(window)!);
            True((IntPtr)typeof(BoardWindow).GetField("_smartForegroundHook", PrivateInstance)!.GetValue(window)! != IntPtr.Zero,
                "智能置顶没有注册前台窗口同步事件");

            target.Hide();
            CallDrawing(window, "OnSmartTargetTick", window, EventArgs.Empty);
            True(!window.IsVisible, "目标隐藏后画板没有同步隐藏");
            target.Show();
            CallDrawing(window, "OnSmartTargetTick", window, EventArgs.Empty);
            True(window.IsVisible, "目标恢复后画板没有恢复");
            target.Close();
            CallDrawing(window, "OnSmartTargetTick", window, EventArgs.Empty);
            Equal(BoardPresentationMode.None, CurrentBoardMode(window));
            True(window.IsVisible, "目标关闭后画板没有恢复");
            Equal(IntPtr.Zero, (IntPtr)typeof(BoardWindow).GetField("_smartForegroundHook", PrivateInstance)!.GetValue(window)!);
        }
        finally { if (target.IsLoaded) target.Close(); }
    });

    private static void BoardModeMostRecentExitOrder() => WithDrawingBoard((first, _) =>
        WithDrawingBoard((second, _) =>
        {
            var app = (App)System.Windows.Application.Current;
            AwaitDrawing(first, "EnterPresentationModeAsync", BoardPresentationMode.Transparent);
            AwaitDrawing(second, "EnterPresentationModeAsync", BoardPresentationMode.IgnoreMouse);
            app.ExitMostRecentBoardMode();
            Equal(BoardPresentationMode.None, CurrentBoardMode(second));
            Equal(BoardPresentationMode.Transparent, CurrentBoardMode(first));
            app.ExitMostRecentBoardMode();
            Equal(BoardPresentationMode.None, CurrentBoardMode(first));
        }));
}
