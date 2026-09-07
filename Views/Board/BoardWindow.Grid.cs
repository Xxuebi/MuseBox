using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Models;
using ScreenshotCollector.Controls;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private bool _grayscaleEnabled;
    private readonly Dictionary<BitmapSource, BitmapSource> _grayscaleSources =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BitmapSource, BitmapSource> _colorSources =
        new(ReferenceEqualityComparer.Instance);

    private void OnGridStyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value } ||
            !Enum.TryParse<BoardGridStyle>(value, out var style)) return;
        _viewport.GridStyle = style;
        UpdateGridMenuState();
        UpdateGridVisual();
        QueueViewportSave();
        BoardStatus.Text = style switch
        {
            BoardGridStyle.Lines => "已显示线网格",
            BoardGridStyle.Dots => "已显示点网格",
            _ => "已隐藏网格"
        };
    }

    private void OnSnapToGridClick(object sender, RoutedEventArgs e)
    {
        _viewport.SnapToGrid = !_viewport.SnapToGrid;
        UpdateGridMenuState();
        QueueViewportSave();
        BoardStatus.Text = _viewport.SnapToGrid ? "移动元素时将吸附到网格" : "已关闭网格吸附";
    }

    private void UpdateGridMenuState()
    {
        GridNoneMenuItem.IsChecked = _viewport.GridStyle == BoardGridStyle.None;
        GridLinesMenuItem.IsChecked = _viewport.GridStyle == BoardGridStyle.Lines;
        GridDotsMenuItem.IsChecked = _viewport.GridStyle == BoardGridStyle.Dots;
        SnapToGridMenuItem.IsChecked = _viewport.SnapToGrid;
        GrayscaleMenuItem.IsChecked = _grayscaleEnabled;
    }

    private void UpdateGridVisual()
    {
        var color = Color.FromRgb(122, 122, 122);
        try { color = (Color)ColorConverter.ConvertFromString(_viewport.BackgroundColor); }
        catch { }
        GridOverlay.SetViewport(_viewport.GridStyle, _viewport.GridSpacing, _viewZoom,
            _viewPanX, _viewPanY, color);
    }

    private void OnResetCameraClick(object sender, RoutedEventArgs e)
    {
        _viewPanX = BoardSurface.ActualWidth / 2;
        _viewPanY = BoardSurface.ActualHeight / 2;
        ApplyViewportTransform();
        UpdateResizeHandles();
        QueueViewportSave();
        BoardStatus.Text = "已重置相机位置";
    }

    private void OnResetCameraZoomClick(object sender, RoutedEventArgs e)
    {
        var center = new Point(BoardSurface.ActualWidth / 2, BoardSurface.ActualHeight / 2);
        var worldCenter = ScreenToWorld(center);
        _viewZoom = 1;
        _viewPanX = center.X - worldCenter.X;
        _viewPanY = center.Y - worldCenter.Y;
        ApplyViewportTransform();
        UpdateResizeHandles();
        QueueViewportSave();
        BoardStatus.Text = "已将相机缩放重置为 100%";
    }

    private async void OnGrayscaleClick(object sender, RoutedEventArgs e)
    {
        await FlushPendingDrawingAsync();
        await CommitTextEditingAsync();
        _grayscaleEnabled = !_grayscaleEnabled;
        ApplyBackground(_viewport.BackgroundColor, _viewport.WindowOpacity);
        RenderItems();
        UpdateSelectionVisuals();
        UpdateGridMenuState();
        BoardStatus.Text = _grayscaleEnabled ? "已开启画板去色显示" : "已恢复画板彩色显示";
    }

    private void ApplySnappedDrag(Point current)
    {
        if (_gestureSnapshot is null) return;
        var originals = _gestureSnapshot.Images.Cast<BoardElement>()
            .Concat(_gestureSnapshot.TextItems).Concat(_gestureSnapshot.Drawings)
            .Where(element => _selected.Contains(element.Id)).ToArray();
        if (originals.Length == 0) return;
        var bounds = GetBounds(originals);
        if (SelectedGroup() is { BackgroundVisible: true } group)
            bounds.Inflate(Math.Clamp(group.FramePadding, 0, 10000), Math.Clamp(group.FramePadding, 0, 10000));
        var dx = (current.X - _mouseStart.X) / _viewZoom;
        var dy = (current.Y - _mouseStart.Y) / _viewZoom;
        var spacing = BoardGridVisual.CalculateVisibleWorldSpacing(_viewport.GridSpacing, _viewZoom);
        dx += Math.Round((bounds.Left + dx) / spacing, MidpointRounding.AwayFromZero) * spacing -
              (bounds.Left + dx);
        dy += Math.Round((bounds.Top + dy) / spacing, MidpointRounding.AwayFromZero) * spacing -
              (bounds.Top + dy);
        foreach (var original in originals)
        {
            var live = AllElements.First(element => element.Id == original.Id);
            live.X = original.X + dx;
            live.Y = original.Y + dy;
            var visual = _visuals[live.Id].Border;
            Canvas.SetLeft(visual, live.X);
            Canvas.SetTop(visual, live.Y);
        }
    }

    private Brush ParseDisplayBrush(string value, Brush fallback)
    {
        var brush = ParseBrush(value, fallback);
        return _grayscaleEnabled ? ToGrayscaleBrush(brush) : brush;
    }

    private static Brush ToGrayscaleBrush(Brush brush)
    {
        if (brush is not SolidColorBrush solid) return brush;
        var color = solid.Color;
        var gray = (byte)Math.Clamp(Math.Round(.2126 * color.R + .7152 * color.G + .0722 * color.B), 0, 255);
        var result = new SolidColorBrush(Color.FromArgb(color.A, gray, gray, gray));
        result.Freeze();
        return result;
    }

    private static void ApplyDocumentGrayscale(FlowDocument document)
    {
        ApplyContentBrushes(document);
        static void ApplyContentBrushes(DependencyObject root)
        {
            if (root.GetValue(TextElement.ForegroundProperty) is Brush foreground)
                root.SetValue(TextElement.ForegroundProperty, ToGrayscaleBrush(foreground));
            if (root.GetValue(TextElement.BackgroundProperty) is Brush background)
                root.SetValue(TextElement.BackgroundProperty, ToGrayscaleBrush(background));
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                ApplyContentBrushes(child);
        }
    }

    private ImageSource? GetDisplayImageSource(ImageSource? source)
    {
        if (!_grayscaleEnabled || source is not BitmapSource bitmap) return GetOriginalImageSource(source);
        if (_colorSources.ContainsKey(bitmap)) return bitmap;
        if (_grayscaleSources.TryGetValue(bitmap, out var cached)) return cached;
        var converted = CreateGrayscaleSource(bitmap);
        _grayscaleSources[bitmap] = converted;
        _colorSources[converted] = bitmap;
        return converted;
    }

    private ImageSource? GetOriginalImageSource(ImageSource? source) =>
        source is BitmapSource bitmap && _colorSources.TryGetValue(bitmap, out var original) ? original : source;

    private static BitmapSource CreateGrayscaleSource(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var gray = (byte)Math.Clamp(Math.Round(.0722 * pixels[index] +
                .7152 * pixels[index + 1] + .2126 * pixels[index + 2]), 0, 255);
            pixels[index] = pixels[index + 1] = pixels[index + 2] = gray;
        }
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
