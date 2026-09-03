using System.Windows;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private void ApplyUndoLimit(int limit)
    {
        _undoStepLimit = Math.Clamp(limit, 1, 500);
        TrimHistory(_undo);
        TrimHistory(_redo);
        foreach (var visual in _visuals.Values)
            if (visual.Editor is not null) visual.Editor.UndoLimit = _undoStepLimit;
        UpdateUndoButtons();
    }

    private void TrimHistory(Stack<BoardSnapshot> history)
    {
        if (history.Count <= _undoStepLimit) return;
        var recent = history.Take(_undoStepLimit).Reverse().ToArray();
        history.Clear();
        foreach (var snapshot in recent) history.Push(snapshot);
    }

    private async Task NavigateHistoryAsync(bool redo)
    {
        if (_historyBusy) return;
        _historyBusy = true;
        UpdateUndoButtons();
        try
        {
            await FlushPendingDrawingAsync();
            if (_activeTextEditor is not null) await CommitTextEditingAsync();
            var source = redo ? _redo : _undo;
            var destination = redo ? _undo : _redo;
            if (source.Count == 0) { BoardStatus.Text = redo ? "没有可以重做的操作" : "没有可以撤回的操作"; return; }
            var current = Snapshot();
            await RestoreSnapshotAsync(source.Peek());
            source.Pop();
            destination.Push(current);
            TrimHistory(destination);
            BoardStatus.Text = redo ? "已重做一步撤回" : "已撤回上一步操作";
        }
        finally { _historyBusy = false; UpdateUndoButtons(); }
    }

    private void ApplyRotationDelta(double delta, bool snap)
    {
        if (_rotateItem is null || _rotateSnapshot is null) return;
        var angle = _rotateSnapshot.Rotation + delta;
        if (snap) angle = Math.Round(angle / 5, MidpointRounding.AwayFromZero) * 5;
        _rotateItem.Rotation = BoardMath.NormalizeAngle(angle);
        if (_rotationSnapshots is null) { UpdateItemVisual(_rotateItem); return; }
        var center = new Point(_rotateSnapshot.X + _rotateSnapshot.Width / 2,
            _rotateSnapshot.Y + _rotateSnapshot.Height / 2);
        var effectiveDelta = angle - _rotateSnapshot.Rotation;
        foreach (var snapshot in _rotationSnapshots)
        {
            var live = AllElements.Single(x => x.Id == snapshot.Id);
            var position = BoardMath.RotatePoint(new Point(snapshot.X + snapshot.Width / 2,
                snapshot.Y + snapshot.Height / 2), center, effectiveDelta);
            live.X = position.X - snapshot.Width / 2;
            live.Y = position.Y - snapshot.Height / 2;
            live.Rotation = BoardMath.NormalizeAngle(snapshot.Rotation + effectiveDelta);
            UpdateItemVisual(live);
        }
    }
}
