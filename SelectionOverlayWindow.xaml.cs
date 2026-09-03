using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class SelectionOverlayWindow : Window
{
    private readonly CapturedScreen _screen;
    private System.Windows.Point _dragStart;
    private bool _isDragging;
    private bool _hasFinished;

    public SelectionOverlayWindow(CapturedScreen screen)
    {
        InitializeComponent();
        _screen = screen;
        ScreenImage.Source = screen.Preview;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += (_, _) => UpdateSelectionVisual(Rect.Empty);
    }

    public event EventHandler<SelectionCompletedEventArgs>? SelectionCompleted;

    public event EventHandler? SelectionCancelled;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var bounds = _screen.Bounds;
        SetWindowPos(
            new WindowInteropHelper(this).Handle,
            HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpShowWindow);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSelectionVisual(Rect.Empty);
        Focus();
        Keyboard.Focus(this);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_hasFinished)
        {
            return;
        }

        _dragStart = e.GetPosition(SelectionSurface);
        _isDragging = true;
        GuideText.Text = "拖动鼠标框选区域 · Esc 或右键取消";
        SelectionSurface.CaptureMouse();
        UpdateSelectionVisual(new Rect(_dragStart, _dragStart));
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        UpdateSelectionVisual(RegionMath.Normalize(
            _dragStart,
            e.GetPosition(SelectionSurface),
            SelectionSurface.ActualWidth,
            SelectionSurface.ActualHeight));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || _hasFinished)
        {
            return;
        }

        _isDragging = false;
        SelectionSurface.ReleaseMouseCapture();
        var selection = RegionMath.Normalize(
            _dragStart,
            e.GetPosition(SelectionSurface),
            SelectionSurface.ActualWidth,
            SelectionSurface.ActualHeight);
        var pixelBounds = RegionMath.ToPixelRectangle(
            selection,
            SelectionSurface.ActualWidth,
            SelectionSurface.ActualHeight,
            _screen.Bitmap.Width,
            _screen.Bitmap.Height);

        if (pixelBounds.Width < 4 || pixelBounds.Height < 4)
        {
            GuideText.Text = "选区太小，请重新拖动 · Esc 或右键取消";
            UpdateSelectionVisual(Rect.Empty);
            return;
        }

        _hasFinished = true;
        SelectionCompleted?.Invoke(this, new SelectionCompletedEventArgs(_screen, pixelBounds));
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelSelection();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelSelection();
            e.Handled = true;
        }
    }

    private void CancelSelection()
    {
        if (_hasFinished)
        {
            return;
        }

        _hasFinished = true;
        SelectionCancelled?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_hasFinished)
        {
            _hasFinished = true;
            SelectionCancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateSelectionVisual(Rect selection)
    {
        var surfaceWidth = Math.Max(0, SelectionSurface.ActualWidth);
        var surfaceHeight = Math.Max(0, SelectionSurface.ActualHeight);

        if (selection.IsEmpty || selection.Width <= 0 || selection.Height <= 0)
        {
            SetRectangle(TopShade, 0, 0, surfaceWidth, surfaceHeight);
            SetRectangle(BottomShade, 0, 0, 0, 0);
            SetRectangle(LeftShade, 0, 0, 0, 0);
            SetRectangle(RightShade, 0, 0, 0, 0);
            SelectionBorder.Visibility = Visibility.Collapsed;
            return;
        }

        SetRectangle(TopShade, 0, 0, surfaceWidth, selection.Top);
        SetRectangle(BottomShade, 0, selection.Bottom, surfaceWidth, surfaceHeight - selection.Bottom);
        SetRectangle(LeftShade, 0, selection.Top, selection.Left, selection.Height);
        SetRectangle(RightShade, selection.Right, selection.Top, surfaceWidth - selection.Right, selection.Height);
        SetRectangle(SelectionBorder, selection.Left, selection.Top, selection.Width, selection.Height);
        SelectionBorder.Visibility = Visibility.Visible;
    }

    private static void SetRectangle(Shape rectangle, double left, double top, double width, double height)
    {
        Canvas.SetLeft(rectangle, Math.Max(0, left));
        Canvas.SetTop(rectangle, Math.Max(0, top));
        rectangle.Width = Math.Max(0, width);
        rectangle.Height = Math.Max(0, height);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}

public sealed class SelectionCompletedEventArgs : EventArgs
{
    public SelectionCompletedEventArgs(CapturedScreen screen, Rectangle pixelBounds)
    {
        Screen = screen;
        PixelBounds = pixelBounds;
    }

    public CapturedScreen Screen { get; }

    public Rectangle PixelBounds { get; }
}
