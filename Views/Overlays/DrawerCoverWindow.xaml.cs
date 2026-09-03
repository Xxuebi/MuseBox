using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class DrawerCoverWindow : Window
{
    private BitmapSource _source = null!;
    private BitmapSource _oriented = null!;
    private Point? _panStart;
    private CoverCropState _panCrop = new();
    private Rect _frame;
    private bool _updating;
    private bool _closing;
    private bool _allowClose;
    private readonly ScaleTransform _scale = new(.96, .96);
    public string SourcePath { get; private set; } = "";
    public CoverCropState Crop { get; private set; } = new();
    public System.Drawing.Bitmap? Result { get; private set; }

    public DrawerCoverWindow(string path, CoverCropState? crop = null)
    {
        InitializeComponent();
        LoadSource(path, crop ?? new());
        CoverChrome.RenderTransformOrigin = new Point(.5, .5);
        CoverChrome.RenderTransform = _scale;
        SourceInitialized += (_, _) => CoverChrome.Opacity = 0;
        Loaded += (_, _) =>
        {
            LayoutPreview();
            var duration = TimeSpan.FromMilliseconds(160);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            CoverChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.96, 1, duration) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.96, 1, duration) { EasingFunction = ease });
        };
        Closing += (_, args) =>
        {
            if (_allowClose || !IsVisible) return;
            args.Cancel = true;
            BeginClose(DialogResult);
        };
    }

    public static string? ChooseImage(Window owner)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择抽屉封面图片", CheckFileExists = true,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*"
        };
        return picker.ShowDialog(owner) == true ? picker.FileName : null;
    }

    private void LoadSource(string path, CoverCropState state)
    {
        // Load before replacing current state, so a failed choice leaves the
        // previous image and adjustments intact. GIF covers use their first frame.
        var source = DrawerCoverRenderer.Load(path);
        var oriented = DrawerCoverRenderer.Orient(source, state);
        SourcePath = path;
        _source = source;
        _oriented = oriented;
        Crop = state;
        CoverImage.Source = _oriented;
        LayoutPreview();
    }

    private void LayoutPreview()
    {
        if (_oriented is null || CoverWorkspace.ActualWidth < 50 || CoverWorkspace.ActualHeight < 50) return;
        var width = Math.Min(CoverWorkspace.ActualWidth - 42,
            (CoverWorkspace.ActualHeight - 36) * CoverCropState.DrawerAspect);
        var height = width / CoverCropState.DrawerAspect;
        CoverWorkspace.Clip = new RectangleGeometry(new Rect(0, 0, CoverWorkspace.ActualWidth, CoverWorkspace.ActualHeight), 12, 12);
        _frame = new Rect((CoverWorkspace.ActualWidth - width) / 2,
            (CoverWorkspace.ActualHeight - height) / 2, width, height);
        var placement = DrawerCoverRenderer.Place(_oriented.PixelWidth, _oriented.PixelHeight, width, height, Crop);
        Crop = placement.Crop;
        CoverImage.Width = placement.Image.Width;
        CoverImage.Height = placement.Image.Height;
        Canvas.SetLeft(CoverImage, _frame.X + placement.Image.X);
        Canvas.SetTop(CoverImage, _frame.Y + placement.Image.Y);
        CoverFrame.Width = width; CoverFrame.Height = height;
        Canvas.SetLeft(CoverFrame, _frame.X); Canvas.SetTop(CoverFrame, _frame.Y);
        CoverMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, CoverWorkspace.ActualWidth, CoverWorkspace.ActualHeight)),
            new RectangleGeometry(_frame));
        _updating = true;
        CoverZoom.Value = Crop.Zoom;
        CoverZoomText.Text = $"{Crop.Zoom * 100:0}%";
        _updating = false;
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e) => LayoutPreview();
    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (_closing || _frame.IsEmpty) return;
        _panStart = e.GetPosition(CoverWorkspace);
        _panCrop = Crop;
        CoverWorkspace.CaptureMouse();
        e.Handled = true;
    }
    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(CoverWorkspace) - start;
        Crop = _panCrop with { PanX = _panCrop.PanX + delta.X / _frame.Width, PanY = _panCrop.PanY + delta.Y / _frame.Height };
        LayoutPreview();
    }
    private void OnPanEnd(object sender, MouseButtonEventArgs e) => CoverWorkspace.ReleaseMouseCapture();
    private void OnPanLost(object sender, MouseEventArgs e) => _panStart = null;
    private void OnZoomWheel(object sender, MouseWheelEventArgs e)
    {
        CoverZoom.Value = Math.Clamp(CoverZoom.Value * Math.Pow(1.1, e.Delta / 120d), 1, 8);
        e.Handled = true;
    }
    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _oriented is null) return;
        Crop = Crop with { Zoom = e.NewValue };
        LayoutPreview();
    }
    private void OnTransformClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string action }) return;
        Crop = action switch
        {
            "flipX" => Crop with { FlipX = !Crop.FlipX, PanX = -Crop.PanX },
            "flipY" => Crop with { FlipY = !Crop.FlipY, PanY = -Crop.PanY },
            // Reflections reverse the rotation direction in source coordinates.
            "left" or "right" => Crop with
            {
                QuarterTurns = (Crop.QuarterTurns + (action == "right" ? 1 : 3) *
                    (Crop.FlipX ^ Crop.FlipY ? -1 : 1) + 8) % 4, PanX = 0, PanY = 0
            },
            _ => new()
        };
        _oriented = DrawerCoverRenderer.Orient(_source, Crop);
        CoverImage.Source = _oriented;
        LayoutPreview();
    }
    private void OnChooseImageClick(object sender, RoutedEventArgs e)
    {
        var path = ChooseImage(this);
        if (path is null) return;
        try { LoadSource(path, new()); }
        catch (Exception error) { new PromptWindow("无法读取图片", error.Message, "知道了", false) { Owner = this }.ShowDialog(); }
    }
    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        try { Result = DrawerCoverRenderer.Render(_oriented, Crop); Finish(true); }
        catch (Exception error) { new PromptWindow("无法生成封面", error.Message, "知道了", false) { Owner = this }.ShowDialog(); }
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(false);
    private void Finish(bool accepted)
    {
        if (!_closing) DialogResult = accepted;
    }
    private void BeginClose(bool? accepted)
    {
        if (_closing) return;
        _closing = true;
        CoverChrome.IsHitTestVisible = false;
        // Animate this actual window, keeping the chosen result until it closes.
        var duration = TimeSpan.FromMilliseconds(130);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(_scale.ScaleX, .96, duration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(_scale.ScaleY, .96, duration) { EasingFunction = ease });
        var fade = new DoubleAnimation(CoverChrome.Opacity, 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            _allowClose = true;
            if (accepted is bool result) DialogResult = result;
            else Close();
        };
        CoverChrome.BeginAnimation(OpacityProperty, fade);
    }
    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
    }
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
