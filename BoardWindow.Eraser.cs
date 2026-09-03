using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private Point _eraserPointer;
    private bool _eraserPointerOnBoard;
    private double _eraserGestureRadius;

    private void OnDrawingEraserSizeClick(object sender, RoutedEventArgs e)
    {
        if (_toolMode != BoardToolMode.Eraser) SetToolMode(BoardToolMode.Eraser);
        DrawingShapesPopup.IsOpen = false;
        DrawingSettingsPopup.IsOpen = false;
        ToggleToolPopup(DrawingEraserPopup);
    }

    private void OnEraserDiameterChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EraserDiameterText is null) return;
        if (!EraserDiameterText.IsKeyboardFocusWithin)
            EraserDiameterText.Text = $"{e.NewValue:0}";
        RefreshEraserCursor();
    }

    private void OnBoardPointerMove(object sender, MouseEventArgs e)
    {
        UpdateEraserCursor(e.GetPosition(BoardSurface), IsDrawingSurfaceSource(e.OriginalSource as DependencyObject));
        OnBoardRightMouseMove(sender, e);
    }

    private void OnBoardPointerLeave(object sender, MouseEventArgs e)
    {
        _eraserPointerOnBoard = false;
        HideEraserCursor();
    }

    private void OnBoardStylusHover(object sender, StylusEventArgs e) =>
        UpdateEraserCursor(e.GetPosition(BoardSurface), IsDrawingSurfaceSource(e.OriginalSource as DependencyObject));

    private void OnBoardStylusLeave(object sender, StylusEventArgs e)
    {
        _eraserPointerOnBoard = false;
        HideEraserCursor();
    }

    private static bool IsDrawingSurfaceSource(DependencyObject? source) =>
        !IsToolPaletteSource(source) &&
        FindVisualAncestor<Button>(source) is null &&
        FindVisualAncestor<ComboBox>(source) is null &&
        FindVisualAncestor<System.Windows.Controls.Primitives.Thumb>(source) is null;

    private void UpdateEraserCursor(Point point, bool onBoard)
    {
        _eraserPointer = point;
        _eraserPointerOnBoard = onBoard;
        RefreshEraserCursor();
    }

    private void RefreshEraserCursor()
    {
        if (EraserCursorOverlay is null || EraserDiameterSlider is null) return;
        if (_toolMode != BoardToolMode.Eraser || !_eraserPointerOnBoard || _spaceDown || _panning || _rightWindowDragCandidate ||
            DrawingEraserPopup.IsOpen || DrawingShapesPopup.IsOpen || DrawingSettingsPopup.IsOpen ||
            BoardSurface.ContextMenu?.IsOpen == true ||
            _eraserPointer.X < 0 || _eraserPointer.Y < 0 ||
            _eraserPointer.X > BoardSurface.ActualWidth || _eraserPointer.Y > BoardSurface.ActualHeight)
        {
            HideEraserCursor();
            return;
        }
        // Size is in screen DIPs. During a gesture the world radius is fixed, so
        // zooming cannot change the actual region erased when the pointer lifts.
        var diameter = _erasing ? _eraserGestureRadius * _viewZoom * 2 : EraserDiameterSlider.Value;
        EraserCursorOverlay.Width = diameter;
        EraserCursorOverlay.Height = diameter;
        Canvas.SetLeft(EraserCursorOverlay, _eraserPointer.X - diameter / 2);
        Canvas.SetTop(EraserCursorOverlay, _eraserPointer.Y - diameter / 2);
        var changed = EraserCursorOverlay.Visibility != Visibility.Visible;
        EraserCursorOverlay.Visibility = Visibility.Visible;
        if (changed) Mouse.UpdateCursor();
    }

    private void HideEraserCursor()
    {
        if (EraserCursorOverlay is null || EraserCursorOverlay.Visibility == Visibility.Collapsed) return;
        EraserCursorOverlay.Visibility = Visibility.Collapsed;
        Mouse.UpdateCursor();
    }

    private void OnBoardQueryCursor(object sender, QueryCursorEventArgs e)
    {
        if (_toolMode != BoardToolMode.Eraser || EraserCursorOverlay.Visibility != Visibility.Visible ||
            !IsDrawingSurfaceSource(e.OriginalSource as DependencyObject)) return;
        e.Cursor = Cursors.None;
        e.Handled = true;
    }
}
