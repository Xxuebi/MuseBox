using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ScreenshotCollector.Controls;

namespace ScreenshotCollector;

public partial class MainWindow
{
    public static readonly DependencyProperty IsCollectionModeProperty = DependencyProperty.Register(
        nameof(IsCollectionMode), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsCollectionMode => (bool)GetValue(IsCollectionModeProperty);
    public static readonly DependencyProperty CollectionTransitioningProperty = DependencyProperty.Register(
        nameof(CollectionTransitioning), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool CollectionTransitioning => (bool)GetValue(CollectionTransitioningProperty);
    public static readonly DependencyProperty CollectionAccessoryOpacityProperty = DependencyProperty.Register(
        nameof(CollectionAccessoryOpacity), typeof(double), typeof(MainWindow), new PropertyMetadata(1d));
    public double CollectionAccessoryOpacity => (double)GetValue(CollectionAccessoryOpacityProperty);
    public static readonly DependencyProperty CollectionProgressProperty = DependencyProperty.Register(
        nameof(CollectionProgress), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0d, (owner, _) => ((MainWindow)owner).UpdateCollectionLayout()));
    public double CollectionProgress => (double)GetValue(CollectionProgressProperty);

    private int _collectionGeneration;
    private double _normalCollectionScrollOffset;
    private FrameworkElement? _enteringDrawer;
    private TaskCompletionSource? _collectionCompletion;
    private TaskCompletionSource? _drawerEntranceCompletion;
    private double _normalCollectionWindowHeight;
    private double _normalCollectionMinHeight = 280;
    private double _collectionWindowTarget;
    private readonly DispatcherTimer _collectionScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _scrollTweenFrom, _scrollTweenTarget, _scrollTweenMilliseconds;
    private long _scrollTweenStarted;
    private readonly Dictionary<string, Popup> _collectionNotices = new();

    private void InitializeCollectionMode()
    {
        _collectionScrollTimer.Tick += (_, _) =>
        {
            var t = Math.Clamp(Stopwatch.GetElapsedTime(_scrollTweenStarted).TotalMilliseconds / _scrollTweenMilliseconds, 0, 1);
            var eased = t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            DrawerScroll.ScrollToVerticalOffset(_scrollTweenFrom + (_scrollTweenTarget - _scrollTweenFrom) * eased);
            if (t >= 1) _collectionScrollTimer.Stop();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) FinishCollectionVisuals(); };
        Closed += (_, _) => FinishCollectionVisuals();
    }

    private void UpdateCollectionLayout()
    {
        var progress = Math.Clamp(CollectionProgress, 0, 1);
        SetValue(CollectionAccessoryOpacityProperty, 1 - progress);
        if (AddDrawerRow is null) return;
        AddDrawerRow.Height = new GridLength(42 * (1 - progress));
        AddDrawerHost.Visibility = progress >= 1 ? Visibility.Collapsed : Visibility.Visible;
        AddDrawerButton.IsEnabled = !IsCollectionMode && !CollectionTransitioning && progress < 1;
    }

    private void ApplyInitialCollectionMode()
    {
        _normalCollectionWindowHeight = double.IsFinite(_settings.MainHeight) ? _settings.MainHeight : 500;
        _normalCollectionMinHeight = 280;
        SetValue(IsCollectionModeProperty, _settings.ImmersiveCollectionEnabled);
        MinHeight = IsCollectionMode ? 160 : _normalCollectionMinHeight;
        SetValue(CollectionProgressProperty, IsCollectionMode ? 1d : 0d);
        UpdateCollectionModeButton();
    }

    private void ApplyInitialCollectionWindowHeight()
    {
        if (!IsCollectionMode) return;
        var normal = Math.Max(_normalCollectionMinHeight, _normalCollectionWindowHeight);
        _collectionWindowTarget = CalculateCompactWindowHeight(normal);
        MinHeight = 160;
        Height = _collectionWindowTarget;
    }

    private void UpdateCollectionModeButton()
    {
        var label = IsCollectionMode ? "退出沉浸收集" : "开启沉浸收集";
        CollectionModeButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(CollectionModeButton, label);
        CollectionModeButton.Background = IsCollectionMode ? (Brush)FindResource("AccentSubtleBrush") : Brushes.Transparent;
        CollectionModeButton.Foreground = (Brush)FindResource("TextBrush");
    }

