using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenshotCollector.Services;
using Bitmap = System.Drawing.Bitmap;
using RotateFlipType = System.Drawing.RotateFlipType;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;

namespace ScreenshotCollector;

public partial class ImageEditorWindow : Window
{
    // Pixel revisions are immutable and shared by slider history entries.
    private sealed record EditState(Bitmap Pixels, double Brightness, double Contrast, double Saturation, double Hue, double Opacity);
    private readonly Bitmap _original;
    private Bitmap _working;
    private readonly HashSet<Bitmap> _ownedPixels = new();
    private readonly List<EditState> _history = new();
    private EditState _lastState = null!;
    private Bitmap? _previewBase;
    private WriteableBitmap? _previewSource;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _ready, _syncing, _adjustmentGesture, _gestureSaved;
    private bool _cropMode, _cropDragging;
    private Point _cropStart;
    private Rect _cropPixels = Rect.Empty;
    public Bitmap? ResultBitmap { get; private set; }
    public bool SaveAsNewImage { get; private set; }

    public ImageEditorWindow(string path, string mode = "Edit")
    {
        using (var loaded = new Bitmap(path))
            _original = loaded.Clone(new Rectangle(0, 0, loaded.Width, loaded.Height), PixelFormat.Format32bppArgb);
        _working = _original;
        _ownedPixels.Add(_original);
        InitializeComponent();
        MaxWidth = Math.Max(650, SystemParameters.WorkArea.Width - 40);
        MaxHeight = Math.Max(450, SystemParameters.WorkArea.Height - 40);
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };
        Closed += (_, _) =>
        {
            _ready = false;
            _previewTimer.Stop();
            _previewBase?.Dispose();
            foreach (var bitmap in _ownedPixels) bitmap.Dispose();
            _ownedPixels.Clear();
        };
        _lastState = CurrentState();
        _ready = true;
        _cropMode = mode == "Crop";
        UpdateCropMode();
        UpdateUndoButton();
        RefreshPreview();
    }

    internal static BitmapSource ToSource(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            BitmapSource source;
            if (data.Stride > 0)
                source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32,
                    null, data.Scan0, checked(data.Stride * bitmap.Height), data.Stride);
            else
            {
                var stride = bitmap.Width * 4;
                var pixels = new byte[checked(stride * bitmap.Height)];
                for (var y = 0; y < bitmap.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
                source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            }
            source.Freeze();
            return source;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private void RefreshPreview()
    {
        if (!_ready) return;
        _previewTimer.Stop();
        if (_previewBase is null)
        {
            var scale = Math.Min(1, 1000d / Math.Max(_working.Width, _working.Height));
            _previewBase = new Bitmap(Math.Max(1, (int)(_working.Width * scale)),
                Math.Max(1, (int)(_working.Height * scale)), PixelFormat.Format32bppArgb);
            using var graphics = System.Drawing.Graphics.FromImage(_previewBase);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(_working, new Rectangle(0, 0, _previewBase.Width, _previewBase.Height));
        }
        using var adjusted = ImageEditService.Adjust(_previewBase, BrightnessSlider.Value / 100,
            1 + ContrastSlider.Value / 100, SaturationSlider.Value / 100, HueSlider.Value, OpacitySlider.Value / 100);
        if (_previewSource is null || _previewSource.PixelWidth != adjusted.Width || _previewSource.PixelHeight != adjusted.Height)
        {
            _previewSource = new WriteableBitmap(adjusted.Width, adjusted.Height, 96, 96, PixelFormats.Bgra32, null);
            PreviewImage.Source = _previewSource;
        }
        var data = adjusted.LockBits(new Rectangle(0, 0, adjusted.Width, adjusted.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            _previewSource.WritePixels(new Int32Rect(0, 0, adjusted.Width, adjusted.Height),
                data.Scan0, checked(data.Stride * adjusted.Height), data.Stride);
        }
        finally { adjusted.UnlockBits(data); }
        EditorStatus.Text = $"{_working.Width} × {_working.Height} px";
        RenderCropSelection();
    }

    private EditState CurrentState() => new(_working, BrightnessSlider.Value, ContrastSlider.Value, SaturationSlider.Value, HueSlider.Value, OpacitySlider.Value);

    private void PushHistory(EditState state)
    {
        _history.Add(state);
        while (_history.Count > 50) _history.RemoveAt(0);
        // Bound unique bitmap revisions; slider-only edits don't duplicate image memory.
        while (_history.Count > 1 && _history.Select(x => x.Pixels).Append(_working).Distinct()
                   .Sum(x => (long)x.Width * x.Height * 4) > 256L * 1024 * 1024)
            _history.RemoveAt(0);
        UpdateUndoButton();
    }

    private void ReleaseUnusedPixels()
    {
        var keep = _history.Select(x => x.Pixels).Append(_original).Append(_working).ToHashSet();
        foreach (var bitmap in _ownedPixels.Where(x => !keep.Contains(x)).ToArray())
        {
            bitmap.Dispose();
            _ownedPixels.Remove(bitmap);
        }
    }

    private void ReplaceWorking(Bitmap bitmap)
    {
        _working = bitmap;
        _ownedPixels.Add(bitmap);
        _cropPixels = Rect.Empty;
        _previewBase?.Dispose();
        _previewBase = null;
        _lastState = CurrentState();
        ReleaseUnusedPixels();
        RefreshPreview();
    }

    private void OnAdjustmentStart(object sender, MouseButtonEventArgs e)
    {
        _adjustmentGesture = true;
        _gestureSaved = false;
    }
    private void OnAdjustmentEnd(object sender, MouseButtonEventArgs e) => EndAdjustment();
    private void OnAdjustmentCaptureLost(object sender, MouseEventArgs e) => EndAdjustment();
    private void EndAdjustment()
    {
        _adjustmentGesture = _gestureSaved = false;
    }

    private void OnAdjustmentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        if (!_adjustmentGesture || !_gestureSaved)
        {
            PushHistory(_lastState);
            _gestureSaved = true;
        }
        _lastState = CurrentState();
        ReleaseUnusedPixels();
        UpdateNumberTexts();
        // Throttle instead of debounce: dragging never postpones the next frame.
        if (!_previewTimer.IsEnabled) _previewTimer.Start();
    }

    private void UpdateNumberTexts(bool force = false)
    {
        foreach (var input in new[] { BrightnessValue, ContrastValue, SaturationValue, HueValue, OpacityValue })
            if (force || !input.IsKeyboardFocusWithin)
                input.Text = ((Slider)FindName((string)input.Tag)).Value.ToString("0.#", CultureInfo.CurrentCulture);
    }

    private void OnNumberMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox input || input.IsKeyboardFocusWithin) return;
        e.Handled = true;
        input.Focus();
        input.SelectAll();
    }
    private void OnNumberFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();
    private void OnEditorPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox input || input.Tag is not string name || FindName(name) is not Slider) return;
        DismissNumberEditor(input, e.OriginalSource as DependencyObject);
    }
    private bool DismissNumberEditor(TextBox input, DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, input)) return false;
        CommitNumber(input);
        EditorSurface.Focus();
        // Do not consume the click: the target button/slider still acts immediately.
        return true;
    }
    private void OnNumberCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitNumber((TextBox)sender);
    private void CommitNumber(TextBox input)
    {
        var slider = (Slider)FindName((string)input.Tag);
        if ((double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
             || double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value))
            slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        input.Text = slider.Value.ToString("0.#", CultureInfo.CurrentCulture);
    }
    private void OnNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.Key == Key.Enter) CommitNumber((TextBox)sender);
        else UpdateNumberTexts(true);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void UndoEdit()
    {
        EndAdjustment();
        if (!_cropPixels.IsEmpty)
        {
            _cropPixels = Rect.Empty;
            RenderCropSelection();
            return;
        }
        if (_history.Count == 0) return;
        var state = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _syncing = true;
        try
        {
            BrightnessSlider.Value = state.Brightness;
            ContrastSlider.Value = state.Contrast;
            SaturationSlider.Value = state.Saturation;
            HueSlider.Value = state.Hue;
            OpacitySlider.Value = state.Opacity;
        }
        finally { _syncing = false; }
        ReplaceWorking(state.Pixels);
        UpdateNumberTexts(true);
        UpdateUndoButton();
    }
    private void UpdateUndoButton() => UndoEditorButton.IsEnabled = _history.Count > 0 || !_cropPixels.IsEmpty;
    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoEdit();

    private Rect ImageRect()
    {
        var scale = Math.Min(PreviewHost.ActualWidth / _working.Width, PreviewHost.ActualHeight / _working.Height);
        var size = new Size(Math.Max(0, _working.Width * scale), Math.Max(0, _working.Height * scale));
        return new Rect((PreviewHost.ActualWidth - size.Width) / 2, (PreviewHost.ActualHeight - size.Height) / 2, size.Width, size.Height);
    }
    private Point PixelPoint(Point point)
    {
        var bounds = ImageRect();
        return new Point(Math.Clamp((point.X - bounds.X) / Math.Max(1, bounds.Width) * _working.Width, 0, _working.Width),
            Math.Clamp((point.Y - bounds.Y) / Math.Max(1, bounds.Height) * _working.Height, 0, _working.Height));
    }
    private static bool InsideButton(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) return true;
        return false;
    }
    private void OnCropDown(object sender, MouseButtonEventArgs e)
    {
        if (!_cropMode || InsideButton(e.OriginalSource) || !ImageRect().Contains(e.GetPosition(PreviewHost))) return;
        _cropStart = PixelPoint(e.GetPosition(PreviewHost));
        _cropPixels = new Rect(_cropStart, _cropStart);
        _cropDragging = true;
        CropCanvas.CaptureMouse();
        RenderCropSelection();
        e.Handled = true;
    }
    private void OnCropMove(object sender, MouseEventArgs e)
    {
        if (!_cropDragging || e.LeftButton != MouseButtonState.Pressed) return;
        _cropPixels = new Rect(_cropStart, PixelPoint(e.GetPosition(PreviewHost)));
        RenderCropSelection();
    }
    private void OnCropUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cropDragging) return;
        _cropPixels = new Rect(_cropStart, PixelPoint(e.GetPosition(PreviewHost)));
        _cropDragging = false;
        CropCanvas.ReleaseMouseCapture();
        if (_cropPixels.Width < 1 || _cropPixels.Height < 1) _cropPixels = Rect.Empty;
        RenderCropSelection();
        e.Handled = true;
    }
    private void RenderCropSelection()
    {
        if (!_ready) return;
        var hasCrop = !_cropPixels.IsEmpty && _cropPixels.Width >= 1 && _cropPixels.Height >= 1;
        CropRectangle.Visibility = hasCrop ? Visibility.Visible : Visibility.Collapsed;
        CropActions.Visibility = hasCrop && !_cropDragging ? Visibility.Visible : Visibility.Collapsed;
        CropApplyButton.IsEnabled = hasCrop;
        UpdateUndoButton();
        if (!hasCrop) { CropShade.Data = null; return; }
        var image = ImageRect();
        var rect = new Rect(image.X + _cropPixels.X / _working.Width * image.Width,
            image.Y + _cropPixels.Y / _working.Height * image.Height,
            _cropPixels.Width / _working.Width * image.Width, _cropPixels.Height / _working.Height * image.Height);
        Canvas.SetLeft(CropRectangle, rect.X);
        Canvas.SetTop(CropRectangle, rect.Y);
        CropRectangle.Width = rect.Width;
        CropRectangle.Height = rect.Height;
        CropShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(image), new RectangleGeometry(rect));
        CropActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = CropActions.DesiredSize;
        var y = rect.Bottom + 7;
        if (y + size.Height > PreviewHost.ActualHeight - 4) y = rect.Bottom - size.Height - 7;
        Canvas.SetLeft(CropActions, Math.Clamp(rect.Right - size.Width, 4, Math.Max(4, PreviewHost.ActualWidth - size.Width - 4)));
        Canvas.SetTop(CropActions, Math.Clamp(y, 4, Math.Max(4, PreviewHost.ActualHeight - size.Height - 4)));
    }

    private void ApplyCrop()
    {
        if (_cropPixels.IsEmpty || _cropPixels.Width < 1 || _cropPixels.Height < 1) return;
        var x = (int)Math.Floor(_cropPixels.X);
        var y = (int)Math.Floor(_cropPixels.Y);
        var rect = new Rectangle(x, y, Math.Min(_working.Width - x, (int)Math.Ceiling(_cropPixels.Width)),
            Math.Min(_working.Height - y, (int)Math.Ceiling(_cropPixels.Height)));
        var cropped = ImageEditService.Crop(_working, rect);
        PushHistory(CurrentState());
        ReplaceWorking(cropped);
    }
    private void OnCropApplyClick(object sender, RoutedEventArgs e) => ApplyCrop();
    private void OnCropCancelClick(object sender, RoutedEventArgs e) { _cropPixels = Rect.Empty; RenderCropSelection(); }
    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => RenderCropSelection();
    private void OnCropModeClick(object sender, RoutedEventArgs e)
    {
        _cropMode = !_cropMode;
        if (!_cropMode) _cropPixels = Rect.Empty;
        UpdateCropMode();
        RenderCropSelection();
    }
    private void UpdateCropMode()
    {
        CropCanvas.Cursor = _cropMode ? Cursors.Cross : Cursors.Arrow;
        CropModeButton.SetResourceReference(
            BackgroundProperty,
            _cropMode ? "AccentSubtleBrush" : "CardBrush");
    }
    private void OnTransformClick(object sender, RoutedEventArgs e)
    {
        var bitmap = (Bitmap)_working.Clone();
        bitmap.RotateFlip((sender as Button)?.Tag?.ToString() switch
        {
            "Horizontal" => RotateFlipType.RotateNoneFlipX,
            "Vertical" => RotateFlipType.RotateNoneFlipY,
            "RotateLeft" => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.Rotate90FlipNone
        });
        PushHistory(CurrentState());
        ReplaceWorking(bitmap);
    }
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (CurrentState() == new EditState(_original, 0, 0, 100, 0, 100) && _cropPixels.IsEmpty) return;
        PushHistory(CurrentState());
        _syncing = true;
        try
        {
            BrightnessSlider.Value = ContrastSlider.Value = HueSlider.Value = 0;
            SaturationSlider.Value = OpacitySlider.Value = 100;
        }
        finally { _syncing = false; }
        ReplaceWorking(_original);
        UpdateNumberTexts(true);
    }
    private bool PrepareResult(bool saveAs)
    {
        if (Keyboard.FocusedElement is TextBox input && input.Tag is string name && FindName(name) is Slider)
            CommitNumber(input);
        if (!_cropPixels.IsEmpty)
        {
            EditorStatus.Text = "请先在裁切框右下角确定或取消裁切";
            return false;
        }
        var result = ImageEditService.Adjust(_working, BrightnessSlider.Value / 100,
            1 + ContrastSlider.Value / 100, SaturationSlider.Value / 100, HueSlider.Value, OpacitySlider.Value / 100);
        ResultBitmap?.Dispose();
        ResultBitmap = result;
        SaveAsNewImage = saveAs;
        return true;
    }
    private void FinishEdit(bool saveAs)
    {
        try
        {
            if (PrepareResult(saveAs)) DialogResult = true;
        }
        catch (Exception error) { EditorStatus.Text = $"无法应用：{error.Message}"; }
    }
    private void OnApplyClick(object sender, RoutedEventArgs e) => FinishEdit(false);
    private void OnSaveAsClick(object sender, RoutedEventArgs e) => FinishEdit(true);
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (!InsideButton(e.OriginalSource) && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Keyboard.FocusedElement is TextBox input && input.Tag is string name && FindName(name) is Slider)
                CommitNumber(input);
            UndoEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox)
        {
            if (!_cropPixels.IsEmpty) { _cropPixels = Rect.Empty; RenderCropSelection(); }
            else DialogResult = false;
            e.Handled = true;
        }
    }
}
