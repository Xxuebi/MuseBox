using System.Windows;
using System.Windows.Threading;
using ScreenshotCollector.Models;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private readonly Dictionary<string, IReadOnlyList<BoardDrawingStroke>> _eraserWorldStrokes = new();
    private readonly Dictionary<string, Rect> _eraserItemBounds = new();
    private readonly HashSet<string> _eraserChangedIds = new();
    private readonly HashSet<string> _eraserDeletedIds = new();
    private DispatcherOperation? _eraserPreviewOperation;
    private int _eraserProcessedPointCount;

    private void BeginEraserPreview()
    {
        ResetEraserPreview();
        RequestEraserPreviewUpdate();
    }

    private void ResetEraserPreview()
    {
        _eraserPreviewOperation?.Abort();
        _eraserPreviewOperation = null;
        _eraserWorldStrokes.Clear();
        _eraserItemBounds.Clear();
        _eraserChangedIds.Clear();
        _eraserDeletedIds.Clear();
        _eraserProcessedPointCount = 0;
    }

    private void RequestEraserPreviewUpdate()
    {
        if (_eraserPreviewOperation?.Status == DispatcherOperationStatus.Pending) return;
        _eraserPreviewOperation = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _eraserPreviewOperation = null;
            UpdateEraserPreview();
        }));
    }

    private void UpdateEraserPreview()
    {
        if (!_erasing || _eraserProcessedPointCount >= _drawingPoints.Count) return;
        // Only process input added since the previous frame, retaining its shared
        // endpoint so fast pointer motion leaves a continuous erased path.
        var path = _drawingPoints.Skip(Math.Max(0, _eraserProcessedPointCount - 1)).ToArray();
        ApplyEraserPath(path, _eraserGestureRadius);
        _eraserProcessedPointCount = _drawingPoints.Count;
    }

    private Task CompleteEraserPreviewAsync(BoardSnapshot before)
    {
        _eraserPreviewOperation?.Abort();
        _eraserPreviewOperation = null;
        UpdateEraserPreview();
        var deleted = _eraserDeletedIds.ToArray();
        var updated = _drawingItems.Where(item => _eraserChangedIds.Contains(item.Id))
            .Select(item => item.Clone()).ToArray();
        _erasing = false;
        _drawingPoints.Clear();
        ResetEraserPreview();
        UpdateSelectionVisuals();
        RefreshEraserCursor();
        if (deleted.Length == 0 && updated.Length == 0) return Task.CompletedTask;
        // Preview changes stay in memory. One completed gesture produces one undo
        // entry and one queued persistence operation, never database writes per frame.
        PushUndoSnapshot(before);
        return QueueDrawingSaveAsync(async () =>
        {
            if (deleted.Length > 0) await _repository.DeleteDrawingItemsAsync(deleted);
            if (updated.Length > 0) await _repository.UpdateDrawingItemsAsync(updated);
        });
    }

    private static Rect EraserBounds(IReadOnlyList<BoardStrokePoint> points, double padding)
    {
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var bounds = new Rect(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
        bounds.Inflate(padding, padding);
        return bounds;
    }
}
