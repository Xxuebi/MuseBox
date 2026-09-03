using System.Windows;
using ScreenshotCollector.Models;

namespace ScreenshotCollector;

public partial class MainWindow
{
    private async void OnSetDrawerCoverClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: string id }) return;
        var model = _drawers.First(x => x.Id == id);
        try
        {
            SetBusy(true);
            var path = model.Cover?.SourcePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) path = DrawerCoverWindow.ChooseImage(this);
            if (path is null) return;
            var editor = new DrawerCoverWindow(path, path == model.Cover?.SourcePath ? model.Cover.Crop : null) { Owner = this };
            var accepted = editor.ShowDialog() == true;
            using var result = editor.Result;
            if (!accepted || result is null) return;
            var cover = await _importService.SaveDrawerCoverAsync(id, editor.SourcePath, result, editor.Crop);
            ApplyDrawerCover(model, cover);
            SetStatus($"已设置抽屉 {id} 封面", false);
        }
        catch (Exception error) { SetStatus($"封面设置失败：{Friendly(error)}", true); }
        finally { SetBusy(false); }
    }

    private void ApplyDrawerCover(DrawerCardModel model, DrawerCover cover)
    {
        model.Cover = cover;
        model.Thumbnail = LoadThumbnail(cover.PreviewPath);
        if (DrawerList.ItemContainerGenerator.ContainerFromItem(model) is System.Windows.Controls.ContentPresenter presenter)
            FindVisualChild<System.Windows.Controls.Canvas>(presenter, "AnimationLayer")?.Children.Clear();
    }

    private async void OnClearDrawerCoverClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: string id }) return;
        try
        {
            SetBusy(true);
            await ClearDrawerCoverAsync(id);
            SetStatus($"抽屉 {id} 已恢复自动预览", false);
        }
        catch (Exception error) { SetStatus($"移除封面失败：{Friendly(error)}", true); }
        finally { SetBusy(false); }
    }

    private async Task ClearDrawerCoverAsync(string id)
    {
        await _repository.UpdateDrawerCoverAsync(id, null);
        var model = _drawers.First(x => x.Id == id);
        model.Cover = null;
        model.Thumbnail = LoadThumbnail(await _repository.GetLatestAssetPathAsync(id));
    }
}
