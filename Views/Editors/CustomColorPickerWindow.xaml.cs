using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

internal sealed record SavedColorChoice(
    string Value,
    Brush OpaqueBrush,
    Brush ActualBrush,
    bool HasTransparency = false,
    bool IsAdd = false);

public partial class CustomColorPickerWindow : Window
{
    private const int MaxSavedColors = 20;
    private double _hue;
    private double _saturation;
    private double _value;
    private double _alpha = 1;
    private bool _draggingSaturationValue;
    private bool _draggingHue;
    private bool _draggingAlpha;
    private bool _updatingHex;
    private bool _completed;
    private readonly DispatcherTimer _copyFeedbackTimer =
        new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly string _initialColorHex;
    private readonly ISettingsService _settingsService = new JsonSettingsService();
    private AppSettings _settings = new();

    internal ObservableCollection<SavedColorChoice> SavedColors { get; } =
        [CreateSavedColorChoice("#000000"), CreateSavedColorChoice("#FFFFFF"), CreateAddChoice()];

    public CustomColorPickerWindow(string color, string? title = null)
    {
        InitializeComponent();
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyFeedbackPopup.IsOpen = false;
        };
        SavedColorList.ItemsSource = SavedColors;
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = title;
            PickerTitleText.Text = title;
        }
        var parsed = ParseColor(color);
        _initialColorHex = FormatColor(parsed);
        _alpha = parsed.A / 255d;
        (_hue, _saturation, _value) = RgbToHsv(parsed);
        Loaded += async (_, _) =>
        {
            await LoadSavedColorsAsync();
            RefreshVisuals();
        };
        Closing += OnPickerClosing;
    }

    public string SelectedColorHex { get; private set; } = "#FFFFFF";

    public event EventHandler<string>? ColorChanged;

    private void OnSaturationValueMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSaturationValue = true;
        SaturationValueCanvas.CaptureMouse();
        UpdateSaturationValue(e.GetPosition(SaturationValueCanvas));
    }

    private void OnSaturationValueMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSaturationValue && e.LeftButton == MouseButtonState.Pressed)
            UpdateSaturationValue(e.GetPosition(SaturationValueCanvas));
    }

    private void OnHueMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        HueTrack.CaptureMouse();
        UpdateHue(e.GetPosition(HueTrack).X);
    }

    private void OnHueMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingHue && e.LeftButton == MouseButtonState.Pressed)
            UpdateHue(e.GetPosition(HueTrack).X);
    }

    private void OnAlphaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _draggingAlpha = true;
        AlphaTrack.CaptureMouse();
        UpdateAlpha(e.GetPosition(AlphaTrack).X);
        e.Handled = true;
    }

    private void OnAlphaMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingAlpha && e.LeftButton == MouseButtonState.Pressed)
            UpdateAlpha(e.GetPosition(AlphaTrack).X);
    }

    private void OnPickerMouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = _draggingSaturationValue = _draggingAlpha = false;
        Mouse.Capture(null);
    }

    private void UpdateSaturationValue(Point point)
    {
        _saturation = Math.Clamp(point.X / Math.Max(1, SaturationValueCanvas.ActualWidth), 0, 1);
        _value = 1 - Math.Clamp(point.Y / Math.Max(1, SaturationValueCanvas.ActualHeight), 0, 1);
        RefreshVisuals();
    }

    private void UpdateHue(double x)
    {
        _hue = Math.Clamp(x / Math.Max(1, HueTrack.ActualWidth), 0, 1) * 360;
        RefreshVisuals();
    }

    private void UpdateAlpha(double x)
    {
        var width = AlphaTrack.ActualWidth > 0 ? AlphaTrack.ActualWidth : AlphaTrack.Width;
        _alpha = Math.Clamp(x / Math.Max(1, width), 0, 1);
        RefreshVisuals();
    }

    private void RefreshVisuals()
    {
        var pureHue = HsvToRgb(_hue, 1, 1);
        PureHueLayer.Background = new SolidColorBrush(pureHue);
        var rgb = HsvToRgb(_hue, _saturation, _value);
        var color = Color.FromArgb((byte)Math.Round(Math.Clamp(_alpha, 0, 1) * 255), rgb.R, rgb.G, rgb.B);
        SelectedColorHex = FormatColor(color);
        AlphaColorTrack.Background = new LinearGradientBrush(
            Color.FromArgb(0, rgb.R, rgb.G, rgb.B),
            Color.FromArgb(255, rgb.R, rgb.G, rgb.B), 0);
        _updatingHex = true;
        HexTextBox.Text = FormatColorValue(color);
        AlphaSlider.Value = _alpha * 100;
        AlphaText.Text = $"{_alpha:P0}";
        _updatingHex = false;

        System.Windows.Controls.Canvas.SetLeft(SaturationValueMarker,
            _saturation * SaturationValueCanvas.ActualWidth - SaturationValueMarker.Width / 2);
        System.Windows.Controls.Canvas.SetTop(SaturationValueMarker,
            (1 - _value) * SaturationValueCanvas.ActualHeight - SaturationValueMarker.Height / 2);
        HueMarker.Margin = new Thickness(
            _hue / 360 * Math.Max(0, HueTrack.ActualWidth - HueMarker.Width), 0, 0, 0);
        ColorChanged?.Invoke(this, SelectedColorHex);
    }

    private void OnHexTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_updatingHex) return;
        var valid = TryParseColorValue(HexTextBox.Text, out var color, out var includesAlpha);
        HexTextBox.BorderBrush = valid
            ? (Brush)FindResource("ControlBorderBrush")
            : Brushes.IndianRed;
        if (!valid) return;
        if (includesAlpha) _alpha = color.A / 255d;
        (_hue, _saturation, _value) = RgbToHsv(color);
        RefreshVisuals();
    }

    private void OnColorFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HexTextBox is not null) RefreshVisuals();
    }

    private void OnAlphaValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingHex || AlphaText is null) return;
        _alpha = Math.Clamp(e.NewValue / 100, 0, 1);
        RefreshVisuals();
    }

    private void OnCopyHexClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(HexTextBox.Text);
            CopyFeedbackPopup.IsOpen = false;
            CopyFeedbackPopup.IsOpen = true;
            _copyFeedbackTimer.Stop();
            _copyFeedbackTimer.Start();
        }
        catch { }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && current != sender)
        {
            if (current is Button) return;
            current = VisualTreeHelper.GetParent(current);
        }
        DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => OnCancelClick(sender, e);

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        _completed = true;
        CompleteDialog(true);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        RestoreInitialColor();
        _completed = true;
        CompleteDialog(false);
    }

    private void OnPickerClosing(object? sender, CancelEventArgs e)
    {
        if (!_completed) RestoreInitialColor();
    }

    private void RestoreInitialColor()
    {
        var color = ParseColor(_initialColorHex);
        _alpha = color.A / 255d;
        (_hue, _saturation, _value) = RgbToHsv(color);
        SelectedColorHex = _initialColorHex;
        ColorChanged?.Invoke(this, SelectedColorHex);
    }

    private void CompleteDialog(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }

    private async Task LoadSavedColorsAsync()
    {
        _settings = await _settingsService.LoadAsync();
        SavedColors.Clear();
        foreach (var value in _settings.SavedColors ?? [])
        {
            if (SavedColors.Count >= MaxSavedColors) break;
            if (!TryParseHex(value, out var color)) continue;
            var normalized = FormatColor(color);
            if (!SavedColors.Any(choice => choice.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                SavedColors.Add(CreateSavedColorChoice(normalized));
        }
        AppendAddChoice();
    }

    private async Task SaveSavedColorsAsync()
    {
        _settings.SavedColors = SavedColors
            .Where(choice => !choice.IsAdd)
            .Take(MaxSavedColors)
            .Select(choice => choice.Value)
            .ToList();
        await _settingsService.SaveAsync(_settings);
    }

    private async void OnSavedColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedColorChoice choice }) return;
        if (choice.IsAdd)
        {
            await AddSavedColorAsync();
            return;
        }
        if (!TryParseHex(choice.Value, out var color)) return;
        _alpha = color.A / 255d;
        (_hue, _saturation, _value) = RgbToHsv(color);
        RefreshVisuals();
    }

    private async Task AddSavedColorAsync()
    {
        if (!TryParseHex(SelectedColorHex, out var color)) return;
        var normalized = FormatColor(color);
        var colorCount = SavedColors.Count(choice => !choice.IsAdd);
        if (colorCount < MaxSavedColors &&
            !SavedColors.Any(choice => !choice.IsAdd &&
                choice.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveAddChoice();
            SavedColors.Add(CreateSavedColorChoice(normalized));
            AppendAddChoice();
            await SaveSavedColorsAsync();
        }
    }

    private async void OnDeleteSavedColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu menu } ||
            menu.PlacementTarget is not Button { Tag: SavedColorChoice choice } ||
            choice.IsAdd) return;
        SavedColors.Remove(choice);
        AppendAddChoice();
        await SaveSavedColorsAsync();
    }

    private void OnSavedColorContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is Button { Tag: SavedColorChoice { IsAdd: true } })
            e.Handled = true;
    }

    private static SavedColorChoice CreateSavedColorChoice(string value)
    {
        var color = ParseColor(value);
        return new SavedColorChoice(
            FormatColor(color),
            new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
            new SolidColorBrush(color),
            color.A < byte.MaxValue);
    }

    private static SavedColorChoice CreateAddChoice() =>
        new(string.Empty, Brushes.Transparent, Brushes.Transparent, IsAdd: true);

    private void RemoveAddChoice()
    {
        var add = SavedColors.FirstOrDefault(choice => choice.IsAdd);
        if (add is not null) SavedColors.Remove(add);
    }

    private void AppendAddChoice()
    {
        RemoveAddChoice();
        if (SavedColors.Count(choice => !choice.IsAdd) < MaxSavedColors)
            SavedColors.Add(CreateAddChoice());
    }

    private string CurrentColorFormat =>
        (ColorFormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "HEX";

    private string FormatColorValue(Color color) => CurrentColorFormat switch
    {
        "RGB" => $"{color.R}, {color.G}, {color.B}",
        "HSB" => $"{_hue:0}, {_saturation * 100:0}%, {_value * 100:0}%",
        "LAB" => FormatLab(color),
        "CSS" => $"rgba({color.R}, {color.G}, {color.B}, {_alpha:0.##})",
        _ => $"{color.R:X2}{color.G:X2}{color.B:X2}"
    };

    private bool TryParseColorValue(string text, out Color color, out bool includesAlpha)
    {
        color = HsvToRgb(_hue, _saturation, _value);
        color.A = (byte)Math.Round(_alpha * 255);
        includesAlpha = false;
        if (CurrentColorFormat == "HEX")
        {
            var normalized = text.Trim().TrimStart('#');
            if (!TryParseHex(normalized, out var parsed)) return false;
            includesAlpha = normalized.Length == 8;
            color = includesAlpha ? parsed : Color.FromArgb(color.A, parsed.R, parsed.G, parsed.B);
            return true;
        }
        var numbers = Regex.Matches(text, @"-?\d+(?:\.\d+)?")
            .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        switch (CurrentColorFormat)
        {
            case "RGB" when numbers.Length >= 3:
                color = Color.FromArgb(color.A, ClampByte(numbers[0]), ClampByte(numbers[1]), ClampByte(numbers[2]));
                return true;
            case "HSB" when numbers.Length >= 3:
                var rgb = HsvToRgb(numbers[0], Math.Clamp(numbers[1] / 100, 0, 1), Math.Clamp(numbers[2] / 100, 0, 1));
                color = Color.FromArgb(color.A, rgb.R, rgb.G, rgb.B);
                return true;
            case "LAB" when numbers.Length >= 3:
                var lab = LabToRgb(numbers[0], numbers[1], numbers[2]);
                color = Color.FromArgb(color.A, lab.R, lab.G, lab.B);
                return true;
            case "CSS" when numbers.Length >= 3:
                if (numbers.Length >= 4)
                {
                    includesAlpha = true;
                    color.A = (byte)Math.Round(Math.Clamp(numbers[3], 0, 1) * 255);
                }
                color.R = ClampByte(numbers[0]);
                color.G = ClampByte(numbers[1]);
                color.B = ClampByte(numbers[2]);
                return true;
            default:
                return false;
        }
    }

    private static byte ClampByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 255));

    private static string FormatLab(Color color)
    {
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        var r = Linear(color.R);
        var g = Linear(color.G);
        var b = Linear(color.B);
        var x = (r * .4124564 + g * .3575761 + b * .1804375) / .95047;
        var y = r * .2126729 + g * .7151522 + b * .072175;
        var z = (r * .0193339 + g * .119192 + b * .9503041) / 1.08883;
        static double F(double value) => value > .008856 ? Math.Cbrt(value) : 7.787 * value + 16d / 116;
        var fx = F(x);
        var fy = F(y);
        var fz = F(z);
        return $"{116 * fy - 16:0.#}, {500 * (fx - fy):0.#}, {200 * (fy - fz):0.#}";
    }

    private static Color LabToRgb(double lightness, double a, double b)
    {
        var fy = (lightness + 16) / 116;
        var fx = a / 500 + fy;
        var fz = fy - b / 200;
        static double Inverse(double value)
        {
            var cube = value * value * value;
            return cube > .008856 ? cube : (value - 16d / 116) / 7.787;
        }
        var x = .95047 * Inverse(fx);
        var y = Inverse(fy);
        var z = 1.08883 * Inverse(fz);
        var r = x * 3.2404542 + y * -.969266 + z * .0556434;
        var g = x * -.968266 + y * 1.8760108 + z * .041556;
        var blue = x * .0556434 + y * -.2040259 + z * 1.0572252;
        static byte Gamma(double value)
        {
            value = value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(Math.Max(0, value), 1 / 2.4) - .055;
            return ClampByte(value * 255);
        }
        return Color.FromRgb(Gamma(r), Gamma(g), Gamma(blue));
    }

    private void OnEyedropperClick(object sender, RoutedEventArgs e)
    {
        var previousOpacity = Opacity;
        var previousHitTest = IsHitTestVisible;
        Opacity = 0;
        IsHitTestVisible = false;
        try
        {
            var overlay = new EyedropperOverlayWindow { Owner = this };
            if (overlay.ShowDialog() != true) return;
            var color = ParseColor(overlay.SelectedColorHex);
            _alpha = 1;
            (_hue, _saturation, _value) = RgbToHsv(color);
            RefreshVisuals();
        }
        catch
        {
            // 吸色失败时保留当前颜色，避免输入设备或显示器状态异常导致应用退出。
        }
        finally
        {
            Opacity = previousOpacity;
            IsHitTestVisible = previousHitTest;
            Activate();
        }
    }

    private static Color ParseColor(string value) =>
        TryParseHex(value, out var color) ? color : Colors.White;

    private static string FormatColor(Color color) => color.A == byte.MaxValue
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryParseHex(string? value, out Color color)
    {
        color = Colors.White;
        var text = value?.Trim().TrimStart('#');
        if (text is null || text.Length is not (6 or 8) ||
            !uint.TryParse(text, NumberStyles.HexNumber, null, out var valueNumber))
            return false;
        color = text.Length == 8
            ? Color.FromArgb((byte)(valueNumber >> 24), (byte)(valueNumber >> 16),
                (byte)(valueNumber >> 8), (byte)valueNumber)
            : Color.FromRgb((byte)(valueNumber >> 16), (byte)(valueNumber >> 8), (byte)valueNumber);
        return true;
    }

    internal static Color HsvToRgb(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    internal static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0 ? 0 :
            max == r ? 60 * (((g - b) / delta) % 6) :
            max == g ? 60 * ((b - r) / delta + 2) :
            60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }
}