    private async void OnCollectionModeClick(object sender, RoutedEventArgs e)
    {
        try { await SetCollectionModeAsync(!IsCollectionMode); }
        catch (Exception error) { SetStatus($"切换收集模式失败：{Friendly(error)}", true); }
    }

    private async Task SetCollectionModeAsync(bool enabled)
    {
        if (_isBusy || _mainClosed || enabled == IsCollectionMode) return;
        foreach (var model in _drawers.Where(x => x.IsEditing).ToArray()) await SaveDrawerNameAsync(model.Id);
        if (_isBusy || _mainClosed || enabled == IsCollectionMode) return;
        CancelDrawerGesture();
        ClearCollectionFeedback();
        if (enabled && !CollectionTransitioning)
        {
            _normalCollectionScrollOffset = DrawerScroll.VerticalOffset;
            _normalCollectionWindowHeight = ActualHeight > 0 ? ActualHeight : Height;
            _normalCollectionMinHeight = MinHeight;
        }
        var generation = ++_collectionGeneration;
        _collectionCompletion?.TrySetResult();
        _collectionCompletion = null;
        var from = CollectionProgress;
        BeginCollectionWindowTransition(enabled);
        SetValue(IsCollectionModeProperty, enabled);
        _settings.ImmersiveCollectionEnabled = enabled;
        SetValue(CollectionTransitioningProperty, true);
        UpdateCollectionModeButton();
        BeginAnimation(CollectionProgressProperty, null);
        // A new WPF clock is not active until the next render tick. Keep the
        // current value as its base so rapid reversals cannot flash the endpoint.
        SetValue(CollectionProgressProperty, IsVisible ? from : enabled ? 1d : 0d);
        if (IsVisible)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _collectionCompletion = completion;
            var animation = new DoubleAnimation(from, enabled ? 1 : 0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            animation.Completed += (_, _) => completion.TrySetResult();
            BeginAnimation(CollectionProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
            StartDrawerScroll(enabled ? 0 : _normalCollectionScrollOffset, 300);
            // Wait for the render clock, not a wall-clock delay which can expire
            // before the animation finishes on a busy UI thread.
            await completion.Task;
        }
        if (generation != _collectionGeneration || _mainClosed) return;
        _collectionCompletion = null;
        _collectionScrollTimer.Stop();
        DrawerScroll.ScrollToVerticalOffset(enabled ? 0 : _normalCollectionScrollOffset);
        SetValue(CollectionProgressProperty, enabled ? 1d : 0d);
        BeginAnimation(CollectionProgressProperty, null);
        SetValue(CollectionTransitioningProperty, false);
        CompleteCollectionWindowTransition(enabled);
        UpdateCollectionLayout();
        SetStatus(enabled ? "沉浸收集 · 点击抽屉直接收集图片" : "已退出沉浸收集 · 下方点击打开画板", false);
    }

    private void StartDrawerScroll(double target, double milliseconds)
    {
        _collectionScrollTimer.Stop();
        _scrollTweenFrom = DrawerScroll.VerticalOffset;
        _scrollTweenTarget = Math.Max(0, target);
        _scrollTweenMilliseconds = milliseconds;
        _scrollTweenStarted = Stopwatch.GetTimestamp();
        _collectionScrollTimer.Start();
    }

    private double CalculateCompactWindowHeight(double normalHeight)
    {
        UpdateLayout();
        var listWidth = DrawerList.ActualWidth > 1 ? DrawerList.ActualWidth :
            Math.Max(1, (ActualWidth > 1 ? ActualWidth : Width) - 26);
        var fixedHeight = ActualHeight > 1
            ? ActualHeight - DrawerScroll.ActualHeight - AddDrawerRow.ActualHeight
            : 102;
        var cards = AdaptiveDrawerPanel.ExtentHeight(listWidth, _drawers.Count, 1);
        return Math.Clamp(Math.Ceiling(fixedHeight + cards), 160, Math.Max(160, normalHeight));
    }

    private void BeginCollectionWindowTransition(bool enabled)
    {
        var from = ActualHeight > 0 ? ActualHeight : Height;
        if (!double.IsFinite(from) || from <= 0) from = 500;
        _collectionWindowTarget = enabled
            ? CalculateCompactWindowHeight(Math.Max(from, _normalCollectionWindowHeight))
            : Math.Max(_normalCollectionMinHeight, _normalCollectionWindowHeight);
        BeginAnimation(HeightProperty, null);
        Height = from;
        if (enabled) MinHeight = 160;
        if (!IsVisible)
        {
            Height = _collectionWindowTarget;
            if (!enabled) MinHeight = _normalCollectionMinHeight;
            return;
        }
        BeginAnimation(HeightProperty, new DoubleAnimation(from, _collectionWindowTarget, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void CompleteCollectionWindowTransition(bool enabled)
    {
        BeginAnimation(HeightProperty, null);
        Height = _collectionWindowTarget > 0 ? _collectionWindowTarget :
            enabled ? CalculateCompactWindowHeight(_normalCollectionWindowHeight) : _normalCollectionWindowHeight;
        MinHeight = enabled ? 160 : _normalCollectionMinHeight;
        if (!enabled) _settings.MainHeight = Height;
        if (IsVisible) EnsureMainWindowOnScreen();
    }

    private void FinishCollectionVisuals()
    {
        var finishWindowSize = CollectionTransitioning || IsCollectionMode;
        ++_collectionGeneration;
        _collectionCompletion?.TrySetResult();
        _collectionCompletion = null;
        _collectionScrollTimer.Stop();
        BeginAnimation(CollectionProgressProperty, null);
        SetValue(CollectionProgressProperty, IsCollectionMode ? 1d : 0d);
        SetValue(CollectionTransitioningProperty, false);
        if (finishWindowSize) CompleteCollectionWindowTransition(IsCollectionMode);
        UpdateCollectionLayout();
        FinishDrawerEntrance();
        ClearCollectionFeedback();
    }

    private void FinishDrawerEntrance()
    {
        _drawerEntranceCompletion?.TrySetResult();
        _drawerEntranceCompletion = null;
        if (_enteringDrawer is not { } container) return;
        _enteringDrawer = null;
        container.BeginAnimation(OpacityProperty, null);
        container.Opacity = 1;
        container.BeginAnimation(AdaptiveDrawerPanel.RevealProgressProperty, null);
        AdaptiveDrawerPanel.SetRevealProgress(container, 1);
        container.RenderTransform = Transform.Identity;
    }

    private async Task AnimateNewDrawerAsync(string id, IReadOnlyDictionary<string, Point> before)
    {
        DrawerList.UpdateLayout();
        DrawerScroll.UpdateLayout();
        if (!IsVisible) { DrawerScroll.ScrollToEnd(); return; }
        var model = _drawers.First(x => x.Id == id);
        if (DrawerList.ItemContainerGenerator.ContainerFromItem(model) is not FrameworkElement container) return;
        FinishDrawerEntrance();
        var destinationOffset = DrawerScroll.ScrollableHeight;
        _enteringDrawer = container;
        container.ClipToBounds = true;
        AnimateDrawerLayout(before);
        var scale = new ScaleTransform(.96, .96);
        var move = new TranslateTransform(0, 20);
        container.RenderTransformOrigin = new Point(.5, 1);
        container.RenderTransform = new TransformGroup { Children = { scale, move } };
        var duration = TimeSpan.FromMilliseconds(300);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        container.Opacity = 0;
        AdaptiveDrawerPanel.SetRevealProgress(container, 0);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _drawerEntranceCompletion = completion;
        var fade = new DoubleAnimation(0, 1, duration);
        fade.Completed += (_, _) => completion.TrySetResult();
        container.BeginAnimation(OpacityProperty, fade);
        container.BeginAnimation(AdaptiveDrawerPanel.RevealProgressProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.96, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.96, 1, duration) { EasingFunction = easing });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, 0, duration) { EasingFunction = easing });
        StartDrawerScroll(destinationOffset, 300);
        try { await completion.Task; }
        finally { if (ReferenceEquals(_enteringDrawer, container)) FinishDrawerEntrance(); }
    }

    private bool PlayCollectionFeedback(string id, ImageSource? incoming = null)
    {
        var model = _drawers.FirstOrDefault(x => x.Id == id);
        if (model is null || DrawerList.ItemContainerGenerator.ContainerFromItem(model) is not ContentPresenter presenter) return false;
        if (IsCollectionMode)
        {
            if (FindVisualChild<Canvas>(presenter, "CompactCollectionFeedback") is not { } layer) return false;
            layer.Children.Clear();
            var surface = new Border { Width = layer.ActualWidth, Height = layer.ActualHeight, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(65, 63, 153, 211)) };
            layer.Children.Add(surface);
            var down = new TranslateTransform(0, -5); surface.RenderTransform = down;
            AnimateDrawerCoordinate(down, TranslateTransform.YProperty, -5, 8, 300);
            var fade = new DoubleAnimation(.85, 0, TimeSpan.FromMilliseconds(330));
            fade.Completed += (_, _) => layer.Children.Remove(surface);
            surface.BeginAnimation(OpacityProperty, fade);
            return true;
        }
        if (model.Cover is not null && incoming is not null &&
            FindVisualChild<Canvas>(presenter, "AnimationLayer") is { } animationLayer)
        {
            AnimateCollectedThumbnail(animationLayer, incoming);
            return true;
        }
        return false;
    }

