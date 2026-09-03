using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenshotCollector;

public partial class BoardSettingsWindow : Window
{
    private string _selectedColor;
    private bool _draggingOpacity;

    public BoardSettingsWindow(
        string backgroundColor,
        double backgroundOpacity,
        bool opacityAffectsImages = false,
        bool showWindowFrame = true)
    {
        _selectedColor = NormalizeColor(backgroundColor);
        InitializeComponent();
        Services.ThemedWindowChromeService.Attach(this);
        OpacitySlider.Value = Math.Clamp(backgroundOpacity, .1, 1) * 100;
        AffectImagesToggle.IsChecked = opacityAffectsImages;
        WindowFrameToggle.IsChecked = showWindowFrame;
        RefreshOpacityText();
        RefreshCustomColorPreview();
        Loaded += (_, _) => RefreshPaletteSelection();
    }

    public string BackgroundColor { get; private set; } = "#7A7A7A";
    public double BackgroundOpacity { get; private set; } = 1;
    public bool OpacityAffectsImages { get; private set; }
    public bool ShowWindowFrame { get; private set; } = true;

    public event Action<string, double, bool>? PreviewChanged;
    public event Action<bool>? WindowFramePreviewChanged;

    private void OnPaletteColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        _selectedColor = color;
        RefreshPaletteSelection();
        RefreshCustomColorPreview();
        RaisePreview();
    }

    private void OnCustomColorClick(object sender, RoutedEventArgs e)
    {
        var rgb = NormalizeColor(_selectedColor).TrimStart('#');
        var alpha = (byte)Math.Round(Math.Clamp(OpacitySlider.Value / 100, 0, 1) * 255);
        var picker = new CustomColorPickerWindow($"#{alpha:X2}{rgb}") { Owner = this };
        picker.ColorChanged += (_, value) =>
        {
            _selectedColor = NormalizeColor(value);
            if (TryGetAlpha(value, out var opacity)) OpacitySlider.Value = opacity * 100;
            RefreshPaletteSelection();
            RefreshCustomColorPreview();
            RaisePreview();
        };
        picker.ShowDialog();
    }

    private IEnumerable<Button> PaletteButtons() =>
        GrayscalePanel.Children.OfType<Button>()
            .Concat(PastelPalettePanel.Children.OfType<Button>());

    private void RefreshPaletteSelection()
    {
        foreach (var button in PaletteButtons())
        {
            var selected = string.Equals(button.Tag as string, _selectedColor, StringComparison.OrdinalIgnoreCase);
            if (selected)
            {
                button.BorderBrush = (Brush)FindResource("AccentBrush");
                button.BorderThickness = new Thickness(2);
            }
            else
            {
                button.ClearValue(Control.BorderBrushProperty);
                button.ClearValue(Control.BorderThicknessProperty);
            }
        }
    }

    private void RefreshCustomColorPreview()
    {
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(_selectedColor); }
        catch { color = Colors.Black; }
        CustomColorPreview.Background = new SolidColorBrush(color);
        if (BackgroundOpacityColorTrack is not null)
        {
            BackgroundOpacityColorTrack.Background = new LinearGradientBrush(
                Color.FromArgb(0, color.R, color.G, color.B),
                Color.FromArgb(255, color.R, color.G, color.B),
                0);
        }
    }

    private void OnOpacityTrackMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _draggingOpacity = true;
        BackgroundOpacityTrack.CaptureMouse();
        UpdateOpacity(e.GetPosition(BackgroundOpacityTrack).X);
        e.Handled = true;
    }

    private void OnOpacityTrackMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingOpacity && e.LeftButton == MouseButtonState.Pressed)
            UpdateOpacity(e.GetPosition(BackgroundOpacityTrack).X);
    }

    private void OnOpacityTrackMouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingOpacity = false;
        BackgroundOpacityTrack.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateOpacity(double x)
    {
        var width = Math.Max(1, BackgroundOpacityTrack.ActualWidth);
        var ratio = Math.Clamp(x / width, 0, 1);
        OpacitySlider.Value = OpacitySlider.Minimum + ratio * (OpacitySlider.Maximum - OpacitySlider.Minimum);
    }

    private void OnOpacityValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText is not null)
        {
            RefreshOpacityText();
            RaisePreview();
        }
    }

    private void OnAffectImagesChanged(object sender, RoutedEventArgs e) => RaisePreview();

    private void OnWindowFrameChanged(object sender, RoutedEventArgs e) =>
        WindowFramePreviewChanged?.Invoke(WindowFrameToggle.IsChecked == true);

    private void RaisePreview() => PreviewChanged?.Invoke(
        _selectedColor,
        OpacitySlider.Value / 100,
        AffectImagesToggle.IsChecked == true);

    private void RefreshOpacityText() => OpacityText.Text = $"{OpacitySlider.Value:0}%";

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _selectedColor = "#7A7A7A";
        OpacitySlider.Value = 100;
        AffectImagesToggle.IsChecked = false;
        WindowFrameToggle.IsChecked = true;
        RefreshPaletteSelection();
        RefreshCustomColorPreview();
        RaisePreview();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        BackgroundColor = _selectedColor;
        BackgroundOpacity = OpacitySlider.Value / 100;
        OpacityAffectsImages = AffectImagesToggle.IsChecked == true;
        ShowWindowFrame = WindowFrameToggle.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string NormalizeColor(string value)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch { return "#7A7A7A"; }
    }

    private static bool TryGetAlpha(string value, out double opacity)
    {
        opacity = 1;
        var text = value.Trim().TrimStart('#');
        if (text.Length != 8 || !byte.TryParse(text[..2],
                System.Globalization.NumberStyles.HexNumber, null, out var alpha)) return false;
        opacity = alpha / 255d;
        return true;
    }
}
