using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScreenshotCollector;

public partial class PromptWindow : Window
{
    public bool AlternativeChosen { get; private set; }
    private bool _closingAnimated;
    private bool _allowClose;
    public PromptWindow(string title, string message, string confirmLabel = "确定", bool confirmation = true)
    {
        InitializeComponent();
        Title = PromptTitle.Text = title;
        PromptMessage.Text = message;
        PromptConfirm.Content = confirmLabel;
        PromptCancel.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
        MaxWidth = Math.Max(280, SystemParameters.WorkArea.Width - 32);
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 32);
        Loaded += (_, _) =>
        {
            (confirmation ? PromptCancel : PromptConfirm).Focus();
            PromptChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            var slide = new TranslateTransform();
            PromptChrome.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(170))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        Closing += (_, args) =>
        {
            if (_allowClose || !IsVisible) return;
            args.Cancel = true;
            if (_closingAnimated) return;
            _closingAnimated = true;
            var result = DialogResult;
            PromptChrome.IsHitTestVisible = false;
            var slide = PromptChrome.RenderTransform as TranslateTransform ?? new TranslateTransform();
            PromptChrome.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slide.Y, 7, TimeSpan.FromMilliseconds(130))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            var fade = new DoubleAnimation(PromptChrome.Opacity, 0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) =>
            {
                _allowClose = true;
                if (result is bool accepted) DialogResult = accepted;
                else Close();
            };
            PromptChrome.BeginAnimation(OpacityProperty, fade);
        };
    }

    public static bool Confirm(Window owner, string title, string message, string confirmLabel)
        => new PromptWindow(title, message, confirmLabel) { Owner = owner }.ShowDialog() == true;

    // 1 = primary, 2 = alternative, 0 = cancel/window close. Cancel retains focus.
    public static int Choose(Window owner, string title, string message, string primary, string alternative)
    {
        var dialog = new PromptWindow(title, message, primary) { Owner = owner, Width = 490 };
        dialog.PromptAlternative.Content = alternative;
        dialog.PromptAlternative.Visibility = Visibility.Visible;
        var accepted = dialog.ShowDialog() == true;
        return dialog.AlternativeChosen ? 2 : accepted ? 1 : 0;
    }
    private void OnAlternativeClick(object sender, RoutedEventArgs e) { AlternativeChosen = true; DialogResult = false; }

    public static void Inform(string title, string message)
        => new PromptWindow(title, message, confirmation: false) { WindowStartupLocation = WindowStartupLocation.CenterScreen }.ShowDialog();

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBlock && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
