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
    private readonly DispatcherTimer _autoSaveTimer = new();
    private ExternalImageMonitor? _externalImageMonitor;
    private bool _sceneStatusReading;
    private bool _autoSaveRunning;
    public bool SceneOperationBusy { get; private set; }
    public bool IsOperationBusy => _isBusy;

    private void InitializeScenes()
    {
        _sceneStatusTimer.Tick += async (_, _) =>
        {
            if (_sceneStatusReading || _isBusy) return;
            _sceneStatusReading = true;
            try
            {
                _externalImageMonitor ??= new ExternalImageMonitor(_repository);
                var changes = await _externalImageMonitor.PollAsync();
                foreach (var group in changes.GroupBy(change => change.DrawerId))
                {
                    ((App)Application.Current).FindBoard(group.Key)?.RefreshLinkedImagePaths(group.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
                    if (_drawers.Any(d => d.Id == group.Key) && await _repository.GetLatestAssetPathAsync(group.Key) is { } latest)
                        await UpdateThumbnailAsync(group.Key, latest);
                }
                await RefreshSceneStatusAsync();
            }
            catch (Exception) { /* Database may be briefly unavailable; the save command reports failures. */ }
            finally { _sceneStatusReading = false; }
        };
        _autoSaveTimer.Tick += async (_, _) => await AutoSaveDirtyScenesAsync();
        Loaded += (_, _) => _sceneStatusTimer.Start();
        Closed += (_, _) =>
        {
            _sceneStatusTimer.Stop();
            _externalImageMonitor?.Dispose();
            _autoSaveTimer.Stop();
        };
    }

    private void ApplyAutoSaveSchedule()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.AutoSaveIntervalMinutes, 1, 120));
        if (_settings.AutoSaveEnabled) _autoSaveTimer.Start();
    }

    private async Task AutoSaveDirtyScenesAsync()
    {
        if (_autoSaveRunning || _isBusy || SceneOperationBusy || !_settings.AutoSaveEnabled) return;
        _autoSaveRunning = true;
        SceneOperationBusy = true;
        SetBusy(true);
        try
        {
            var dirty = (await _repository.GetDrawersAsync())
                .Where(drawer => drawer.HasUnsavedScene && !string.IsNullOrWhiteSpace(drawer.ScenePath))
                .ToArray();
            var saved = 0;
            foreach (var drawer in dirty)
            {
                var binding = await _repository.GetSceneBindingAsync(drawer.Id);
                if (binding is null || string.IsNullOrWhiteSpace(binding.FilePath)) continue;
                try
                {
                    using var lease = await PrepareSceneBoardAsync(drawer.Id);
                    var snapshot = await _repository.CaptureSceneAsync(drawer.Id);
                    await PersistSceneSnapshotAsync(drawer.Id, binding.FilePath, snapshot, binding.FileHash,
                        _settings.AskBeforeSavingLinks ? ExternalImageSaveMode.KeepLinks : _settings.LinkedImageSaveMode);
                    saved++;
                }
                catch (SceneFileConflictException)
                {
                    SetStatus($"自动保存已跳过：{Path.GetFileName(binding.FilePath)} 已被外部修改", true);
                }
                catch (Exception error)
                {
                    SetStatus($"自动保存失败：{Friendly(error)}", true);
                }
            }
            if (saved > 0) SetStatus($"已自动保存 {saved} 个场景", false);
        }
        catch (Exception error) { SetStatus($"自动保存失败：{Friendly(error)}", true); }
        finally
        {
            _autoSaveRunning = false;
            SceneOperationBusy = false;
            SetBusy(false);
            await TryRefreshSceneStatusAsync();
        }
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
    private void OnOpenDrawerBoardMenuClick(object sender, RoutedEventArgs e)
    {
        if (!_isBusy && sender is FrameworkElement { Tag: string id })
            ((App)Application.Current).OpenBoard(id);
    }
    private async void OnOpenDrawerSceneFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) await OpenDrawerSceneFileAsync(id);
    }
    private async Task<bool> OpenDrawerSceneFileAsync(string id)
    {
        if (_isBusy) return false;
        try
        {
            var path = _sceneDialogs.OpenFile(this);
            return path is not null && await OpenSceneFileAsync(path, id);
        }
        catch (Exception error) { ShowSceneError("无法打开画板", error); return false; }
    }
    private async void OnSaveSceneClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) await SaveSceneAsync(id, false);
    }
    private async void OnSaveSceneAsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) await SaveSceneAsync(id, true);
    }
    private async void OnExportAllImagesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var choice = _sceneDialogs.Choose(this, "导出所有图像", "选择单独导出画板内的图片，或将画板可见内容合成为一张透明 PNG。", "导出图像", "合成 PNG");
        if (choice == 1) await ExportBoardAsync(id, null, BoardExportMode.IndividualFiles, this);
        else if (choice == 2) await ExportBoardAsync(id, null, BoardExportMode.CompositePng, this);
    }
    public async Task<bool> SaveSceneAsync(string id, bool saveAs, Window? owner = null)
    {
        if (_isBusy) return false;
        SetBusy(true); SceneOperationBusy = true;
        try
        {
            using var lease = await PrepareSceneBoardAsync(id);
            return await SaveSceneCoreAsync(id, saveAs, owner ?? this);
        }
        catch (Exception error) { ShowSceneError("场景保存失败", error); return false; }
        finally { SceneOperationBusy = false; SetBusy(false); await TryRefreshSceneStatusAsync(); }
    }
    private async Task<IDisposable?> PrepareSceneBoardAsync(string id)
    {
        foreach (var model in _drawers.Where(x => x.Id == id && x.IsEditing).ToArray()) await SaveDrawerNameAsync(model.Id);
        return ((App)Application.Current).FindBoard(id) is { } board ? await board.PrepareSceneAsync() : null;
    }
    private async Task<bool> SaveSceneCoreAsync(string id, bool saveAs, Window? owner = null)
    {
        owner ??= this;
        var binding = await _repository.GetSceneBindingAsync(id);
        var path = saveAs ? null : binding?.FilePath;
        if (path is null)
        {
            var model = _drawers.First(x => x.Id == id);
            var filename = string.Concat(model.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            path = _sceneDialogs.SaveFile(owner, filename + SceneFileService.Extension, saveAs);
            if (path is null) return false;
            if (saveAs && binding is not null && string.Equals(Path.GetFullPath(path), binding.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _sceneDialogs.Inform(owner, "请选择其他文件", "另存为需要使用新的文件名或位置，原场景文件将保持不变。");
                return false;
            }
        }
        var snapshot = await _repository.CaptureSceneAsync(id);
        var mode = _settings.LinkedImageSaveMode;
        var remember = false;
        if (snapshot.Document.Assets.Any(a => a.SourceKind == AssetSourceKind.External) && _settings.AskBeforeSavingLinks)
        {
            var choice = _sceneDialogs.ChooseLinkedSave(owner);
            if (choice.Choice == 0) return false;
            mode = choice.Choice == 1 ? ExternalImageSaveMode.Copy : ExternalImageSaveMode.KeepLinks;
            remember = choice.Remember;
        }
        var expected = binding is not null && string.Equals(binding.FilePath, path, StringComparison.OrdinalIgnoreCase) ? binding.FileHash : null;
        SetStatus("正在打包场景，请稍候…", false);
        try { await PersistSceneSnapshotAsync(id, path, snapshot, expected, mode); }
        catch (SceneFileConflictException)
        {
            var choice = _sceneDialogs.Choose(owner, "场景文件已改变", "原文件已被修改、移走或删除。确认覆盖会使用当前画板内容。", "确认覆盖", "另存为");
            if (choice == 0) return false;
            if (choice == 2) return await SaveSceneCoreAsync(id, true, owner);
            await PersistSceneSnapshotAsync(id, path, snapshot, null, mode);
        }
        if (remember)
        {
            _settings.AskBeforeSavingLinks = false;
            _settings.LinkedImageSaveMode = mode;
            try { await _settingsService.SaveAsync(_settings); }
            catch { /* Saving the scene has already succeeded. */ }
        }
        SetStatus($"场景已保存：{Path.GetFileName(path)}", false);
        return true;
    }
    private async Task PersistSceneSnapshotAsync(string id, string path, SceneSnapshot snapshot,
        string? expected, ExternalImageSaveMode mode)
    {
        if (!snapshot.Document.Assets.Any(a => a.SourceKind == AssetSourceKind.External))
        {
            var hash = await Task.Run(() => SceneFileService.WriteAsync(path, snapshot, expected));
            await _repository.MarkSceneSavedAsync(new SceneBinding(id, Path.GetFullPath(path), snapshot.Revision, hash));
            return;
        }
        var board = ((App)Application.Current).FindBoard(id);
        var completeConversion = board?.PrepareLinkedConversionUndo();
        var previousMaterials = board is null ? await _repository.GetMaterialsAsync(id) : Array.Empty<BoardMaterialItem>();
        var previousImages = board is null ? await _repository.GetItemsAsync(id) : Array.Empty<BoardItem>();
        var converted = await LinkedSceneSaveService.SaveAsync(_repository, _importService, id, path, snapshot, mode, expected);
        if (converted && completeConversion is not null) await completeConversion();
        else if (converted)
        {
            var materials = await _repository.GetMaterialsAsync(id);
            var images = await _repository.GetItemsAsync(id);
            var oldMaterials = previousMaterials.Where(m => materials.Any(a => a.Id == m.Id && a.AssetId != m.AssetId)).ToArray();
            var oldImages = previousImages.Where(m => images.Any(a => a.Id == m.Id && a.AssetId != m.AssetId)).ToArray();
            MaterialAreaSession.For(_repository).Queue(new MaterialChange(id, oldMaterials,
                materials.Where(m => oldMaterials.Any(o => o.Id == m.Id)).ToArray(), oldImages,
                images.Where(m => oldImages.Any(o => o.Id == m.Id)).ToArray()));
        }
        if (converted && _drawers.FirstOrDefault(x => x.Id == id) is not null &&
            await _repository.GetLatestAssetPathAsync(id) is { } latest) await UpdateThumbnailAsync(id, latest);
    }

    public async Task<bool> ExportBoardAsync(string id, IReadOnlySet<string>? selectedIds,
        BoardExportMode mode, Window? owner = null)
    {
        if (_isBusy) return false;
        owner ??= this;
        SetBusy(true); SceneOperationBusy = true;
        try
        {
            using var lease = await PrepareSceneBoardAsync(id);
            var snapshot = await _repository.CaptureSceneAsync(id);
            var filename = string.Concat(snapshot.Document.Name.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            if (string.IsNullOrWhiteSpace(filename)) filename = "画板";
            BoardExportResult result;
            if (mode == BoardExportMode.IndividualFiles)
            {
                var request = new ImageExportRequest(snapshot, selectedIds, "图片");
                var initial = new ImageExportOptions(
                    string.IsNullOrWhiteSpace(_settings.ImageExportDirectory)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : _settings.ImageExportDirectory,
                    _settings.ImageExportFormat,
                    string.IsNullOrWhiteSpace(_settings.ImageExportNamingTemplate)
                        ? ImageExportOptions.DefaultTemplate
                        : ImageExportTemplateService.ToCompactTemplate(_settings.ImageExportNamingTemplate));
                BoardExportResult? exported = null;
                ImageExportWindow? dialog = null;
                dialog = new ImageExportWindow(request, initial, async options =>
                {
                    try
                    {
                        var plan = ImageExportTemplateService.CreatePlan(request, options);
                        var action = ImageExportConflictAction.OverwriteAll;
                        if (plan.Conflicts.Count > 0)
                        {
                            var choice = _sceneDialogs.Choose(dialog!, "文件已存在",
                                $"目标位置已有 {plan.Conflicts.Count} 个同名文件。请选择覆盖全部、跳过这些文件，或取消导出。",
                                "覆盖全部", "跳过冲突");
                            if (choice == 0) return new ImageExportAttempt(false);
                            action = choice == 2 ? ImageExportConflictAction.SkipConflicts : ImageExportConflictAction.OverwriteAll;
                            if (action == ImageExportConflictAction.SkipConflicts && plan.Conflicts.Count == plan.Entries.Count)
                                return new ImageExportAttempt(false, "所有目标文件都已存在，没有可导出的文件。");
                        }
                        SetStatus("正在导出图片…", false);
                        exported = await BoardExportService.ExportImagesAsync(request, options, action);
                        _settings.ImageExportDirectory = Path.GetFullPath(options.Directory);
                        _settings.ImageExportFormat = options.Format;
                        _settings.ImageExportNamingTemplate = options.NamingTemplate;
                        try { await _settingsService.SaveAsync(_settings); }
                        catch { /* Export remains successful even if preference persistence is unavailable. */ }
                        return new ImageExportAttempt(true);
                    }
                    catch (Exception error) { return new ImageExportAttempt(false, Friendly(error)); }
                }) { Owner = owner };
                if (dialog.ShowDialog() != true || exported is null) return false;
                result = exported;
            }
            else
            {
                var path = _sceneDialogs.SaveExportPng(owner, filename + ".png");
                if (path is null) return false;
                SetStatus("正在合成 PNG…", false);
                result = await BoardExportService.ExportCompositeAsync(snapshot, selectedIds, path);
            }
            var size = result.PixelWidth > 0 ? $"（{result.PixelWidth} × {result.PixelHeight}）" : string.Empty;
            var capped = result.WasScaledDown ? "，已按尺寸上限等比缩小" : string.Empty;
            SetStatus($"已导出 {result.FileCount} 个文件{size}{capped}", false);
            return true;
        }
        catch (Exception error)
        {
            SetStatus($"导出失败：{Friendly(error)}", true);
            _sceneDialogs.Inform(owner, "导出失败", Friendly(error));
            return false;
        }
        finally { SceneOperationBusy = false; SetBusy(false); await TryRefreshSceneStatusAsync(); }
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
            // Saving may have replaced the very file selected for opening. Use its
            // newly saved contents, not the snapshot validated before the prompt.
            var binding = targetDrawer is not null ? await _repository.GetSceneBindingAsync(targetDrawer) : null;
            using var savedScene = binding is not null &&
                string.Equals(binding.FilePath, path, StringComparison.OrdinalIgnoreCase) && binding.FileHash != prepared.FileHash
                ? await Task.Run(() => SceneFileService.ReadAsync(path)) : null;
            var incoming = savedScene ?? prepared;
            // Keep the old window frozen until the transaction succeeds. If import
            // fails it remains intact; it cannot commit old state after replacement.
            incoming.MoveMaterialsToBoard = !incoming.Document.Viewport.MaterialAreaEnabled;
            var id = await Task.Run(() => _repository.ImportSceneAsync(targetDrawer, incoming, path));
            MaterialAreaSession.For(_repository).Clear(id);
            if (targetDrawer is not null) app.FindBoard(targetDrawer)?.CloseForSceneReplacement();
            await ReloadDrawersAsync();
            app.OpenBoard(id);
            SetStatus($"已打开场景：{Path.GetFileName(path)}", false);
            var missing = SceneFontService.MissingFonts(incoming.Document);
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
        var hasContent = d.Images.Count + d.Texts.Count + d.Drawings.Count + d.Materials.Count > 0 || d.Cover is not null ||
            d.Name != "未命名" || v.BackgroundColor != "#7A7A7A" || v.WindowOpacity != 1 ||
            v.OpacityAffectsImages || !v.ShowWindowFrame || v.Topmost || v.Zoom != 1 || v.PanX != 0 || v.PanY != 0 ||
            v.WindowWidth != 1100 || v.WindowHeight != 760 || v.WindowLeft is not null || v.WindowTop is not null;
        if (binding is not null ? snapshot.Revision <= binding.SavedRevision : !hasContent) return true;
        var message = binding is null
            ? $"当前抽屉“{d.Name}”内有未保存图像或其他内容（包含素材区）。是否先保存？"
            : $"当前抽屉“{d.Name}”的 .mubo 画板有未保存的修改。是否先保存？";
        var choice = _sceneDialogs.Choose(this, "打开前保存当前画板？",
            message + "\n\n保存：保存成功后打开所选文件。\n覆盖：不保存当前内容，直接替换此抽屉；不会删除原 .mubo 文件或外部图像。", "保存", "覆盖");
        if (choice != 1) return choice == 2;
        try { return await SaveSceneCoreAsync(id, false, this); }
        catch (Exception error) { ShowSceneError("场景保存失败", error); return false; }
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
                if (choice == 0 || choice == 1 && !await SaveSceneCoreAsync(drawer.Id, false, this)) return false;
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
