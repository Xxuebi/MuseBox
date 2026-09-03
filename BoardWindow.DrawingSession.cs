using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private string? _drawingSessionId;
    private Task _drawingSaveTask = Task.CompletedTask;
    private bool _closeAfterDrawingSave;
    private bool _syncingDrawingToolbar;
    private bool _itemsDragMoved;
    private BoardToolMode _lastShapeMode = BoardToolMode.Ellipse;

    private void BeginDrawingSession(BoardDrawingItem item)
    {
        SetToolMode(BoardToolMode.Pen);
        _drawingSessionId = item.Id;
        _selected.Clear();
        _selected.Add(item.Id);
        var last = DrawingGroupService.Read(item).LastOrDefault();
        if (last is not null)
        {
            _syncingDrawingToolbar = true;
            _drawingStrokeColor = last.StrokeColor;
            _drawingFillColor = last.FillColor;
            _drawingDashed = last.Dashed;
            _drawingArrow = last.Kind == BoardDrawingKind.CurveArrow;
            DrawingThicknessSlider.Value = last.StrokeThickness;
            DrawingOpacitySlider.Value = last.StrokeOpacity;
            _syncingDrawingToolbar = false;
        }
        UpdateDrawingToolbarState();
        UpdateSelectionVisuals();
        BoardStatus.Text = "正在续画这组笔迹 · 关闭绘制后作为整体选择";
    }

    private Task CommitStrokeToSessionAsync(BoardDrawingItem stroke, BoardSnapshot before)
    {
        var group = _drawingItems.FirstOrDefault(item => item.Id == _drawingSessionId);
        var isNew = group is null;
        group ??= new BoardDrawingItem
        {
            Id = _drawingSessionId ?? Guid.NewGuid().ToString("N"),
            DrawerId = _drawerId, Kind = BoardDrawingKind.Group, ZIndex = NextZIndex(), LayerName = "绘制"
        };
        DrawingGroupService.Append(group, stroke);
        _drawingSessionId = group.Id;
        if (isNew)
        {
            _drawingItems.Add(group);
            AddDrawingVisual(group);
        }
        else UpdateItemVisual(group);
        PushUndoSnapshot(before);
        _selected.Clear();
        UpdateSelectionVisuals();
        var saved = group.Clone();
        return QueueDrawingSaveAsync(() => isNew
            ? _repository.AddDrawingItemsAsync(new[] { saved })
            : _repository.UpdateDrawingItemsAsync(new[] { saved }));
    }

    private Task QueueDrawingSaveAsync(Func<Task> save)
    {
        _drawingSaveTask = SaveAfterAsync(_drawingSaveTask, save);
        return _drawingSaveTask;
    }

    private static async Task SaveAfterAsync(Task previous, Func<Task> save)
    {
        await previous;
        await save();
    }

    private async Task FlushPendingDrawingAsync()
    {
        if (_previewDrawing is not null || _erasing) await CompleteDrawingAsync();
        await _drawingSaveTask;
    }

    private void ApplyEraserPath(IReadOnlyList<BoardStrokePoint> path, double radius)
    {
        if (path.Count == 0) return;
        var pathBounds = EraserBounds(path, radius);
        foreach (var item in _drawingItems.ToArray())
        {
            if (!_eraserWorldStrokes.TryGetValue(item.Id, out var strokes))
            {
                strokes = DrawingGroupService.ToWorld(item);
                if (strokes.Count == 0) continue;
                _eraserWorldStrokes[item.Id] = strokes;
                var padding = strokes.Max(stroke => stroke.Kind is BoardDrawingKind.Arrow or BoardDrawingKind.CurveArrow
                    ? ArrowGeometry.Padding(stroke.StrokeThickness) : stroke.StrokeThickness / 2);
                _eraserItemBounds[item.Id] = EraserBounds(strokes.SelectMany(stroke => stroke.Points).ToArray(), padding);
            }
            if (!_eraserItemBounds[item.Id].IntersectsWith(pathBounds)) continue;
            var retained = new List<BoardDrawingStroke>();
            var changed = false;
            foreach (var stroke in strokes)
            {
                var effectiveRadius = radius + stroke.StrokeThickness / 2;
                if (stroke.Kind is not (BoardDrawingKind.Pen or BoardDrawingKind.Highlighter))
                {
                    var points = stroke.Points;
                    var outline = points.ToList();
                    if (stroke.Kind == BoardDrawingKind.Ellipse && points.Count == 4)
                    {
                        var a = new Point(points[0].X, points[0].Y);
                        var x = new Point(points[1].X, points[1].Y) - a;
                        var y = new Point(points[3].X, points[3].Y) - a;
                        outline = Enumerable.Range(0, 65).Select(i =>
                        {
                            var angle = i * Math.PI * 2 / 64;
                            var p = a + x * (.5 + .5 * Math.Cos(angle)) + y * (.5 + .5 * Math.Sin(angle));
                            return new BoardStrokePoint(p.X, p.Y);
                        }).ToList();
                    }
                    else if (stroke.Kind == BoardDrawingKind.Rectangle && points.Count == 4)
                        outline.Add(points[0]);
                    if (stroke.Kind is BoardDrawingKind.CurveArrow or BoardDrawingKind.Arrow)
                    {
                        var head = ArrowGeometry.Head(points.Select(p => new Point(p.X, p.Y)).ToArray(), stroke.StrokeThickness);
                        outline.AddRange(head.Select(p => new BoardStrokePoint(p.X, p.Y)));
                    }
                    var hit = PathHitsPath(outline, path, effectiveRadius);
                    if (hit) changed = true;
                    else retained.Add(stroke);
                    continue;
                }
                if (!PathHitsPath(stroke.Points, path, effectiveRadius))
                {
                    retained.Add(stroke);
                    continue;
                }
                changed = true;
                var segment = new List<BoardStrokePoint>();
                void AddPoint(BoardStrokePoint point)
                {
                    if (PointHitsPath(point, path, effectiveRadius))
                    {
                        FlushSegment();
                        return;
                    }
                    segment.Add(point);
                }
                void FlushSegment()
                {
                    if (segment.Count > 0)
                        retained.Add(stroke with { Points = Simplify(segment, .35).ToList() });
                    segment.Clear();
                }
                AddPoint(stroke.Points[0]);
                for (var index = 1; index < stroke.Points.Count; index++)
                {
                    var a = stroke.Points[index - 1];
                    var b = stroke.Points[index];
                    var samples = Math.Clamp((int)Math.Ceiling(Distance(a, b) / Math.Max(1, radius / 2)), 1, 10000);
                    for (var step = 1; step <= samples; step++)
                    {
                        var t = step / (double)samples;
                        AddPoint(new BoardStrokePoint(
                            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
                            a.Pressure + (b.Pressure - a.Pressure) * t));
                    }
                }
                FlushSegment();
            }
            if (!changed) continue;
            _eraserChangedIds.Add(item.Id);
            if (retained.Count == 0)
            {
                _eraserDeletedIds.Add(item.Id);
                _eraserWorldStrokes.Remove(item.Id);
                _eraserItemBounds.Remove(item.Id);
                _drawingItems.Remove(item);
                _selected.Remove(item.Id);
                if (_visuals.Remove(item.Id, out var visual)) WorldCanvas.Children.Remove(visual.Border);
            }
            else
            {
                _eraserWorldStrokes[item.Id] = retained;
                DrawingGroupService.SetWorldStrokes(item, retained);
                UpdateItemVisual(item);
            }
        }
    }

    private static bool IsToolPaletteSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetTreeParent(current))
            if (current is FrameworkElement { Name: "DrawingPalette" or "TextPalette" or "Toolbar" or "ImagePalette" or "ImageLinkButtons" or "TextLinkButtons" or "GifFramesPanel" })
                return true;
        return false;
    }

    private void CloseDrawingPopups()
    {
        DrawingShapesPopup.IsOpen = false;
        DrawingSettingsPopup.IsOpen = false;
        DrawingEraserPopup.IsOpen = false;
    }

    private void OnDrawingShapesClick(object sender, RoutedEventArgs e)
    {
        DrawingSettingsPopup.IsOpen = false;
        DrawingEraserPopup.IsOpen = false;
        ToggleToolPopup(DrawingShapesPopup);
    }

    private void OnDrawingSettingsClick(object sender, RoutedEventArgs e)
    {
        DrawingShapesPopup.IsOpen = false;
        DrawingEraserPopup.IsOpen = false;
        ToggleToolPopup(DrawingSettingsPopup);
    }

    private void UpdateDrawingToolbarState()
    {
        if (DrawingPenButton is null || DrawingStrokePreview is null ||
            DrawingDashButton is null || DrawingSolidButton is null) return;
        var active = (Brush)FindResource("AccentSubtleBrush");
        DrawingPenButton.Background = _toolMode == BoardToolMode.Pen ? active : Brushes.Transparent;
        DrawingEraseButton.Background = _toolMode == BoardToolMode.Eraser ? active : Brushes.Transparent;
        var shape = _toolMode is BoardToolMode.Line or BoardToolMode.Arrow or BoardToolMode.Rectangle or BoardToolMode.Ellipse;
        DrawingShapesButton.Background = shape ? active : Brushes.Transparent;
        foreach (var button in new[] { DrawingLineButton, DrawingArrowButton, DrawingRectangleButton, DrawingEllipseButton })
            button.Background = button.Tag?.ToString() == _toolMode.ToString() ? active : Brushes.Transparent;
        if (shape) _lastShapeMode = _toolMode;
        DrawingShapeIcon.Data = Geometry.Parse(_lastShapeMode switch
        {
            BoardToolMode.Line => "M 2,16 L 16,2",
            BoardToolMode.Arrow => "M 2,16 L 16,2 M 7,2 L 16,2 L 16,11",
            BoardToolMode.Rectangle => "M 2,3 L 16,3 L 16,15 L 2,15 Z",
            _ => "M 9,1 A 8,8 0 1 1 9,17 A 8,8 0 1 1 9,1"
        });
        DrawingStrokePreview.Background = ParseBrush(_drawingStrokeColor, Brushes.DeepSkyBlue);
        DrawingFillPreview.Background = ParseBrush(_drawingFillColor, Brushes.Transparent);
        DrawingSolidButton.Background = !_drawingDashed && !_drawingArrow ? active : Brushes.Transparent;
        DrawingDashButton.Background = _drawingDashed ? active : Brushes.Transparent;
        DrawingCurveArrowButton.Background = _drawingArrow ? active : Brushes.Transparent;
        if (!DrawingThicknessText.IsKeyboardFocusWithin)
            DrawingThicknessText.Text = $"{DrawingThicknessSlider.Value:0.#}";
        if (!DrawingOpacityText.IsKeyboardFocusWithin)
            DrawingOpacityText.Text = $"{DrawingOpacitySlider.Value * 100:0}";
    }

    private void OnDrawingNumericGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox input) input.SelectAll();
    }

    private void OnDrawingNumericMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox input || input.IsKeyboardFocusWithin) return;
        e.Handled = true;
        input.Focus();
        input.SelectAll();
    }

    private void OnDrawingNumericKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox input) return;
        if (e.Key == Key.Enter)
        {
            CommitDrawingNumericInput(input);
            BoardSurface.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateDrawingNumericTexts();
            BoardSurface.Focus();
            e.Handled = true;
        }
    }

    private void OnDrawingNumericLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox input) CommitDrawingNumericInput(input);
    }

    private void CommitDrawingNumericInput(TextBox input)
    {
        var raw = input.Text.Replace("px", "", StringComparison.OrdinalIgnoreCase)
            .Replace("%", "", StringComparison.Ordinal).Trim();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            UpdateDrawingNumericTexts();
            return;
        }
        if (!double.IsFinite(value)) { UpdateDrawingNumericTexts(); return; }
        if (ReferenceEquals(input, DrawingThicknessText))
            DrawingThicknessSlider.Value = Math.Clamp(value, DrawingThicknessSlider.Minimum, DrawingThicknessSlider.Maximum);
        else if (ReferenceEquals(input, DrawingOpacityText))
            DrawingOpacitySlider.Value = Math.Clamp(value / 100, DrawingOpacitySlider.Minimum, DrawingOpacitySlider.Maximum);
        else if (ReferenceEquals(input, EraserDiameterText))
            EraserDiameterSlider.Value = Math.Round(Math.Clamp(value, EraserDiameterSlider.Minimum, EraserDiameterSlider.Maximum));
        UpdateDrawingNumericTexts();
    }

    private void UpdateDrawingNumericTexts()
    {
        DrawingThicknessText.Text = $"{DrawingThicknessSlider.Value:0.#}";
        DrawingOpacityText.Text = $"{DrawingOpacitySlider.Value * 100:0}";
        EraserDiameterText.Text = $"{EraserDiameterSlider.Value:0}";
    }
}
