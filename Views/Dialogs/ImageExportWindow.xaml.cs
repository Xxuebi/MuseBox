using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public sealed record ImageExportAttempt(bool Success, string? Error = null);

public partial class ImageExportWindow : Window
{
    private sealed record FormatChoice(ImageExportFormat Value, string Label)
    {
        public override string ToString() => Label;
    }
    private readonly ImageExportRequest _request;
    private readonly Func<ImageExportOptions, Task<ImageExportAttempt>> _export;
    private bool _loaded;
    public ImageExportOptions? ResultOptions { get; private set; }

    public ImageExportWindow(ImageExportRequest request, ImageExportOptions initial,
        Func<ImageExportOptions, Task<ImageExportAttempt>> export)
    {
        _request = request;
        _export = export;
        InitializeComponent();
        FormatInput.ItemsSource = new[]
        {
            new FormatChoice(ImageExportFormat.Original, "原始"), new FormatChoice(ImageExportFormat.Png, "PNG"),
            new FormatChoice(ImageExportFormat.Jpg, "JPG"), new FormatChoice(ImageExportFormat.Bmp, "BMP")
        };
        TemplateInput.ItemsSource = ImageExportTemplateService.Presets;
        DirectoryInput.Text = initial.Directory;
        FormatInput.SelectedItem = ((IEnumerable<FormatChoice>)FormatInput.ItemsSource).First(x => x.Value == initial.Format);
        TemplateInput.Text = ImageExportTemplateService.ToCompactTemplate(initial.NamingTemplate);
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnAnyTextChanged));
        Loaded += (_, _) => { _loaded = true; RefreshPreview(); };
    }

    private ImageExportOptions ReadOptions()
    {
        var format = (FormatInput.SelectedItem as FormatChoice)?.Value ?? ImageExportFormat.Original;
        return new ImageExportOptions(DirectoryInput.Text.Trim(), format, TemplateInput.Text);
    }

    private void RefreshPreview()
    {
        if (!_loaded) return;
        try
        {
            var options = ReadOptions();
            PreviewText.Text = ImageExportTemplateService.Preview(_request, options, out var mixed);
            MixedFormatHint.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
            FormatHint.Text = options.Format switch
            {
                ImageExportFormat.Original => "直接复制资源并保留 GIF 等原格式",
                ImageExportFormat.Png => "保留透明通道",
                ImageExportFormat.Jpg => "质量 92，透明区域填充白色",
                _ => "透明区域填充白色"
            };
            ExportStatus.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = true;
        }
        catch (Exception error)
        {
            PreviewText.Text = "—";
            MixedFormatHint.Visibility = Visibility.Collapsed;
            ExportStatus.Foreground = (System.Windows.Media.Brush)FindResource("DangerTextBrush");
            ExportStatus.Text = error.Message;
            ExportStatus.Visibility = Visibility.Visible;
            ExportButton.IsEnabled = false;
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        ImageExportOptions options;
        try { options = ReadOptions(); ImageExportTemplateService.CreatePlan(_request, options); }
        catch (Exception error) { ShowError(error.Message); return; }
        ExportButton.IsEnabled = false;
        ExportStatus.Text = "正在导出…";
        ExportStatus.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        ExportStatus.Visibility = Visibility.Visible;
        try
        {
            var attempt = await _export(options);
            if (attempt.Success) { ResultOptions = options; DialogResult = true; return; }
            if (!string.IsNullOrWhiteSpace(attempt.Error)) ShowError(attempt.Error);
            else { ExportStatus.Visibility = Visibility.Collapsed; ExportButton.IsEnabled = true; }
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void ShowError(string message)
    {
        ExportStatus.Foreground = (System.Windows.Media.Brush)FindResource("DangerTextBrush");
        ExportStatus.Text = message;
        ExportStatus.Visibility = Visibility.Visible;
        ExportButton.IsEnabled = true;
    }
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择导出文件夹", Multiselect = false,
            InitialDirectory = Directory.Exists(DirectoryInput.Text) ? DirectoryInput.Text : null };
        if (dialog.ShowDialog(this) == true) DirectoryInput.Text = dialog.FolderName;
    }
    private void OnClearDirectoryClick(object sender, RoutedEventArgs e) { DirectoryInput.Clear(); DirectoryInput.Focus(); }
    private void OnClearTemplateClick(object sender, RoutedEventArgs e) { TemplateInput.Text = ""; TemplateInput.Focus(); }
    private void OnAnyTextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();
    private void OnInputChanged(object sender, EventArgs e) => RefreshPreview();
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; } }
}
