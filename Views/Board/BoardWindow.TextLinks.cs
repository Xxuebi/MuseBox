using System.Windows;
using System.Windows.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private BoardTextItem? SelectedLinkedText() => !IsDrawingTool(_toolMode) ? SelectedTextItem() : null;

    private void UpdateTextLinks()
    {
        if (TextLinkButtons is null) return;
        var item = SelectedLinkedText();
        TextLinkButtons.Visibility = Visibility.Collapsed;
        if (item is null || item.WebLink.Length + item.FileLink.Length == 0) return;
        var world = RotatedImageBounds(item);
        var bounds = new Rect(world.X * _viewZoom + _viewPanX, world.Y * _viewZoom + _viewPanY,
            world.Width * _viewZoom, world.Height * _viewZoom);
        var surface = new Rect(0, 0, Math.Max(1, BoardSurface.ActualWidth), Math.Max(1, BoardSurface.ActualHeight));
        if (!bounds.IntersectsWith(surface)) return;
        TextWebLinkButton.Visibility = item.WebLink.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TextFileLinkButton.Visibility = item.FileLink.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TextWebLinkButton.ToolTip = item.WebLink;
        TextFileLinkButton.ToolTip = item.FileLink;
        TextLinkButtons.Visibility = Visibility.Visible;
        TextLinkButtons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = TextLinkButtons.DesiredSize;
        const double gap = 18; // Leave the corner's rotation/resize handles unobstructed.
        var x = Math.Clamp(bounds.Right - size.Width, 8, Math.Max(8, surface.Width - size.Width - 8));
        var y = bounds.Bottom + gap;
        if (y + size.Height > surface.Height - 8)
        {
            // At the bottom edge use the outside right/left, never cover the annotation.
            y = Math.Clamp(bounds.Bottom - size.Height, 8, Math.Max(8, surface.Height - size.Height - 8));
            if (bounds.Right + gap + size.Width <= surface.Width - 8) x = bounds.Right + gap;
            else if (bounds.Left - gap - size.Width >= 8) x = bounds.Left - gap - size.Width;
            else { TextLinkButtons.Visibility = Visibility.Collapsed; return; }
        }
        Canvas.SetLeft(TextLinkButtons, x);
        Canvas.SetTop(TextLinkButtons, y);
    }

    private async void OnTextLinksClick(object sender, RoutedEventArgs e)
    {
        var item = SelectedLinkedText();
        if (item is not null) await EditTextLinksAsync(item);
    }

    private async Task EditTextLinksAsync(BoardTextItem item)
    {
        if (_boardClosed || _sceneOperation || _imageEditBusy || !_textItems.Contains(item)) return;
        CloseToolPopups();
        try
        {
            // Finalize text first so changing links has its own board undo step.
            await CommitTextEditingAsync();
            if (_boardClosed || _sceneOperation || !_textItems.Contains(item)) return;
            var dialog = new ImageLinksWindow(item.WebLink, item.FileLink, "注释链接") { Owner = this };
            if (dialog.ShowDialog() == true) await SaveTextLinksAsync(item, dialog.WebLink, dialog.FileLink);
        }
        catch (Exception error) { BoardStatus.Text = $"链接保存失败：{error.Message}"; }
    }

    private async Task SaveTextLinksAsync(BoardTextItem item, string web, string file)
    {
        var webLink = ImageLinkService.NormalizeWeb(web);
        var fileLink = ImageLinkService.NormalizeFile(file);
        await CommitTextEditingAsync();
        if (!_textItems.Contains(item) || webLink == item.WebLink && fileLink == item.FileLink) return;
        _imageEditBusy = true;
        BoardSurface.IsHitTestVisible = false;
        try
        {
            var before = Snapshot();
            var updated = item.Clone();
            updated.WebLink = webLink;
            updated.FileLink = fileLink;
            await _repository.UpdateTextItemsAsync(new[] { updated });
            PushUndoSnapshot(before);
            item.WebLink = webLink;
            item.FileLink = fileLink;
            UpdateTextLinks();
            BoardStatus.Text = "注释链接已保存 · 可撤回";
        }
        finally { _imageEditBusy = false; BoardSurface.IsHitTestVisible = true; }
    }

    private void OnOpenTextWebLink(object sender, RoutedEventArgs e) => OpenBoardWebLink(SelectedLinkedText()?.WebLink);
    private void OnOpenTextFileLink(object sender, RoutedEventArgs e)
    {
        if (SelectedLinkedText() is { } item)
            OpenBoardFileLink(item.FileLink, () => _ = EditTextLinksAsync(item));
    }
}
