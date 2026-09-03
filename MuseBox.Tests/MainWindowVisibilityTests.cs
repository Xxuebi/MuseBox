using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SceneTestWindowRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out SceneTestWindowRect rectangle);

    private static void AssertMainIntersectsScreen(MainWindow window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        True(GetWindowRect(handle, out var rect), "无法读取小窗位置");
        True(System.Windows.Forms.Screen.AllScreens.Any(screen =>
            rect.Left >= screen.WorkingArea.Left && rect.Top >= screen.WorkingArea.Top &&
            rect.Right <= screen.WorkingArea.Right + 1 && rect.Bottom <= screen.WorkingArea.Bottom + 1),
            $"小窗恢复到屏幕外：{rect.Left},{rect.Top}–{rect.Right},{rect.Bottom}");
        True(window.IsVisible && window.WindowState == WindowState.Normal, "小窗恢复后被隐藏或最小化");
    }

    private static void MainWindowOffscreenStartup() => WithMainDrawerWindow((window, _) =>
    {
        window.Opacity = 0; // Native geometry only; do not cover the user's desktop.
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        foreach (var position in new[] { new Point(-30000, -30000), new Point(30000, 30000) })
        {
            typeof(MainWindow).GetField("_settings", PrivateInstance)!.SetValue(window, new AppSettings
            {
                MainLeft = position.X, MainTop = position.Y, MainWidth = 520, MainHeight = 420,
                MainTopmost = false, HotkeyEnabled = false
            });
            MainCall(window, "RestoreMainWindowState");
            PumpDrawerAnimation(60);
            AssertMainIntersectsScreen(window);
        }
    });

    private static void MainWindowStartupAndWakeVisibility() => WithMainDrawerWindow((window, repository) =>
    {
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        var initial = new AppSettings { MainLeft = 30000, MainTop = -30000, MainWidth = 520,
            MainHeight = 420, MainTopmost = false, HotkeyEnabled = false, CompatibleRendering = true };
        window.PrepareStartupWindow(initial);
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        True(handle != IntPtr.Zero && !window.IsVisible, "没有在首次显示前准备小窗位置");
        True(GetWindowRect(handle, out var beforeShow), "无法读取首次显示前的位置");
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        True(beforeShow.Left >= area.Left && beforeShow.Top >= area.Top &&
            beforeShow.Right <= area.Right && beforeShow.Bottom <= area.Bottom, "首次显示前位置仍在屏幕外");
        window.Loaded += (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(MainWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        PumpSceneTask(async () =>
        {
            window.Show();
            await window.Initialization;
            return true;
        });
        PumpDrawerAnimation(80);
        AssertMainIntersectsScreen(window);
        Equal(4, MainDrawers(window).Count);
        Equal(System.Windows.Interop.RenderMode.SoftwareOnly,
            System.Windows.Interop.HwndSource.FromHwnd(handle)!.CompositionTarget.RenderMode);
        // A repeated Loaded notification must not replay startup state or rebuild drawers.
        MainDrawers(window)[0].DisplayName = "保留当前窗口状态";
        typeof(MainWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!
            .Invoke(window, new object[] { window, new RoutedEventArgs() });
        PumpDrawerAnimation(60);
        Equal("保留当前窗口状态", MainDrawers(window)[0].DisplayName);
        foreach (var minimize in new[] { false, true })
        {
            window.Left = -30000; window.Top = 30000;
            if (minimize) window.WindowState = WindowState.Minimized;
            else window.Hide();
            window.ShowCollectorWindow();
            PumpDrawerAnimation(80);
            AssertMainIntersectsScreen(window);
        }
        window.Left = 30000; window.Top = 30000;
        MainCall(window, "OnMainDisplaySettingsChanged", window, EventArgs.Empty);
        PumpDrawerAnimation(80);
        AssertMainIntersectsScreen(window);
        GetWindowRect(handle, out var valid);
        window.ShowCollectorWindow();
        GetWindowRect(handle, out var kept);
        Equal(valid.Left, kept.Left); Equal(valid.Top, kept.Top);
        Equal(4, repository.GetDrawersAsync().GetAwaiter().GetResult().Count);
    });
}
