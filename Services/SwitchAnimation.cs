using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScreenshotCollector.Services;

public static class SwitchAnimation
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SwitchAnimation),
        new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty ReadyProperty = DependencyProperty.RegisterAttached(
        "Ready", typeof(bool), typeof(SwitchAnimation));

    private static readonly DependencyProperty PreviousValueProperty = DependencyProperty.RegisterAttached(
        "PreviousValue", typeof(bool), typeof(SwitchAnimation));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetWithoutAnimation(ToggleButton toggle, bool value)
    {
        toggle.SetValue(ReadyProperty, false);
        toggle.IsChecked = value;
        toggle.ApplyTemplate();
        Snap(toggle);
        toggle.SetValue(PreviousValueProperty, value);
        toggle.SetValue(ReadyProperty, toggle.IsLoaded);
    }

    private static void OnEnabledChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        if (source is not ToggleButton toggle) return;
        if ((bool)e.NewValue)
        {
            toggle.Loaded += OnLoaded;
            toggle.Checked += OnStateChanged;
            toggle.Unchecked += OnStateChanged;
        }
        else
        {
            toggle.Loaded -= OnLoaded;
            toggle.Checked -= OnStateChanged;
            toggle.Unchecked -= OnStateChanged;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleButton)sender;
        toggle.ApplyTemplate();
        Snap(toggle);
        toggle.SetValue(PreviousValueProperty, toggle.IsChecked == true);
        toggle.SetValue(ReadyProperty, true);
    }

    private static void OnStateChanged(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleButton)sender;
        var isChecked = toggle.IsChecked == true;
        if (!(bool)toggle.GetValue(ReadyProperty))
        {
            toggle.SetValue(PreviousValueProperty, isChecked);
            return;
        }

        toggle.ApplyTemplate();
        if (toggle.Template.FindName("TrackOn", toggle) is not Border track ||
            toggle.Template.FindName("ThumbTranslate", toggle) is not TranslateTransform thumb) return;

        var previous = (bool)toggle.GetValue(PreviousValueProperty);
        var fromOpacity = track.HasAnimatedProperties ? track.Opacity : previous ? 1d : 0d;
        var fromX = thumb.HasAnimatedProperties ? thumb.X : previous ? 19d : 0d;
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        track.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(fromOpacity, isChecked ? 1 : 0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        thumb.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromX, isChecked ? 19 : 0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        toggle.SetValue(PreviousValueProperty, isChecked);
    }

    private static void Snap(ToggleButton toggle)
    {
        if (toggle.Template.FindName("TrackOn", toggle) is Border track)
            track.BeginAnimation(UIElement.OpacityProperty, null);
        if (toggle.Template.FindName("ThumbTranslate", toggle) is TranslateTransform thumb)
            thumb.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
