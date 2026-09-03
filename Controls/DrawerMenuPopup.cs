using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace ScreenshotCollector.Controls;

// Keep the same HWND alive throughout dismissal. Native ContextMenu capture and
// snapshot hand-offs otherwise steal opener hover and can produce a blank frame.
public sealed class DrawerMenuPopup : Popup
{
    private readonly Border _surface;
    private readonly TranslateTransform _slide = new();
    private int _generation;
    private double _direction = 6;
    public StackPanel Actions { get; } = new();
    public bool IsExpanded { get; private set; }

    public DrawerMenuPopup()
    {
        AllowsTransparency = true;
        StaysOpen = true;
        Focusable = false;
        PopupAnimation = PopupAnimation.None;
        Placement = PlacementMode.Bottom;
        _surface = new Border
        {
            Width = 190, Padding = new Thickness(5), Margin = new Thickness(8),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = .18 },
            Child = Actions, RenderTransform = _slide
        };
        _surface.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        _surface.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        Child = _surface;
        Closed += (_, _) => { ++_generation; IsExpanded = false; };
    }

    public void ShowMenu()
    {
        ++_generation;
        IsExpanded = true;
        _surface.IsHitTestVisible = true;
        if (!IsOpen)
        {
            _surface.BeginAnimation(UIElement.OpacityProperty, null);
            _slide.BeginAnimation(TranslateTransform.YProperty, null);
            _surface.Opacity = 0;
            _slide.Y = -6;
            IsOpen = true;
            _surface.UpdateLayout();
            if (PlacementTarget is FrameworkElement target && PresentationSource.FromVisual(_surface) is not null)
                _direction = _surface.PointToScreen(new Point(0, _surface.ActualHeight / 2)).Y <
                    target.PointToScreen(new Point(0, target.ActualHeight / 2)).Y ? -6 : 6;
            _slide.Y = -_direction;
        }
        Animate(1, 0, 160, null);
    }

    public void Dismiss(bool animate = true)
    {
        if (!IsOpen || !IsExpanded) return;
        IsExpanded = false;
        _surface.IsHitTestVisible = false;
        var generation = ++_generation;
        if (!animate) { IsOpen = false; return; }
        Animate(0, -_direction, 130, () =>
        {
            if (generation == _generation && !IsExpanded) IsOpen = false;
        });
    }

    private void Animate(double opacity, double y, int milliseconds, Action? completed)
    {
        var easing = new CubicEase { EasingMode = opacity == 0 ? EasingMode.EaseIn : EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var fade = new DoubleAnimation(_surface.Opacity, opacity, duration) { EasingFunction = easing };
        if (completed is not null) fade.Completed += (_, _) => completed();
        _surface.BeginAnimation(UIElement.OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(_slide.Y, y, duration) { EasingFunction = easing });
    }
}
