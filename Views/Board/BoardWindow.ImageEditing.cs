using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Bitmap = System.Drawing.Bitmap;
using RotateFlipType = System.Drawing.RotateFlipType;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private string? _imageToolbarId;
    private bool _imageEditBusy;
    private int _spotlightGeneration;

    private BoardItem? SelectedToolbarImage() => _toolMode == BoardToolMode.Select && _selected.Count == 1
        ? _items.FirstOrDefault(x => _selected.Contains(x.Id)) : null;

    private void CloseImageToolbar()
    {
        _imageToolbarId = null;
        PopupTransitions.HidePanel(ImagePalette);
        ImagePalette.Visibility = Visibility.Collapsed;
        ImagePalette.BeginAnimation(OpacityProperty, null);
        ImagePalette.RenderTransform = Transform.Identity;
        GifSpeedPopup.IsOpen = false;
        CloseGifFrames();
        AnimateImageSpotlight(false);
        ApplyItemOpacity();
    }

    private void OnImageToolbarToggleClick(object sender, RoutedEventArgs e)
    {
        var item = SelectedToolbarImage();
        if (item is null) return;
        var opening = _imageToolbarId != item.Id;
        if (!opening) CloseImageToolbar();
        else
        {
            CloseToolPopups();
            _imageToolbarId = item.Id;
            AnimateImageSpotlight(true);
        }
        UpdateImageToolbar();
        if (opening)
        {
            PopupTransitions.ShowScalePanel(ImagePalette, Math.Max(.12, 38 / Math.Max(38, ImagePalette.DesiredSize.Width)), .65);
        }
    }

    private void AnimateImageSpotlight(bool show)
    {
        var generation = ++_spotlightGeneration;
        if (!IsVisible)
        {
            ImageFocusShade.BeginAnimation(OpacityProperty, null);
            ImageFocusShade.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var from = ImageFocusShade.Visibility == Visibility.Visible ? ImageFocusShade.Opacity : 0;
        if (show) ImageFocusShade.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(from, show ? 1 : 0, TimeSpan.FromMilliseconds(160));
        if (!show) animation.Completed += (_, _) =>
        {
            if (generation != _spotlightGeneration) return;
            ImageFocusShade.Visibility = Visibility.Collapsed;
            ImageFocusShade.BeginAnimation(OpacityProperty, null);
        };
        ImageFocusShade.BeginAnimation(OpacityProperty, animation);
    }

    private void UpdateImageToolbar()
    {
        if (ImageEditToggle is null) return;
        var item = SelectedToolbarImage();
        if (item is null)
        {
            if (_imageToolbarId is not null) CloseImageToolbar();
            ImageEditToggle.Visibility = ImageLinkButtons.Visibility = Visibility.Collapsed;
            return;
        }
        if (_imageToolbarId is not null && _imageToolbarId != item.Id) CloseImageToolbar();
        var worldBounds = RotatedImageBounds(item);
        var bounds = new Rect(worldBounds.X * _viewZoom + _viewPanX, worldBounds.Y * _viewZoom + _viewPanY,
            worldBounds.Width * _viewZoom, worldBounds.Height * _viewZoom);
        var surface = new Rect(0, 0, Math.Max(1, BoardSurface.ActualWidth), Math.Max(1, BoardSurface.ActualHeight));
        if (!bounds.IntersectsWith(surface))
        {
            if (_imageToolbarId is not null) CloseImageToolbar();
            ImageEditToggle.Visibility = ImageLinkButtons.Visibility = Visibility.Collapsed;
            return;
        }
        var open = _imageToolbarId == item.Id;
        var gif = GifAnimationService.IsGif(item.AssetPath);
        ImageEditPencilIcon.Visibility = gif ? Visibility.Collapsed : Visibility.Visible;
        GifEditMotionIcon.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
        ImageEditToggle.ToolTip = gif ? "打开动图工具栏" : "打开图片工具栏";
        System.Windows.Automation.AutomationProperties.SetName(ImageEditToggle, ImageEditToggle.ToolTip.ToString());
        StaticImageTools.Visibility = gif ? Visibility.Collapsed : Visibility.Visible;
        GifImageTools.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
        ImagePalette.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ImageEditToggle.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (open) ImageFocusShade.Visibility = Visibility.Visible;
        PositionImageControl(open ? ImagePalette : ImageEditToggle, bounds, false);
        if (open)
        {
            var hole = new RectangleGeometry(new Rect(item.X * _viewZoom + _viewPanX, item.Y * _viewZoom + _viewPanY,
                item.Width * _viewZoom, item.Height * _viewZoom));
            hole.Transform = new RotateTransform(item.Rotation, (item.X + item.Width / 2) * _viewZoom + _viewPanX,
                (item.Y + item.Height / 2) * _viewZoom + _viewPanY);
            ImageFocusShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(surface), hole);
            if (_visuals.TryGetValue(item.Id, out var visual)) visual.Border.Opacity = 1;
        }
        ImageWebLinkButton.Visibility = item.WebLink.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ImageFileLinkButton.Visibility = item.FileLink.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ImageWebLinkButton.ToolTip = item.WebLink;
        ImageFileLinkButton.ToolTip = item.FileLink;
        ImageLinkButtons.Visibility = item.WebLink.Length + item.FileLink.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ImageLinkButtons.Visibility == Visibility.Visible) PositionImageControl(ImageLinkButtons, bounds, true);
        UpdateGifToolbar();
        PositionGifFrames(bounds);
    }

    private void PositionImageControl(FrameworkElement control, Rect bounds, bool lowerRight)
    {
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = control.DesiredSize.Width;
        var height = control.DesiredSize.Height;
        var x = lowerRight ? bounds.Right - width - 8 : bounds.X + (bounds.Width - width) / 2;
        var y = lowerRight ? bounds.Bottom - height - 8 : bounds.Top - height - 10;
        var minY = !lowerRight && _toolbarVisible ? 82 : 8;
        if (!lowerRight && y < minY) y = bounds.Bottom + 10;
        Canvas.SetLeft(control, Math.Clamp(x, 8, Math.Max(8, BoardSurface.ActualWidth - width - 8)));
        Canvas.SetTop(control, Math.Clamp(y, minY, Math.Max(minY, BoardSurface.ActualHeight - height - 8)));
    }

    private async void OnImageEditorClick(object sender, RoutedEventArgs e)
    {
        var item = SelectedToolbarImage();
        if (item is null || _imageEditBusy) return;
        try
        {
            var editor = new ImageEditorWindow(item.AssetPath, (sender as FrameworkElement)?.Tag?.ToString() ?? "Edit") { Owner = this };
            if (editor.ShowDialog() != true || editor.ResultBitmap is null) return;
            using var bitmap = editor.ResultBitmap;
            if (editor.SaveAsNewImage) await AddEditedImageAsync(item, bitmap);
            else await ReplaceImageBitmapAsync(item, bitmap);
        }
        catch (Exception error) { BoardStatus.Text = $"图片编辑失败：{error.Message}"; }
    }

    private async void OnImageFlipClick(object sender, RoutedEventArgs e)
    {
        var item = SelectedToolbarImage();
        if (item is null || _imageEditBusy) return;
        try
        {
            using var bitmap = new Bitmap(item.AssetPath);
            bitmap.RotateFlip((sender as FrameworkElement)?.Tag?.ToString() == "Vertical"
                ? RotateFlipType.RotateNoneFlipY : RotateFlipType.RotateNoneFlipX);
            await ReplaceImageBitmapAsync(item, bitmap);
        }
        catch (Exception error) { BoardStatus.Text = $"翻转失败：{error.Message}"; }
    }

    private async void OnImageRotateClick(object sender, RoutedEventArgs e)
    {
        var item = SelectedToolbarImage();
        if (item is null || _imageEditBusy) return;
        try
        {
            using var bitmap = new Bitmap(item.AssetPath);
            bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
            await ReplaceImageBitmapAsync(item, bitmap);
        }
        catch (Exception error) { BoardStatus.Text = $"旋转失败：{error.Message}"; }
    }

    private async Task AddEditedImageAsync(BoardItem item, Bitmap bitmap)
    {
        _imageEditBusy = true;
        BoardSurface.IsHitTestVisible = false;
        try
        {
            var before = Snapshot();
            using var previous = new Bitmap(item.AssetPath);
            var asset = await _importService.SaveEditedBitmapAsync(bitmap);
            var added = item.Clone();
            added.Id = Guid.NewGuid().ToString("N");
            added.GroupId = string.Empty;
            added.CreatedUtc = DateTime.UtcNow;
            added.AssetId = asset.Asset.Id;
            added.AssetPath = asset.FullPath;
            added.Width = Math.Max(1, item.Width * bitmap.Width / previous.Width);
            added.Height = Math.Max(1, item.Height * bitmap.Height / previous.Height);
            added.X += 32 / _viewZoom;
            added.Y += 32 / _viewZoom;
            added.ZIndex = _items.Cast<BoardElement>().Concat(_textItems).Concat(_drawingItems)
                .Select(x => x.ZIndex).DefaultIfEmpty(-1).Max() + 1;
            await _repository.AddItemsAsync(new[] { added });
            PushUndoSnapshot(before);
            _items.Add(added);
            _selected.Clear();
            _selected.Add(added.Id);
            CloseImageToolbar();
            RenderItems();
            if (_visuals.TryGetValue(added.Id, out var visual) && visual.Border.Child is Grid grid)
            {
                var image = grid.Children.OfType<Image>().FirstOrDefault();
                if (image is not null) image.Source = ImageEditorWindow.ToSource(bitmap);
            }
            UpdateSelectionVisuals();
            BoardStatus.Text = "已另存为新图片 · 原图片保持不变 · 可撤回";
        }
        finally { _imageEditBusy = false; BoardSurface.IsHitTestVisible = true; }
    }

    private async Task ReplaceImageBitmapAsync(BoardItem item, Bitmap bitmap)
    {
        _imageEditBusy = true;
        BoardSurface.IsHitTestVisible = false;
        try
        {
            var before = Snapshot();
            using var previous = new Bitmap(item.AssetPath);
            var asset = await _importService.SaveEditedBitmapAsync(bitmap);
            var updated = item.Clone();
            updated.AssetId = asset.Asset.Id;
            updated.AssetPath = asset.FullPath;
            updated.Width = Math.Max(1, item.Width * bitmap.Width / previous.Width);
            updated.Height = Math.Max(1, item.Height * bitmap.Height / previous.Height);
            updated.X += (item.Width - updated.Width) / 2;
            updated.Y += (item.Height - updated.Height) / 2;
            await _repository.UpdateItemsAsync(new[] { updated });
            PushUndoSnapshot(before);
            item.AssetId = updated.AssetId; item.AssetPath = updated.AssetPath;
            item.X = updated.X; item.Y = updated.Y; item.Width = updated.Width; item.Height = updated.Height;
            RenderItems();
            if (_visuals.TryGetValue(item.Id, out var visual) && visual.Border.Child is Grid grid)
            {
                var image = grid.Children.OfType<Image>().FirstOrDefault();
                if (image is not null) image.Source = ImageEditorWindow.ToSource(bitmap);
            }
            UpdateSelectionVisuals();
            BoardStatus.Text = "已应用图片编辑 · 可撤回";
        }
        finally { _imageEditBusy = false; BoardSurface.IsHitTestVisible = true; }
    }

    private async void OnImageLinksClick(object sender, RoutedEventArgs e)
    {
        var item = SelectedToolbarImage();
        if (item is not null) await EditImageLinksAsync(item);
    }

    private async Task EditImageLinksAsync(BoardItem item)
    {
        if (_boardClosed || _sceneOperation || _imageEditBusy || !_items.Contains(item)) return;
        var dialog = new ImageLinksWindow(item.WebLink, item.FileLink) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await SaveImageLinksAsync(item, dialog.WebLink, dialog.FileLink); }
        catch (Exception error) { BoardStatus.Text = $"链接保存失败：{error.Message}"; }
    }

    private async Task SaveImageLinksAsync(BoardItem item, string web, string file)
    {
        var updated = item.Clone();
        updated.WebLink = ImageLinkService.NormalizeWeb(web);
        updated.FileLink = ImageLinkService.NormalizeFile(file);
        if (updated.WebLink == item.WebLink && updated.FileLink == item.FileLink) return;
        _imageEditBusy = true;
        BoardSurface.IsHitTestVisible = false;
        try
        {
            var before = Snapshot();
            await _repository.UpdateItemsAsync(new[] { updated });
            PushUndoSnapshot(before);
            item.WebLink = updated.WebLink;
            item.FileLink = updated.FileLink;
            UpdateImageToolbar();
            BoardStatus.Text = "图片链接已保存";
        }
        finally { _imageEditBusy = false; BoardSurface.IsHitTestVisible = true; }
    }

    private void OnOpenImageWebLink(object sender, RoutedEventArgs e) => OpenBoardWebLink(SelectedToolbarImage()?.WebLink);

    private void OpenBoardWebLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return;
        try
        {
            Process.Start(new ProcessStartInfo(ImageLinkService.NormalizeWeb(link)) { UseShellExecute = true });
        }
        catch (Exception error) { BoardStatus.Text = $"无法打开网页：{error.Message}"; }
    }

    private void OnOpenImageFileLink(object sender, RoutedEventArgs e)
    {
        if (SelectedToolbarImage() is { } item)
            OpenBoardFileLink(item.FileLink, () => _ = EditImageLinksAsync(item));
    }

    private async void OpenBoardFileLink(string? link, Action? editLinks = null)
    {
        if (string.IsNullOrWhiteSpace(link) || _boardClosed || _sceneOperation) return;
        try
        {
            var path = ImageLinkService.NormalizeFile(link);
            if (!await Task.Run(() => File.Exists(path) || Directory.Exists(path)))
            {
                ShowUnavailableFileLink("关联的文件或文件夹在本机不存在，或当前没有访问权限。", editLinks);
                return;
            }
            if (_boardClosed || _sceneOperation) return;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            // Executables/scripts are revealed in Explorer rather than executed as a link.
            if (File.Exists(path) && new[] { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".lnk", ".url", ".scr" }.Contains(extension))
            {
                var start = new ProcessStartInfo("explorer.exe");
                start.ArgumentList.Add("/select,");
                start.ArgumentList.Add(path);
                Process.Start(start);
            }
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { ShowUnavailableFileLink(error.Message, editLinks); }
    }

    private void ShowUnavailableFileLink(string message, Action? editLinks)
    {
        if (_boardClosed || _sceneOperation) return;
        BoardStatus.Text = "无法打开文件链接";
        if (PromptWindow.Confirm(this, "无法打开", message + "\n可以在链接设置中清除原地址或重新选择文件。", "编辑链接"))
            editLinks?.Invoke();
    }
}
