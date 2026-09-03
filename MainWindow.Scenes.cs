using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class MainWindow
{
    private readonly ISceneDialogs _sceneDialogs;
    private readonly DispatcherTimer _sceneStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _sceneStatusReading;
    public bool SceneOperationBusy { get; private set; }
    public bool IsOperationBusy => _isBusy;

    private void InitializeScenes()
    {
        _sceneStatusTimer.Tick += async (_, _) =>
        {
            if (_sceneStatusReading || _isBusy) return;
            _sceneStatusReading = true;
            try { await RefreshSceneStatusAsync(); }
            catch (Exception) { /* Database may be briefly unavailable; the save command reports failures. */ }
            finally { _sceneStatusReading = false; }
        };
        Loaded += (_, _) => _sceneStatusTimer.Start();
        Closed += (_, _) => _sceneStatusTimer.Stop();
    }
    private async Task RefreshSceneStatusAsync()
    {
        var drawers = await _repository.GetDrawersAsync();
        foreach (var drawer in drawers)
        {
            var model = _drawers.FirstOrDefault(x => x.Id == drawer.Id);
            if (model is null) continue;
            var board = ((App)Application.Current).FindBoard(drawer.Id);
            model.ScenePath = drawer.ScenePath;
            model.SceneDirty = drawer.HasUnsavedScene || drawer.ScenePath is not null && board?.HasPendingSceneEdit == true;
            board?.UpdateSceneTitle(drawer);
        }
    }
    private async void OnOpenSceneClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: string id }) return;
        if (_sceneDialogs.OpenFile(this) is { } path) await OpenSceneFileAsync(path, id);
    }
    private async void OnSaveSceneClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) await SaveSceneAsync(id, false);
    }
    private async void OnSaveSceneAsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) await SaveSceneAsync(id, true);
    }
    public async Task<bool> SaveSceneAsync(string id, bool saveAs)
    {
        if (_isBusy) return false;
        SetBusy(true); SceneOperationBusy = true;
        try
        {
            using var lease = await PrepareSceneBoardAsync(id);
            return await SaveSceneCoreAsync(id, saveAs);
        }
        catch (Exception error) { ShowSceneError("场景保存失败", error); return false; }
        finally { SceneOperationBusy = false; SetBusy(false); await TryRefreshSceneStatusAsync(); }
    }
    private async Task<IDisposable?> PrepareSceneBoardAsync(string id)
    {
        foreach (var model in _drawers.Where(x => x.Id == id && x.IsEditing).ToArray()) await SaveDrawerNameAsync(model.Id);
        return ((App)Application.Current).FindBoard(id) is { } board ? await board.PrepareSceneAsync() : null;
    }
    private async Task<bool> SaveSceneCoreAsync(string id, bool saveAs)
    {
        var binding = await _repository.GetSceneBindingAsync(id);
        var path = saveAs ? null : binding?.FilePath;
        if (path is null)
        {
            var model = _drawers.First(x => x.Id == id);
            var filename = string.Concat(model.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            path = _sceneDialogs.SaveFile(this, filename + SceneFileService.Extension, saveAs);
            if (path is null) return false;
            if (saveAs && binding is not null && string.Equals(Path.GetFullPath(path), binding.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _sceneDialogs.Inform(this, "请选择其他文件", "另存为需要使用新的文件名或位置，原场景文件将保持不变。");
                return false;
            }
        }
        var snapshot = await _repository.CaptureSceneAsync(id);
        var expected = binding is not null && string.Equals(binding.FilePath, path, StringComparison.OrdinalIgnoreCase) ? binding.FileHash : null;
        string hash;
        SetStatus("正在打包场景，请稍候…", false);
        try { hash = await Task.Run(() => SceneFileService.WriteAsync(path, snapshot, expected)); }
        catch (SceneFileConflictException)
        {
            var choice = _sceneDialogs.Choose(this, "场景文件已改变", "原文件已被修改、移走或删除。确认覆盖会使用当前画板内容。", "确认覆盖", "另存为");
            if (choice == 0) return false;
            if (choice == 2) return await SaveSceneCoreAsync(id, true);
            hash = await Task.Run(() => SceneFileService.WriteAsync(path, snapshot));
        }
        await _repository.MarkSceneSavedAsync(new SceneBinding(id, Path.GetFullPath(path), snapshot.Revision, hash));
        SetStatus($"场景已保存：{Path.GetFileName(path)}", false);
        return true;
    }
    public async Task<bool> OpenSceneFileAsync(string path, string? targetDrawer = null)
    {
        if (_isBusy) return false;
        SetBusy(true); SceneOperationBusy = true;
        try
        {
            path = Path.GetFullPath(path);
            var app = (App)Application.Current;
            if (targetDrawer is null)
            {
                var existing = (await _repository.GetDrawersAsync()).FirstOrDefault(d =>
                    string.Equals(d.ScenePath, path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) { app.OpenBoard(existing.Id); return true; }
            }
            SetStatus("正在校验场景文件…", false);
            using var prepared = await Task.Run(() => SceneFileService.ReadAsync(path));
            using var lease = targetDrawer is not null ? await PrepareSceneBoardAsync(targetDrawer) : null;
            if (targetDrawer is not null && !await ConfirmSceneReplacementAsync(targetDrawer)) return false;
            // Keep the old window frozen until the transaction succeeds. If import
            // fails it remains intact; it cannot commit old state after replacement.
            var id = await Task.Run(() => _repository.ImportSceneAsync(targetDrawer, prepared, path));
            if (targetDrawer is not null) app.FindBoard(targetDrawer)?.CloseForSceneReplacement();
            await ReloadDrawersAsync();
            app.OpenBoard(id);
            SetStatus($"已打开场景：{Path.GetFileName(path)}", false);
            var missing = SceneFontService.MissingFonts(prepared.Document);
            if (missing.Count > 0)
                _sceneDialogs.Inform(this, "部分字体未安装", $"本机缺少：{string.Join("、", missing.Take(8))}。已使用系统替代字体；原字体名称和文字格式仍保留。");
            return true;
        }
        catch (Exception error) { ShowSceneError("无法打开场景", error); return false; }
        finally { SceneOperationBusy = false; SetBusy(false); await TryRefreshSceneStatusAsync(); }
    }
    private async Task<bool> ConfirmSceneReplacementAsync(string id)
    {
        var snapshot = await _repository.CaptureSceneAsync(id);
        var binding = await _repository.GetSceneBindingAsync(id);
        var d = snapshot.Document;
        var v = d.Viewport;
        var hasContent = d.Images.Count + d.Texts.Count + d.Drawings.Count > 0 || d.Cover is not null ||
            d.Name != "未命名" || v.BackgroundColor != "#7A7A7A" || v.WindowOpacity != 1 ||
            v.OpacityAffectsImages || !v.ShowWindowFrame || v.Topmost || v.Zoom != 1 || v.PanX != 0 || v.PanY != 0 ||
            v.WindowWidth != 1100 || v.WindowHeight != 760 || v.WindowLeft is not null || v.WindowTop is not null;
        if (binding is not null ? snapshot.Revision <= binding.SavedRevision : !hasContent) return true;
        var choice = _sceneDialogs.Choose(this, "打开前保存当前画板？",
            "打开场景将替换这个抽屉的内容。不保存会放弃当前尚未写入场景文件的内容。", "保存", "不保存");
        return choice == 2 || choice == 1 && await SaveSceneCoreAsync(id, false);
    }
    public async Task<bool> ConfirmSceneExitAsync()
    {
        if (_isBusy) return false;
        SetBusy(true); SceneOperationBusy = true;
        var leases = new List<IDisposable>();
        try
        {
            var drawers = await _repository.GetDrawersAsync();
            foreach (var drawer in drawers)
                if (await PrepareSceneBoardAsync(drawer.Id) is { } lease) leases.Add(lease);
            foreach (var drawer in await _repository.GetDrawersAsync())
            {
                if (!drawer.HasUnsavedScene) continue;
                var choice = _sceneDialogs.Choose(this, "场景尚未保存", $"“{drawer.DisplayName}”有未保存修改。退出后本机工作内容仍保留，但场景文件不会自动更新。",
                    "保存", "不保存");
                if (choice == 0 || choice == 1 && !await SaveSceneCoreAsync(drawer.Id, false)) return false;
            }
            return true;
        }
        catch (Exception error) { ShowSceneError("暂时无法退出", error); return false; }
        finally { foreach (var lease in leases) lease.Dispose(); SceneOperationBusy = false; SetBusy(false); }
    }
    private void ShowSceneError(string title, Exception error)
    {
        SetStatus($"{title}：{Friendly(error)}", true);
        _sceneDialogs.Inform(this, title, Friendly(error));
    }
    private async Task TryRefreshSceneStatusAsync()
    {
        try { await RefreshSceneStatusAsync(); }
        catch (Exception error) { SetStatus($"状态刷新失败：{Friendly(error)}", true); }
    }
}
