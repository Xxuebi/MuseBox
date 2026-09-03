using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private IEnumerable<Popup> ToolPopups => new[]
        { DrawingShapesPopup, DrawingEraserPopup, DrawingSettingsPopup, TextMorePopup };

    private void InitializeToolPopups()
    {
        TextMorePopup.PlacementTarget = TextMoreButton;
        foreach (var popup in ToolPopups)
            popup.Opened += (_, _) => AlignPopupPointer(popup);
        // Native outside-click capture closes a StaysOpen=false popup on mouse-down,
        // before its opener receives Click, so a second click would reopen it.
        // Own dismissal instead, allowing the opener to toggle the current state.
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnToolPopupPointerDown), true);
        AddHandler(Stylus.PreviewStylusDownEvent, new StylusDownEventHandler(OnToolPopupStylusDown), true);
        Deactivated += (_, _) => CloseToolPopups();
        LocationChanged += (_, _) => CloseToolPopups();
        SizeChanged += (_, _) => CloseToolPopups();
    }

    private void OnToolPopupPointerDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        DismissToolPopupsOutside(source);
        if (GifSpeedPopup.IsOpen && !IsWithinPopupElement(source, GifSpeedButton) && !IsWithinPopupElement(source, GifSpeedPopup.Child))
            GifSpeedPopup.IsOpen = false;
        if (GroupOptionsPopup.IsOpen && !IsWithinPopupElement(source, GroupOptionsButton) &&
            !IsWithinPopupElement(source, GroupOptionsPopup.Child)) GroupOptionsPopup.IsOpen = false;
        if (GroupBorderOptionsPopup.IsOpen && !IsWithinPopupElement(source, GroupBorderOptionsButton) &&
            !IsWithinPopupElement(source, GroupBorderOptionsPopup.Child)) GroupBorderOptionsPopup.IsOpen = false;
    }

    private void OnToolPopupStylusDown(object sender, StylusDownEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        DismissToolPopupsOutside(source);
        if (GroupOptionsPopup.IsOpen && !IsWithinPopupElement(source, GroupOptionsButton) &&
            !IsWithinPopupElement(source, GroupOptionsPopup.Child)) GroupOptionsPopup.IsOpen = false;
        if (GroupBorderOptionsPopup.IsOpen && !IsWithinPopupElement(source, GroupBorderOptionsButton) &&
            !IsWithinPopupElement(source, GroupBorderOptionsPopup.Child)) GroupBorderOptionsPopup.IsOpen = false;
    }

    private void DismissToolPopupsOutside(DependencyObject? source)
    {
        foreach (var popup in ToolPopups)
        {
            if (!popup.IsOpen || IsWithinPopupElement(source, popup.PlacementTarget) ||
                IsWithinPopupElement(source, popup.Child)) continue;
            popup.IsOpen = false;
        }
    }

    private static bool IsWithinPopupElement(DependencyObject? source, DependencyObject? target)
    {
        for (var current = source; current is not null; current = GetTreeParent(current))
            if (current == target) return true;
        return false;
    }

    private void CloseToolPopups()
    {
        GifSpeedPopup.IsOpen = false;
        GroupBorderOptionsPopup.IsOpen = false;
        GroupOptionsPopup.IsOpen = false;
        foreach (var popup in ToolPopups) popup.IsOpen = false;
    }

    private void ToggleToolPopup(Popup popup)
    {
        if (popup.IsOpen)
        {
            popup.IsOpen = false;
            return;
        }
        CloseToolPopups();
        PositionToolPopup(popup);
        HideEraserCursor();
        popup.IsOpen = true;
    }

    private void PositionToolPopup(Popup popup)
    {
        foreach (var arrow in PopupPointers(popup)) arrow.RenderTransform = System.Windows.Media.Transform.Identity;
        var target = (FrameworkElement)popup.PlacementTarget;
        var palette = popup == TextMorePopup ? TextPalette : DrawingPalette;
        popup.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var below = popup == TextMorePopup;
        if (below && BoardSurface.ActualHeight > 0)
        {
            var bottom = palette.TranslatePoint(new Point(0, palette.ActualHeight), BoardSurface).Y;
            below = bottom + popup.Child.DesiredSize.Height + 6 <= BoardSurface.ActualHeight;
        }
        if (popup == TextMorePopup)
        {
            TextMoreArrowUp.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
            TextMoreArrowDown.Visibility = below ? Visibility.Collapsed : Visibility.Visible;
            popup.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        var targetTop = target.TranslatePoint(new Point(), palette).Y;
        var targetBottom = targetTop + target.ActualHeight;
        popup.Placement = below ? PlacementMode.Bottom : PlacementMode.Top;
        popup.HorizontalOffset = (target.ActualWidth - popup.Child.DesiredSize.Width) / 2;
        // Clear the entire toolbar, not just the inset button. The popup's outer
        // padding leaves another six pixels around the fully visible pointer tip.
        popup.VerticalOffset = below
            ? Math.Max(0, palette.ActualHeight - targetBottom) + 6
            : -(Math.Max(0, targetTop) + 6);
    }

    private void RefreshOpenToolPopups()
    {
        foreach (var popup in ToolPopups.Where(p => p.IsOpen))
        {
            PositionToolPopup(popup);
            PopupTransitions.Reposition(popup);
            AlignPopupPointer(popup);
        }
    }

    private IEnumerable<FrameworkElement> PopupPointers(Popup popup) => popup == TextMorePopup
        ? new FrameworkElement[] { TextMoreArrowUp, TextMoreArrowDown }
        : new FrameworkElement[] { popup == DrawingShapesPopup ? DrawingShapesArrow
            : popup == DrawingEraserPopup ? DrawingEraserArrow : DrawingSettingsArrow };

    private void AlignPopupPointer(Popup popup)
    {
        popup.Child.UpdateLayout();
        var target = (FrameworkElement)popup.PlacementTarget;
        foreach (var arrow in PopupPointers(popup).Where(x => x.Visibility == Visibility.Visible))
        {
            if (PresentationSource.FromVisual(arrow) is null) continue;
            var point = arrow.PointFromScreen(target.PointToScreen(new Point(target.ActualWidth / 2, 0)));
            var child = (FrameworkElement)popup.Child;
            var half = Math.Max(0, (child.ActualWidth - arrow.ActualWidth) / 2 - 18);
            arrow.RenderTransform = new System.Windows.Media.TranslateTransform(Math.Clamp(point.X - arrow.ActualWidth / 2, -half, half), 0);
        }
    }
}