    private static void AnimateCollectedThumbnail(Canvas layer, ImageSource source, Stretch stretch = Stretch.Uniform)
    {
        layer.Children.Clear();
        var image = new Image { Source = source, Stretch = stretch, Width = layer.ActualWidth, Height = layer.ActualHeight,
            RenderTransformOrigin = new Point(.5, .5) };
        var translate = new TranslateTransform();
        var scale = new ScaleTransform(1, 1);
        image.RenderTransform = new TransformGroup { Children = { scale, translate } };
        layer.Children.Add(image);
        var duration = TimeSpan.FromMilliseconds(300);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Math.Max(88, layer.ActualHeight * .72), duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, .72, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, .72, duration) { EasingFunction = easing });
        var fade = new DoubleAnimation(1, 0, duration);
        fade.Completed += (_, _) => layer.Children.Remove(image);
        image.BeginAnimation(OpacityProperty, fade);
    }

    private async void ShowCollectedNotice(string id)
    {
        if (!IsCollectionMode) return;
        var model = _drawers.FirstOrDefault(x => x.Id == id);
        if (model is null || DrawerList.ItemContainerGenerator.ContainerFromItem(model) is not ContentPresenter presenter ||
            FindVisualChild<Border>(presenter, "DrawerRoot") is not { } target) return;
        if (_collectionNotices.Remove(id, out var previous)) previous.IsOpen = false;
        var text = new TextBlock { Text = "已收集", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush") };
        var surface = new Border
        {
            Child = text, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("ControlBorderBrush"),
            BorderThickness = new Thickness(1), Opacity = 0, RenderTransformOrigin = new Point(.5, 1),
            RenderTransform = new TranslateTransform(0, 5),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = .16 }
        };
        var popup = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Top, VerticalOffset = -4,
            AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false, Child = surface
        };
        _collectionNotices[id] = popup;
        popup.IsOpen = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        ((TranslateTransform)surface.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        await Task.Delay(650);
        if (!_collectionNotices.TryGetValue(id, out var current) || !ReferenceEquals(current, popup)) return;
        var fade = new DoubleAnimation(surface.Opacity, 0, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.HoldEnd };
        surface.BeginAnimation(OpacityProperty, fade);
        await Task.Delay(170);
        if (_collectionNotices.Remove(id, out current) && ReferenceEquals(current, popup)) popup.IsOpen = false;
    }

    private void ClearCollectionFeedback()
    {
        foreach (var popup in _collectionNotices.Values) popup.IsOpen = false;
        _collectionNotices.Clear();
        foreach (var model in _drawers)
            if (DrawerList.ItemContainerGenerator.ContainerFromItem(model) is ContentPresenter presenter)
                foreach (var name in new[] { "AnimationLayer", "CompactCollectionFeedback" })
                    FindVisualChild<Canvas>(presenter, name)?.Children.Clear();
    }
}
