using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using ScreenshotCollector.Models;

namespace ScreenshotCollector;

public partial class MainWindow
{
    private bool _mainStateReady;
    private bool _restoringMainState;
    private bool _startupStatePrepared;

    // Apply saved geometry before the first visible frame, not after Loaded.
    public void PrepareStartupWindow(AppSettings settings)
    {
        _settings = settings.Copy();
        RestoreMainWindowState();
        _startupStatePrepared = true;
    }

    public void ShowCollectorWindow()
    {
        if (_mainClosed) return;
        Show();
        WindowState = WindowState.Normal;
        EnsureMainWindowOnScreen();
        Activate();
    }

    private async void OnAddDrawerClick(object sender, RoutedEventArgs e) => await AddDrawerAsync();

    private async Task AddDrawerAsync()
    {
        if (_isBusy || IsCollectionMode || CollectionTransitioning) return;
        SetBusy(true);
        try
        {
            foreach (var editing in _drawers.Where(x => x.IsEditing).ToArray())
                await SaveDrawerNameAsync(editing.Id);
            var drawer = await _repository.AddNextDrawerAsync();
            var before = CaptureDrawerPositions();
            _drawers.Add(new DrawerCardModel(drawer.Id, drawer.DisplayName, null) { ShowLetter = _settings.ShowDrawerLetters });
            // Keep existing thumbnails and focus intact instead of rebuilding the list.
            await AnimateNewDrawerAsync(drawer.Id, before);
            SetStatus($"已新建抽屉 {drawer.Id} · 上方收集图片，下方打开画板", false);
        }
        catch (Exception error) { SetStatus($"新建失败：{Friendly(error)}", true); }
        finally { SetBusy(false); }
    }

    private void RestoreMainWindowState()
    {
        _restoringMainState = true;
        try
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            ApplyMainRenderMode();
            ApplyInitialCollectionMode();
            Topmost = _settings.MainTopmost;
            if (_settings.MainLeft is double left && double.IsFinite(left) &&
                _settings.MainTop is double top && double.IsFinite(top))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
            var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var fromPixels = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var available = fromPixels.Transform(new Vector(work.Width, work.Height));
            ApplySavedMainSize(new Size(Math.Abs(available.X), Math.Abs(available.Y)));
            EnsureMainWindowOnScreen();
        }
        finally { _restoringMainState = false; _mainStateReady = true; }
    }

    private void ApplyMainRenderMode()
    {
        if (_windowSource?.CompositionTarget is { } target)
            target.RenderMode = _settings.CompatibleRendering ? RenderMode.SoftwareOnly : RenderMode.Default;
    }

    private void EnsureMainWindowOnScreen()
    {
        if (_mainClosed || WindowState != WindowState.Normal) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetMainWindowRect(handle, out var rectangle)) return;
        // Work in physical pixels so mixed-DPI and negative-position monitors
        // are not compared against WPF device-independent coordinates.
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var width = Math.Min(Math.Max(1, rectangle.Right - rectangle.Left), area.Width);
        var height = Math.Min(Math.Max(1, rectangle.Bottom - rectangle.Top), area.Height);
        var outside = rectangle.Right <= area.Left || rectangle.Left >= area.Right ||
            rectangle.Bottom <= area.Top || rectangle.Top >= area.Bottom;
        var left = outside ? area.Left + (area.Width - width) / 2 : Math.Clamp(rectangle.Left, area.Left, area.Right - width);
        var top = outside ? area.Top + (area.Height - height) / 2 : Math.Clamp(rectangle.Top, area.Top, area.Bottom - height);
        if (left == rectangle.Left && top == rectangle.Top &&
            width == rectangle.Right - rectangle.Left && height == rectangle.Bottom - rectangle.Top) return;
        SetMainWindowPos(handle, IntPtr.Zero, left, top, width, height, 0x0014); // no z-order or activation change
    }

    private void OnMainDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_mainClosed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible && WindowState == WindowState.Normal) EnsureMainWindowOnScreen();
        }));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MainNativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetMainWindowRect(IntPtr handle, out MainNativeRect rectangle);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool SetMainWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private void ApplySavedMainSize(Size available)
    {
        var width = double.IsFinite(_settings.MainWidth) ? _settings.MainWidth : 360;
        var height = double.IsFinite(_settings.MainHeight) ? _settings.MainHeight : 500;
        Width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, available.Width));
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, available.Height));
    }

    // Native non-client resizing preserves Windows' DPI handling, minimum size,
    // drag capture and resize cursors without adding a square system border.
    internal static int MainResizeHitTest(Point point, Size size)
    {
        if (point.X < 0 || point.Y < 0 || point.X > size.Width || point.Y > size.Height) return 0;
        const double edge = 6;
        const double corner = 16;
        var left = point.X <= edge;
        var right = point.X >= size.Width - edge;
        var top = point.Y <= edge;
        var bottom = point.Y >= size.Height - edge;
        if (point.X <= corner && point.Y <= corner) return 13; // HTTOPLEFT
        if (point.X >= size.Width - corner && point.Y <= corner) return 14;
        if (point.X <= corner && point.Y >= size.Height - corner) return 16;
        if (point.X >= size.Width - corner && point.Y >= size.Height - corner) return 17;
        if (left) return 10;
        if (right) return 11;
        if (top) return 12;
        if (bottom) return 15;
        return 0;
    }
}
