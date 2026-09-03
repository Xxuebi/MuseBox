using System.Windows;
using System.Windows.Threading;
using ScreenshotCollector.Models;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private bool _sceneOperation;
    private readonly TaskCompletionSource _boardInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _gifStateSave = Task.CompletedTask;
    private readonly Dictionary<string, GifSceneState> _savedGifStates = new();
    public bool HasPendingSceneEdit => _activeTextEditor is not null || _previewDrawing is not null || _imageEditBusy;

    public async Task<IDisposable> PrepareSceneAsync()
    {
        if (_sceneOperation) throw new InvalidOperationException("此画板正在处理另一个场景操作。");
        if (OwnedWindows.Cast<Window>().Any(w => w.IsVisible))
            throw new InvalidOperationException("请先完成或取消画板的编辑窗口，再操作场景文件。");
        _sceneOperation = true;
        IsEnabled = false;
        var lease = new SceneLease(this);
        try
        {
            if (IsVisible) await _boardInitialization.Task;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            while (_imageEditBusy || _historyBusy) await Task.Delay(30);
            await FlushPendingDrawingAsync();
            await CommitTextEditingAsync();
            await _gifStateSave;
            await PersistGifStatesAsync();
            _viewportTimer.Stop();
            await SaveViewportAsync();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void CloseForSceneReplacement()
    {
        if (!_sceneOperation) throw new InvalidOperationException("切换场景前必须先保存待处理操作。");
        _closeAfterDrawingSave = true;
        Close();
    }
    private sealed class SceneLease : IDisposable
    {
        private BoardWindow? _window;
        public SceneLease(BoardWindow window) => _window = window;
        public void Dispose()
        {
            if (_window is not { } window) return;
            _window = null;
            window._sceneOperation = false;
            if (!window._boardClosed) window.IsEnabled = true;
        }
    }
    private Task PersistGifStatesAsync()
    {
        var states = _gifBindings.Where(x => x.Value.Playback is not null).Select(x =>
        {
            var state = x.Value.Playback!;
            return new GifSceneState(x.Key, state.Speed, state.IsPlaying, state.FrameIndex);
        }).ToArray();
        return _repository.SaveGifStatesAsync(states);
    }
    private void QueueGifStateSave()
    {
        // Capture values now; subsequent frame ticks are deliberately not writes.
        var states = _gifBindings.Where(x => x.Value.Playback is not null).Select(x =>
            new GifSceneState(x.Key, x.Value.Playback!.Speed, x.Value.Playback.IsPlaying, x.Value.Playback.FrameIndex)).ToArray();
        _gifStateSave = SaveGifAfterAsync(_gifStateSave, states);
    }
    private async Task SaveGifAfterAsync(Task previous, GifSceneState[] states)
    {
        try { await previous; await _repository.SaveGifStatesAsync(states); }
        catch (Exception error) { BoardStatus.Text = $"GIF 状态保存失败：{error.Message}"; throw; }
    }
    public void UpdateSceneTitle(Drawer drawer)
    {
        var marker = drawer.ScenePath is not null && (drawer.HasUnsavedScene || HasPendingSceneEdit) ? " *" : "";
        var title = $"画板 {drawer.DisplayName} {_drawerId}{marker}";
        Title = title;
        BoardTitle.Text = title;
        BoardTitle.ToolTip = drawer.ScenePath ?? "尚未保存为场景文件";
    }
    private void ConstrainSceneWindowToScreen()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var fromPixels = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?
            .CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var area = screen.WorkingArea;
        var bounds = new System.Windows.Media.MatrixTransform(fromPixels).TransformBounds(new Rect(area.X, area.Y, area.Width, area.Height));
        Width = Math.Max(MinWidth, Math.Min(Width, bounds.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, bounds.Height));
        if (double.IsFinite(Left)) Left = Math.Clamp(Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - Width));
        if (double.IsFinite(Top)) Top = Math.Clamp(Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - Height));
    }
}
