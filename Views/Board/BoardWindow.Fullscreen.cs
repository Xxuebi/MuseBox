using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private bool _isFullScreen;
    private Rect _fullscreenRestoreBounds;
    private ResizeMode _fullscreenRestoreResizeMode;
    private WindowState _fullscreenRestoreState;
    private Size _fullscreenRestoreMinimum;

    private async void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await FlushPendingDrawingAsync();
            ToggleFullScreen();
        }
        catch (Exception error) { BoardStatus.Text = $"无法切换全屏：{error.Message}"; }
    }

    private void ToggleFullScreen()
    {
        CloseToolPopups();
        HideEraserCursor();
        if (_isFullScreen)
        {
            ExitFullScreen();
            return;
        }
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var bounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        _fullscreenRestoreState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _fullscreenRestoreBounds = WindowState == WindowState.Normal
            ? new Rect(double.IsFinite(Left) ? Left : 0, double.IsFinite(Top) ? Top : 0, Width, Height)
            : RestoreBounds;
        _fullscreenRestoreResizeMode = ResizeMode;
        _fullscreenRestoreMinimum = new Size(MinWidth, MinHeight);
        _viewport.WindowLeft = _fullscreenRestoreBounds.Left;
        _viewport.WindowTop = _fullscreenRestoreBounds.Top;
        _viewport.WindowWidth = _fullscreenRestoreBounds.Width;
        _viewport.WindowHeight = _fullscreenRestoreBounds.Height;
        _isFullScreen = true;
        ApplyWindowFrame(_viewport.ShowWindowFrame);
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = MinHeight = 0;
        // Screen.Bounds and SetWindowPos both use physical pixels. This avoids
        // mixed-DPI monitor offsets and covers the taskbar without changing Topmost.
        if (!SetFullscreenWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014))
        {
            var error = Marshal.GetLastWin32Error();
            ExitFullScreen();
            throw new Win32Exception(error);
        }
        UpdateFullscreenButton();
        QueueViewportSave();
    }

    private void ExitFullScreen()
    {
        _isFullScreen = false;
        ApplyWindowFrame(_viewport.ShowWindowFrame);
        WindowState = WindowState.Normal;
        ResizeMode = _fullscreenRestoreResizeMode;
        MinWidth = _fullscreenRestoreMinimum.Width;
        MinHeight = _fullscreenRestoreMinimum.Height;
        Left = _fullscreenRestoreBounds.Left;
        Top = _fullscreenRestoreBounds.Top;
        Width = _fullscreenRestoreBounds.Width;
        Height = _fullscreenRestoreBounds.Height;
        WindowState = _fullscreenRestoreState;
        UpdateFullscreenButton();
        QueueViewportSave();
    }

    private void UpdateFullscreenButton()
    {
        BoardFullscreenButton.ToolTip = _isFullScreen ? "退出全屏 (F11)" : "全屏 (F11)";
        BoardFullscreenIcon.Data = Geometry.Parse(_isFullScreen
            ? "M1,5 H5 V1 M11,1 V5 H15 M15,11 H11 V15 M5,15 V11 H1"
            : "M1,6 V1 H6 M10,1 H15 V6 M15,10 V15 H10 M6,15 H1 V10");
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFullscreenWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
