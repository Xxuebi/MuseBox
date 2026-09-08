using System.Windows;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
namespace ScreenshotCollector;
public partial class MainWindow
{
    private async Task<ImageImportDestination> CollectionDestinationAsync(string drawerId)
        => (await _repository.GetViewportAsync(drawerId)).MaterialAreaEnabled
            ? ImageImportDestination.Materials : ImageImportDestination.Board;

    public async Task<bool> SetMaterialAreaEnabledAsync(string drawerId, bool enabled)
    {
        if (_isBusy || SceneOperationBusy) return false;
        if ((await _repository.GetViewportAsync(drawerId)).MaterialAreaEnabled == enabled) return true;
        SetBusy(true); SceneOperationBusy = true;
        IDisposable? lease = null;
        try
        {
            bool moveRemaining = false;
            if (!enabled)
            {
                var materials = await _repository.GetMaterialsAsync(drawerId);
                if (materials.Count > 0)
                {
                    if (_sceneDialogs.Choose(this, "关闭当前画板素材区",
                        $"将当前画板的 {materials.Count} 张素材移入画板。其他抽屉不受影响，可在画板撤回。",
                        "移入画板并关闭", "保持开启") != 1) return false;
                    moveRemaining = true;
                }
            }
            lease = await PrepareSceneBoardAsync(drawerId);
            var change = moveRemaining ? await MaterialAreaService.PlanMoveAsync(_repository, drawerId) : null;
            await _repository.SetMaterialAreaEnabledAsync(drawerId, enabled, change);
            if (change is not null) MaterialAreaSession.For(_repository).Queue(change);
            if (((App)Application.Current).FindBoard(drawerId) is { } board)
                await board.ReloadAsync();
            await ReloadDrawersAsync();
            return true;
        }
        catch (Exception error) { ShowSceneError("素材区设置未完成", error); return false; }
        finally { lease?.Dispose(); SceneOperationBusy = false; SetBusy(false); }
    }

    private async Task<bool> ApplyMaterialSettingsAsync(AppSettings next)
    {
        try { await _settingsService.SaveAsync(next); _settings = next; return true; }
        catch (Exception error) { ShowSceneError("设置未保存", error); return false; }
    }
}

