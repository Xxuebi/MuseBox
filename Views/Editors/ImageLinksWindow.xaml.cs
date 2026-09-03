using System.Windows;
using System.Windows.Input;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class ImageLinksWindow : Window
{
    public string WebLink { get; private set; } = "";
    public string FileLink { get; private set; } = "";
    public ImageLinksWindow(string webLink, string fileLink, string title = "图片链接")
    {
        InitializeComponent();
        Title = LinksTitleText.Text = title;
        WebLinkInput.Text = webLink;
        FileLinkInput.Text = fileLink;
    }
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            WebLink = ImageLinkService.NormalizeWeb(WebLinkInput.Text);
            FileLink = ImageLinkService.NormalizeFile(FileLinkInput.Text);
            DialogResult = true;
        }
        catch (Exception error) { LinkStatus.Text = error.Message; LinkStatus.Visibility = Visibility.Visible; }
    }
    private void OnClearLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && FindName(name) is System.Windows.Controls.TextBox input)
        {
            input.Clear();
            input.Focus();
            LinkStatus.Visibility = Visibility.Collapsed;
        }
    }
    private void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择关联的文件", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) FileLinkInput.Text = dialog.FileName;
    }
    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择自定义文件夹" };
        if (dialog.ShowDialog(this) == true) FileLinkInput.Text = dialog.FolderName;
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
