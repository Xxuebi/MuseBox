using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace ScreenshotCollector.Services;

// One policy for context menus, toolbar popups, submenus and combo dropdowns.
// Native popup dismissal remains immediate for input/focus. A short-lived,
// non-interactive snapshot supplies the exit animation without intercepting clicks.
public static class PopupTransitions
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PopupTransitions), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(PopupTransitions));
    public static readonly DependencyProperty PanelPlacementProperty = DependencyProperty.RegisterAttached(
        "PanelPlacement", typeof(PlacementMode), typeof(PopupTransitions), new PropertyMetadata(PlacementMode.Bottom));
    public static void SetPanelPlacement(DependencyObject element, PlacementMode value) => element.SetValue(PanelPlacementProperty, value);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void ShowPanel(FrameworkElement element, bool animate = true)
    {
        var state = element.GetValue(StateProperty) as State ?? new State(element);
        element.SetValue(StateProperty, state);
        state.Open(animate);
    }
    public static void HidePanel(FrameworkElement element) => (element.GetValue(StateProperty) as State)?.Close();
    public static void ShowScalePanel(FrameworkElement element, double fromX, double fromY)
    {
        var state = element.GetValue(StateProperty) as State ?? new State(element);
        element.SetValue(StateProperty, state);
        state.Open(scaleFrom: new Vector(fromX, fromY));
    }
    public static void Reposition(Popup popup)
    {
        if (!popup.IsOpen) return;
        // WPF does not follow a PlacementTarget's render translation automatically.
        var offset = popup.HorizontalOffset;
        popup.SetCurrentValue(Popup.HorizontalOffsetProperty, offset + .001);
        popup.SetCurrentValue(Popup.HorizontalOffsetProperty, offset);
    }

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not true || element.GetValue(StateProperty) is State) return;
        var state = new State(element);
        element.SetValue(StateProperty, state);
        if (element is Popup popup)
        {
            popup.PopupAnimation = PopupAnimation.None;
            popup.Opened += (_, _) => state.Open();
            popup.Closed += (_, _) => state.Close();
        }
        else if (element is ContextMenu menu)
        {
            menu.Opened += (_, _) =>
            {
                // ContextMenu creates its own Popup, outside our XAML templates.
                // Its system fade would otherwise delay Closed and replay a fade.
                if (menu.Parent is Popup nativePopup) nativePopup.PopupAnimation = PopupAnimation.None;
                state.Open();
            };
            menu.Closed += (_, _) => state.Close();
        }
    }

    private sealed class State(DependencyObject owner)
    {
        private FrameworkElement? _root;
        private Transform? _originalTransform;
        private Point? _screenOrigin;
        private DpiScale _dpi;
        private Vector _direction;
        private Popup? _ghost;
        private DispatcherTimer? _timer;
        private int _generation;
        private HwndSource? _source;
        private TranslateTransform? _slide;
        private ScaleTransform? _scale;
        private Vector? _scaleFrom;
        private double _originalOpacity;
        private bool _exitPrepared;
        private bool _preparedBeforeNativeHide;
        private Rect? _targetBounds;

        public void Open(bool animate = true, Vector? scaleFrom = null)
        {
            StopGhost();
            DetachSource();
            var generation = ++_generation;
            var root = owner is Popup popup ? popup.Child as FrameworkElement : owner as FrameworkElement;
            if (root is null) return;
            _root = root;
            _exitPrepared = false;
            _preparedBeforeNativeHide = false;
            _scaleFrom = scaleFrom;
            _originalTransform = root.RenderTransform;
            _originalOpacity = (double)root.GetAnimationBaseValue(UIElement.OpacityProperty);
            _direction = OpeningDirection();
            _slide = scaleFrom is null ? new TranslateTransform(-_direction.X, -_direction.Y) : null;
            _scale = scaleFrom is Vector from ? new ScaleTransform(from.X, from.Y) : null;
            if (animate)
            {
                root.RenderTransform = new TransformGroup { Children = { _originalTransform, (Transform?)_scale ?? _slide! } };
                // Hide synchronously, before the native window's first visible frame.
                root.BeginAnimation(UIElement.OpacityProperty, null);
                root.SetCurrentValue(UIElement.OpacityProperty, 0d);
            }
            root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (generation != _generation || !IsOpen()) return;
                root.UpdateLayout();
                if (PresentationSource.FromVisual(root) is null) return;
                _dpi = VisualTreeHelper.GetDpi(root);
                _screenOrigin = root.PointToScreen(new Point());
                if (owner is Popup or ContextMenu && PresentationSource.FromVisual(root) is HwndSource source)
                {
                    _source = source;
                    source.AddHook(OnNativeWindowMessage);
                    CompositionTarget.Rendering += TrackPlacement;
                    _targetBounds = TargetBounds();
                }
                _direction = OpeningDirection();
                if (!animate) return;
                if (_scale is not null && scaleFrom is Vector start)
                {
                    Animate(_scale, ScaleTransform.ScaleXProperty, start.X, 1, 180);
                    Animate(_scale, ScaleTransform.ScaleYProperty, start.Y, 1, 180);
                }
                else if (_slide is not null)
                {
                    Animate(_slide, TranslateTransform.XProperty, -_direction.X, 0, 170);
                    Animate(_slide, TranslateTransform.YProperty, -_direction.Y, 0, 170);
                }
                root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, _originalOpacity, TimeSpan.FromMilliseconds(150)));
            }));
        }

        private FrameworkElement? Target => (owner is Popup p ? p.PlacementTarget ?? p.TemplatedParent as UIElement :
            owner is ContextMenu menu ? menu.PlacementTarget : null) as FrameworkElement;
        private Rect? TargetBounds()
        {
            var target = Target;
            return target is not null && PresentationSource.FromVisual(target) is not null
                ? new Rect(target.PointToScreen(new Point()), target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight)))
                : null;
        }
        private Vector OpeningDirection()
        {
            var placement = owner is Popup p ? p.Placement : owner is ContextMenu m ? m.Placement : (PlacementMode)owner.GetValue(PanelPlacementProperty);
            var direction = placement switch
            {
                PlacementMode.Top => new Vector(0, -6), PlacementMode.Right => new Vector(6, 0),
                PlacementMode.Left => new Vector(-6, 0), _ => new Vector(0, 6)
            };
            // Native popups may flip at a monitor edge. Follow the actual side.
            if (_root is not null && PresentationSource.FromVisual(_root) is not null && TargetBounds() is Rect target)
            {
                var center = _root.PointToScreen(new Point(_root.ActualWidth / 2, _root.ActualHeight / 2));
                if (placement is PlacementMode.Top or PlacementMode.Bottom)
                    direction.Y = center.Y < target.Top + target.Height / 2 ? -6 : 6;
                else if (placement is PlacementMode.Left or PlacementMode.Right)
                    direction.X = center.X < target.Left + target.Width / 2 ? -6 : 6;
            }
            return direction;
        }
        private void TrackPlacement(object? sender, EventArgs args)
        {
            if (!IsOpen() || TargetBounds() is not Rect bounds || _targetBounds == bounds) return;
            _targetBounds = bounds;
            if (owner is Popup popup) Reposition(popup);
            else if (owner is ContextMenu menu && menu.Parent is Popup native) Reposition(native);
        }
        private IntPtr OnNativeWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Prepare popup exit while the original HWND is still on screen. A
            // ContextMenu closes immediately: snapshotting that native window can
            // briefly redraw the menu after dismissal and look like a flash.
            if (message == 0x0046 && lParam != IntPtr.Zero && (Marshal.PtrToStructure<WindowPosition>(lParam).Flags & 0x80) != 0)
            {
                _preparedBeforeNativeHide = true;
                if (owner is not ContextMenu) PrepareExit();
            }
            return IntPtr.Zero;
        }
        private void DetachSource()
        {
            CompositionTarget.Rendering -= TrackPlacement;
            if (_source is not null) { _source.RemoveHook(OnNativeWindowMessage); _source = null; }
            _targetBounds = null;
        }

        private bool IsOpen() => owner is Popup popup ? popup.IsOpen :
            owner is ContextMenu menu ? menu.IsOpen : owner is FrameworkElement { Visibility: Visibility.Visible };

        public void Close()
        {
            ++_generation;
            if (owner is ContextMenu)
            {
                _preparedBeforeNativeHide = true;
                StopGhost();
            }
            else if (!_preparedBeforeNativeHide) PrepareExit();
            DetachSource();
            var root = _root;
            if (root is null) return;
            root.BeginAnimation(UIElement.OpacityProperty, null);
            root.SetCurrentValue(UIElement.OpacityProperty, _originalOpacity);
            if (_originalTransform is not null) root.RenderTransform = _originalTransform;
            _originalTransform = null;
            _screenOrigin = null;
        }

        private void PrepareExit()
        {
            var root = _root;
            if (_exitPrepared || root is null) return;
            _exitPrepared = true;
            var target = Target ?? owner as UIElement;
            if (_screenOrigin is null || root.Opacity < .03 || root.ActualWidth <= 0 || root.ActualHeight <= 0 ||
                target is not null && Window.GetWindow(target) is Window { IsVisible: false }) return;
            try
            {
                StopGhost();
                _direction = OpeningDirection();
                // Preserve the CURRENT opacity/transform. Explicit visual bounds
                // prevent VisualBrush from stretching shadows or dropping margins.
                var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                if (VisualTreeHelper.GetTransform(root) is Transform transform) bounds = transform.TransformBounds(bounds);
                bounds.Offset(VisualTreeHelper.GetOffset(root));
                bounds.Inflate(8, 8);
                var location = VisualTreeHelper.GetParent(root) is Visual parent && PresentationSource.FromVisual(parent) is not null
                    ? parent.PointToScreen(bounds.TopLeft) : _screenOrigin.Value - new Vector(8 * _dpi.DpiScaleX, 8 * _dpi.DpiScaleY);
                var width = bounds.Width;
                var height = bounds.Height;
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = bounds, Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * _dpi.DpiScaleX)),
                    Math.Max(1, (int)Math.Ceiling(height * _dpi.DpiScaleY)), _dpi.PixelsPerInchX, _dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(drawing); bitmap.Freeze();
                var slide = new TranslateTransform();
                var shrink = new ScaleTransform(1, 1);
                var duration = _scaleFrom is null ? 130 : 160;
                var image = new System.Windows.Controls.Image { Source = bitmap, Width = width, Height = height,
                    RenderTransform = _scaleFrom is null ? slide : shrink, RenderTransformOrigin = new Point(.5, .5),
                    IsHitTestVisible = false };
                if (_scaleFrom is Vector from)
                {
                    Animate(shrink, ScaleTransform.ScaleXProperty, 1, from.X / Math.Max(.01, _scale?.ScaleX ?? 1), duration, true);
                    Animate(shrink, ScaleTransform.ScaleYProperty, 1, from.Y / Math.Max(.01, _scale?.ScaleY ?? 1), duration, true);
                }
                else
                {
                    Animate(slide, TranslateTransform.XProperty, 0, -_direction.X - (_slide?.X ?? 0), duration, true);
                    Animate(slide, TranslateTransform.YProperty, 0, -_direction.Y - (_slide?.Y ?? 0), duration, true);
                }
                // Install the first frame BEFORE opening the exit HWND.
                image.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(duration)));
                _ghost = new Popup
                {
                    AllowsTransparency = true, StaysOpen = true, Focusable = false, IsHitTestVisible = false,
                    PopupAnimation = PopupAnimation.None,
                    Placement = PlacementMode.AbsolutePoint, HorizontalOffset = location.X / _dpi.DpiScaleX,
                    VerticalOffset = location.Y / _dpi.DpiScaleY, Child = image
                };
                _ghost.IsOpen = true;
                if (PresentationSource.FromVisual(image) is HwndSource source)
                {
                    // Keep the snapshot at the exact physical screen position on
                    // mixed-DPI displays and make its native window click-through.
                    var styles = GetWindowLong(source.Handle, -20);
                    SetWindowLong(source.Handle, -20, styles | 0x20 | 0x08000000);
                    SetWindowPos(source.Handle, IntPtr.Zero, (int)Math.Round(location.X),
                        (int)Math.Round(location.Y), 0, 0, 0x0015);
                }
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(duration + 15) };
                _timer.Tick += OnGhostFinished;
                _timer.Start();
            }
            catch (InvalidOperationException) { StopGhost(); }
        }

        private void OnGhostFinished(object? sender, EventArgs args) => StopGhost();
        private void StopGhost()
        {
            if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnGhostFinished; _timer = null; }
            if (_ghost is not null) { _ghost.IsOpen = false; _ghost.Child = null; _ghost = null; }
        }
    }

    private static void Animate(Animatable target, DependencyProperty property, double from, double to, int duration, bool exiting = false)
    {
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration))
        { EasingFunction = new CubicEase { EasingMode = exiting ? EasingMode.EaseIn : EasingMode.EaseOut } });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Hwnd, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
