using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ScreenshotCollector;

public partial class MainWindow
{
    private Border? _drawerDragPreview;
    private TranslateTransform? _drawerDragTranslation;
    private string? _drawerDragPreviewId;
    private Vector _drawerDragGrabOffset;

    private Dictionary<string, Point> CaptureDrawerPositions()
    {
        var positions = new Dictionary<string, Point>();
        foreach (var model in _drawers)
            if (DrawerList.ItemContainerGenerator.ContainerFromItem(model) is FrameworkElement container)
                positions[model.Id] = container.TranslatePoint(new Point(), DrawerScroll);
        return positions;
    }

    private Point DrawerLayoutOrigin(FrameworkElement container)
    {
        var point = container.TranslatePoint(new Point(), DrawerScroll);
        var offset = container.RenderTransform.Value;
        return new Point(point.X - offset.OffsetX, point.Y - offset.OffsetY);
    }

    private void AnimateDrawerLayout(IReadOnlyDictionary<string, Point> before)
    {
        DrawerList.UpdateLayout();
        foreach (var model in _drawers)
        {
            if (DrawerList.ItemContainerGenerator.ContainerFromItem(model) is not FrameworkElement container) continue;
            var destination = DrawerLayoutOrigin(container);
            var offset = model.Id != _drawerDragPreviewId && before.TryGetValue(model.Id, out var previous)
                ? previous - destination : new Vector();
            var translation = new TranslateTransform();
            container.RenderTransform = translation;
            AnimateDrawerCoordinate(translation, TranslateTransform.XProperty, offset.X, 0, 210);
            AnimateDrawerCoordinate(translation, TranslateTransform.YProperty, offset.Y, 0, 210);
        }
    }

    private static void AnimateDrawerCoordinate(TranslateTransform target, DependencyProperty property,
        double from, double to, double milliseconds)
    {
        target.SetValue(property, to);
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void StartDrawerDragVisual(string id, Point pointer)
    {
        ClearDrawerDragVisual();
        var model = _drawers.First(x => x.Id == id);
        if (DrawerList.ItemContainerGenerator.ContainerFromItem(model) is not FrameworkElement container ||
            container.ActualWidth <= 0 || container.ActualHeight <= 0) return;
        // Snapshot once, before fading the placeholder. Re-rendering a live visual
        // brush on every pointer event is unnecessary and can flash during moves.
        var size = new Size(container.ActualWidth, container.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(container);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(container), null, new Rect(size));
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        var origin = container.TranslatePoint(new Point(), DrawerDragLayer);
        _drawerDragGrabOffset = DrawerScroll.TranslatePoint(pointer, DrawerDragLayer) - origin;
        _drawerDragTranslation = new TranslateTransform(origin.X, origin.Y);
        _drawerDragPreviewId = id;
        _drawerDragPreview = new Border
        {
            Width = size.Width, Height = size.Height, CornerRadius = new CornerRadius(12),
            Background = new ImageBrush(bitmap), RenderTransform = _drawerDragTranslation,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = .2 },
            IsHitTestVisible = false
        };
        DrawerDragLayer.Children.Add(_drawerDragPreview);
    }

    private void UpdateDrawerDragVisual(Point pointer)
    {
        if (_drawerDragTranslation is null || _draggingDrawerId is null) return;
        var position = DrawerScroll.TranslatePoint(pointer, DrawerDragLayer) - _drawerDragGrabOffset;
        // Position updates are composited by WPF each frame; no new bitmaps/layout
        // passes and no animation lag between the pointer and the grabbed handle.
        _drawerDragTranslation.X = position.X;
        _drawerDragTranslation.Y = position.Y;
    }

    private async Task SettleDrawerDragVisualAsync()
    {
        if (_drawerDragTranslation is null || !IsVisible) return;
        var model = _drawers.FirstOrDefault(x => x.Id == _drawerDragPreviewId);
        if (model is null || DrawerList.ItemContainerGenerator.ContainerFromItem(model) is not FrameworkElement container) return;
        var destination = DrawerScroll.TranslatePoint(DrawerLayoutOrigin(container), DrawerDragLayer);
        AnimateDrawerCoordinate(_drawerDragTranslation, TranslateTransform.XProperty, _drawerDragTranslation.X, destination.X, 180);
        AnimateDrawerCoordinate(_drawerDragTranslation, TranslateTransform.YProperty, _drawerDragTranslation.Y, destination.Y, 180);
        await Task.Delay(180);
    }

    private void ClearDrawerDragVisual()
    {
        DrawerDragLayer.Children.Clear();
        _drawerDragPreview = null;
        _drawerDragTranslation = null;
        _drawerDragPreviewId = null;
    }
}
