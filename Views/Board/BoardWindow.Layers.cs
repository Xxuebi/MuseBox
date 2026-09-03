using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private readonly HashSet<string> _expandedLayerGroups = new(StringComparer.Ordinal);
    private readonly List<LayerRow> _layerRows = new();
    private bool _layersInitialized;
    private Point _layerPointerDown;
    private LayerRow? _layerDragRow;
    private LayerDropTarget? _layerDropTarget;
    private string? _layerHoverGroupId;
    private DateTime _layerHoverStarted;
    private bool _layerRenameBusy;
    private (string Id, bool IsGroup)? _layerSelectionAnchor;
    private bool _layerDirectSelectionActive;
    private double? _layerPanelWidth;

    private void OnLayersToggleChanged(object sender, RoutedEventArgs e)
    {
        SetLayersPanelVisible(LayersButton.IsChecked == true);
    }

    private void OnLayersCloseClick(object sender, RoutedEventArgs e) => LayersButton.IsChecked = false;

    private void SetLayersPanelVisible(bool visible)
    {
        UpdateLayersPanelWidth();
        var easing = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn };
        if (visible)
        {
            RefreshLayersPanel();
            LayersPanel.Visibility = Visibility.Visible;
            LayersPanel.IsHitTestVisible = true;
            var fadeIn = new DoubleAnimation(LayersPanel.Opacity, 1, TimeSpan.FromMilliseconds(170))
                { EasingFunction = easing };
            fadeIn.Completed += (_, _) =>
            {
                if (LayersButton.IsChecked != true) return;
                LayersPanel.BeginAnimation(OpacityProperty, null);
                LayersPanel.Opacity = 1;
                LayersPanelTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                LayersPanelTranslate.X = 0;
            };
            LayersPanel.BeginAnimation(OpacityProperty, fadeIn);
            LayersPanelTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(Math.Max(LayersPanelTranslate.X, LayersPanel.Width + 20), 0,
                    TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
            return;
        }
        LayersPanel.IsHitTestVisible = false;
        var opacity = new DoubleAnimation(LayersPanel.Opacity, 0, TimeSpan.FromMilliseconds(145)) { EasingFunction = easing };
        opacity.Completed += (_, _) =>
        {
            if (LayersButton.IsChecked == true) return;
            LayersPanel.Visibility = Visibility.Collapsed;
            LayersPanelTranslate.X = LayersPanel.Width + 20;
        };
        LayersPanel.BeginAnimation(OpacityProperty, opacity);
        LayersPanelTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(LayersPanelTranslate.X, LayersPanel.Width + 20,
                TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
    }

    private void UpdateLayersPanelWidth()
    {
        if (!IsInitialized) return;
        var available = Math.Max(180, Math.Floor(BoardSurface.ActualWidth - 24));
        var minimum = Math.Min(260, available);
        var maximum = Math.Max(minimum, Math.Min(720, Math.Floor(BoardSurface.ActualWidth * .72)));
        var automatic = Math.Clamp(Math.Round(BoardSurface.ActualWidth * .42), minimum, Math.Min(320, maximum));
        LayersPanel.Width = Math.Round(Math.Clamp(_layerPanelWidth ?? automatic, minimum, maximum));
    }

    private void OnLayersResizeDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        _layerPanelWidth = LayersPanel.Width - e.HorizontalChange;
        UpdateLayersPanelWidth();
    }

    private void OnLayersResizeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _layerPanelWidth = null;
        UpdateLayersPanelWidth();
        e.Handled = true;
    }

    private void OnLayersPanelMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private bool IsPointerInsideLayersPanel(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, LayersPanel)) return true;
            source = GetTreeParent(source);
        }
        return false;
    }

    private void OnLayersListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroll = FindVisualChild<ScrollViewer>(LayersList);
        if (scroll is null) return;
        if (e.Delta > 0) scroll.LineUp();
        else scroll.LineDown();
        e.Handled = true;
    }

    private void RefreshLayersPanel()
    {
        if (LayersList is null) return;
        BoardLayerNameService.EnsureNames(AllElements, _groups);
        var tree = BoardLayerTreeService.BuildTree(_groups, AllElements);
        if (!_layersInitialized)
        {
            foreach (var group in _groups) _expandedLayerGroups.Add(group.Id);
            _layersInitialized = true;
        }
        var editing = _layerRows.FirstOrDefault(row => row.IsEditing);
        _layerRows.Clear();
        void Append(IEnumerable<BoardLayerTreeService.Node> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                var row = LayerRow.From(node, depth, _expandedLayerGroups.Contains(node.Id),
                    node.IsGroup ? _explicitSelectedGroupId == node.Id : _selected.Contains(node.Id));
                if (editing is not null && editing.Id == row.Id && editing.IsGroup == row.IsGroup)
                {
                    row.IsEditing = true;
                    row.EditText = editing.EditText;
                }
                _layerRows.Add(row);
                if (node.IsGroup && row.IsExpanded) Append(node.Children, depth + 1);
            }
        }
        Append(tree, 0);
        LayersList.ItemsSource = null;
        LayersList.ItemsSource = _layerRows;
        UpdateLayerSelectionShapes();
        LayersEmptyText.Visibility = _layerRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncLayerSelection()
    {
        foreach (var row in _layerRows)
            row.IsSelected = row.IsGroup ? _explicitSelectedGroupId == row.Id : _selected.Contains(row.Id);
        UpdateLayerSelectionShapes();
    }

    private void UpdateLayerSelectionShapes()
    {
        for (var index = 0; index < _layerRows.Count; index++)
        {
            var row = _layerRows[index];
            var above = row.IsSelected && index > 0 && _layerRows[index - 1].IsSelected;
            var below = row.IsSelected && index + 1 < _layerRows.Count && _layerRows[index + 1].IsSelected;
            row.SelectionCornerRadius = new CornerRadius(above ? 0 : 7, above ? 0 : 7,
                below ? 0 : 7, below ? 0 : 7);
            row.SelectionChromeMargin = new Thickness(0, above ? 0 : 1, 0, below ? 0 : 1);
        }
    }

    private void OnLayerExpandClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LayerRow row || !row.IsGroup) return;
        if (!_expandedLayerGroups.Add(row.Id)) _expandedLayerGroups.Remove(row.Id);
        RefreshLayersPanel();
        e.Handled = true;
    }

    private void OnLayerListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        var row = LayerRowFromSource(e.OriginalSource as DependencyObject);
        if (row is null) return;
        LayersList.SelectedItem = row;
        _layerPointerDown = e.GetPosition(LayersList);
        _layerDragRow = row;
        var modifiers = Keyboard.Modifiers;
        SelectLayerRow(row, e.ClickCount >= 2, modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void SelectLayerRow(LayerRow row, bool doubleClick, bool control, bool shift)
    {
        if (doubleClick && !row.IsGroup && AllElements.FirstOrDefault(candidate => candidate.Id == row.Id) is { } focusedElement)
        {
            _layerDirectSelectionActive = true;
            _individualGroupSelectionId = focusedElement.Id;
            _explicitSelectedGroupId = null;
            ToggleElementFocus(focusedElement, preserveZoom: true);
            SyncLayerSelection();
            return;
        }

        if (shift)
        {
            var anchorIndex = _layerSelectionAnchor is { } anchor
                ? _layerRows.FindIndex(candidate => candidate.Id == anchor.Id && candidate.IsGroup == anchor.IsGroup)
                : -1;
            var rowIndex = _layerRows.IndexOf(row);
            if (anchorIndex < 0) anchorIndex = rowIndex;
            if (!control) _selected.Clear();
            for (var index = Math.Min(anchorIndex, rowIndex); index <= Math.Max(anchorIndex, rowIndex); index++)
                _selected.UnionWith(LayerRowElementIds(_layerRows[index]));
            _explicitSelectedGroupId = null;
            _individualGroupSelectionId = _selected.Count == 1 ? _selected.Single() : null;
            _layerDirectSelectionActive = true;
            UpdateSelectionVisuals();
            SyncLayerSelection();
            return;
        }

        _layerSelectionAnchor = (row.Id, row.IsGroup);
        if (row.IsGroup)
        {
            var ids = GroupMembers(row.Id).Select(element => element.Id).ToArray();
            if (control && ids.All(_selected.Contains)) _selected.ExceptWith(ids);
            else
            {
                if (!control) _selected.Clear();
                _selected.UnionWith(ids);
            }
            _explicitSelectedGroupId = _selected.Overlaps(ids) ? row.Id : null;
            _individualGroupSelectionId = null;
            // The layer panel is an explicit hierarchy editor. Selecting an inner
            // group here must not be promoted to an outer locked ancestor.
            _layerDirectSelectionActive = true;
        }
        else
        {
            var element = AllElements.FirstOrDefault(candidate => candidate.Id == row.Id);
            if (element is null) return;
            var ids = new[] { element.Id };
            if (control && _selected.Contains(element.Id)) _selected.Remove(element.Id);
            else
            {
                if (!control) _selected.Clear();
                _selected.Add(element.Id);
            }
            _explicitSelectedGroupId = null;
            _individualGroupSelectionId = _selected.Count == 1 ? _selected.Single() : null;
            _layerDirectSelectionActive = true;
        }
        UpdateSelectionVisuals();
        SyncLayerSelection();
        if (doubleClick && row.IsGroup) ToggleGroupFocus(row.Id);
    }

    private IEnumerable<string> LayerRowElementIds(LayerRow row) => row.IsGroup
        ? GroupMembers(row.Id).Select(element => element.Id)
        : new[] { row.Id };

    private void OnLayerListMouseMove(object sender, MouseEventArgs e)
    {
        if (_layerDragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(LayersList);
        if (Math.Abs(point.X - _layerPointerDown.X) < 5 && Math.Abs(point.Y - _layerPointerDown.Y) < 5) return;
        var source = LockedDragSource(_layerDragRow);
        _layerDragRow = null;
        DragDrop.DoDragDrop(LayersList, new DataObject(typeof(LayerRow), source), DragDropEffects.Move);
    }

    private void OnLayerListMouseUp(object sender, MouseButtonEventArgs e) => _layerDragRow = null;

    private LayerRow LockedDragSource(LayerRow source)
    {
        if (source.IsGroup) return source;
        var element = AllElements.FirstOrDefault(candidate => candidate.Id == source.Id);
        if (element is null || _layerDirectSelectionActive || _individualGroupSelectionId == element.Id) return source;
        var locked = BoardLayerTreeService.OutermostLockedAncestor(element, _groups);
        return locked is null ? source : _layerRows.FirstOrDefault(row => row.IsGroup && row.Id == locked) ?? source;
    }

    private void OnLayerListDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LayerRow)) || e.Data.GetData(typeof(LayerRow)) is not LayerRow source)
        { e.Effects = DragDropEffects.None; return; }
        var target = LayerRowFromSource(e.OriginalSource as DependencyObject);
        ClearLayerDropMarkers();
        if (target is null || target.Id == source.Id && target.IsGroup == source.IsGroup)
        { e.Effects = DragDropEffects.None; return; }
        var position = e.GetPosition(LayersList);
        var rowElement = FindRowElement(e.OriginalSource as DependencyObject);
        var local = rowElement is null ? new Point(0, 0) : e.GetPosition(rowElement);
        var centerDrop = target.IsGroup && local.Y > 9 && local.Y < 25 && position.X >= 24 + target.Depth * 18;
        string parent;
        string? before;
        if (position.X < 24)
        {
            var root = RootRow(target);
            parent = string.Empty;
            before = root.Id;
            target = root;
        }
        else if (centerDrop)
        {
            parent = target.Id;
            before = null;
            target.IsDropTarget = true;
            HoverExpand(target);
        }
        else
        {
            parent = target.ParentGroupId;
            if (local.Y < 17) { before = target.Id; target.DropBefore = true; }
            else { before = SiblingAfter(target); target.DropAfter = true; }
            _layerHoverGroupId = null;
        }
        _layerDropTarget = new LayerDropTarget(parent, before, source, target);
        AutoScrollLayers(position.Y);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private async void OnLayerListDrop(object sender, DragEventArgs e)
    {
        var drop = _layerDropTarget;
        ClearLayerDropMarkers();
        if (drop is null) return;
        var before = Snapshot();
        if (!BoardLayerTreeService.MoveNode(_groups, AllElements.ToArray(), drop.Source.Id, drop.Source.IsGroup,
                drop.ParentGroupId, drop.BeforeNodeId))
        {
            BoardStatus.Text = "无法移动到该位置：组合不能包含自身，且最多嵌套 32 层";
            return;
        }
        PushUndoSnapshot(before);
        await PersistLayerTreeAsync();
        if (drop.Source.IsGroup)
        {
            _explicitSelectedGroupId = drop.Source.Id;
            _selected.Clear();
            _selected.UnionWith(GroupMembers(drop.Source.Id).Select(element => element.Id));
        }
        RenderItems();
        UpdateSelectionVisuals();
        BoardStatus.Text = drop.ParentGroupId.Length == 0 ? "已调整图层顺序" : "已移动到组合中";
        e.Handled = true;
    }

    private void OnLayerListDragLeave(object sender, DragEventArgs e)
    {
        if (!LayersList.IsMouseOver) ClearLayerDropMarkers();
    }

    private void ClearLayerDropMarkers()
    {
        foreach (var row in _layerRows) { row.IsDropTarget = false; row.DropBefore = false; row.DropAfter = false; }
        _layerDropTarget = null;
    }

    private void HoverExpand(LayerRow row)
    {
        if (row.IsExpanded) return;
        if (_layerHoverGroupId != row.Id)
        {
            _layerHoverGroupId = row.Id;
            _layerHoverStarted = DateTime.UtcNow;
            return;
        }
        if (DateTime.UtcNow - _layerHoverStarted < TimeSpan.FromMilliseconds(600)) return;
        _expandedLayerGroups.Add(row.Id);
        RefreshLayersPanel();
    }

    private void AutoScrollLayers(double y)
    {
        var scroll = FindVisualChild<ScrollViewer>(LayersList);
        if (scroll is null) return;
        if (y < 28) scroll.LineUp();
        else if (y > LayersList.ActualHeight - 28) scroll.LineDown();
    }

    private string? SiblingAfter(LayerRow target)
    {
        var siblings = _layerRows.Where(row => row.Depth == target.Depth && row.ParentGroupId == target.ParentGroupId).ToArray();
        var index = Array.FindIndex(siblings, row => row.Id == target.Id && row.IsGroup == target.IsGroup);
        return index >= 0 && index + 1 < siblings.Length ? siblings[index + 1].Id : null;
    }

    private LayerRow RootRow(LayerRow row)
    {
        while (row.ParentGroupId.Length > 0)
        {
            var parent = _layerRows.FirstOrDefault(candidate => candidate.IsGroup && candidate.Id == row.ParentGroupId);
            if (parent is null) break;
            row = parent;
        }
        return row;
    }

    private void OnLayerListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && LayersList.SelectedItem is LayerRow row)
        { BeginLayerRename(row); e.Handled = true; }
        else if (e.Key == Key.Escape && !_layerRows.Any(row => row.IsEditing))
        { LayersButton.IsChecked = false; e.Handled = true; }
    }

    private void OnLayerListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = LayerRowFromSource(e.OriginalSource as DependencyObject);
        if (row is null) return;
        LayersList.SelectedItem = row;
        if (!row.IsSelected) SelectLayerRow(row, doubleClick: false, control: false, shift: false);
    }

    private void OnRenameLayerMenuClick(object sender, RoutedEventArgs e)
    {
        if (LayersList.SelectedItem is LayerRow row) BeginLayerRename(row);
    }

    private void BeginLayerRename(LayerRow row)
    {
        foreach (var candidate in _layerRows) candidate.IsEditing = false;
        row.EditText = row.DisplayName;
        row.IsEditing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (LayersList.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return;
            var editor = FindVisualChild<TextBox>(container);
            editor?.Focus();
            editor?.SelectAll();
        }));
    }

    private async void OnLayerNameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LayerRow row) return;
        if (e.Key == Key.Escape) { row.IsEditing = false; LayersList.Focus(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        await CommitLayerRenameAsync(row);
        e.Handled = true;
    }

    private async void OnLayerNameEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        await Task.CompletedTask;
    }

    private async void OnLayerRenameConfirmClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LayerRow row)
            await CommitLayerRenameAsync(row);
        e.Handled = true;
    }

    private void OnLayerRenameCancelClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LayerRow row)
        {
            row.EditText = row.DisplayName;
            row.IsEditing = false;
            LayersList.Focus();
        }
        e.Handled = true;
    }

    private async Task CommitLayerRenameAsync(LayerRow row)
    {
        if (_layerRenameBusy || !row.IsEditing) return;
        _layerRenameBusy = true;
        try
        {
            var value = BoardLayerNameService.Normalize(row.EditText);
            var before = Snapshot();
            if (row.IsGroup)
            {
                var group = _groups.FirstOrDefault(candidate => candidate.Id == row.Id);
                if (group is null) return;
                if (value.Length == 0) value = NextGroupName();
                if (group.LayerName == value) { row.IsEditing = false; return; }
                group.LayerName = value;
            }
            else
            {
                var element = AllElements.FirstOrDefault(candidate => candidate.Id == row.Id);
                if (element is null) return;
                if (value.Length == 0) value = BoardLayerNameService.DefaultName(element);
                if (element.LayerName == value) { row.IsEditing = false; return; }
                element.LayerName = value;
            }
            PushUndoSnapshot(before);
            await PersistLayerTreeAsync();
            row.DisplayName = value;
            row.IsEditing = false;
            BoardStatus.Text = "图层名称已更新";
        }
        finally { _layerRenameBusy = false; }
    }

    private static LayerRow? LayerRowFromSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: LayerRow row }) return row;
            source = GetTreeParent(source);
        }
        return null;
    }

    private static FrameworkElement? FindRowElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Grid { DataContext: LayerRow }) return (FrameworkElement)source;
            source = GetTreeParent(source);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private sealed record LayerDropTarget(string ParentGroupId, string? BeforeNodeId, LayerRow Source, LayerRow Target);

    private sealed class LayerRow : INotifyPropertyChanged
    {
        private bool _selected;
        private bool _editing;
        private bool _dropTarget;
        private bool _dropBefore;
        private bool _dropAfter;
        private string _displayName = string.Empty;
        private CornerRadius _selectionCornerRadius = new(7);
        private Thickness _selectionChromeMargin = new(0, 1, 0, 1);
        public string Id { get; init; } = string.Empty;
        public bool IsGroup { get; init; }
        public string ParentGroupId { get; init; } = string.Empty;
        public int Depth { get; init; }
        public Thickness Indent => new(Depth * 18, 0, 0, 0);
        public bool IsExpanded { get; init; }
        public string IconGlyph { get; init; } = string.Empty;
        public string IconFontFamily { get; init; } = "Segoe MDL2 Assets";
        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
        public string EditText { get; set; } = string.Empty;
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
        public bool IsEditing { get => _editing; set => Set(ref _editing, value); }
        public bool IsDropTarget { get => _dropTarget; set => Set(ref _dropTarget, value); }
        public bool DropBefore { get => _dropBefore; set => Set(ref _dropBefore, value); }
        public bool DropAfter { get => _dropAfter; set => Set(ref _dropAfter, value); }
        public CornerRadius SelectionCornerRadius { get => _selectionCornerRadius; set => Set(ref _selectionCornerRadius, value); }
        public Thickness SelectionChromeMargin { get => _selectionChromeMargin; set => Set(ref _selectionChromeMargin, value); }

        public static LayerRow From(BoardLayerTreeService.Node node, int depth, bool expanded, bool selected)
        {
            var element = node.Element;
            return new LayerRow
            {
                Id = node.Id, IsGroup = node.IsGroup, ParentGroupId = node.ParentGroupId, Depth = depth,
                IsExpanded = expanded, IsSelected = selected,
                DisplayName = node.IsGroup ? node.Group!.LayerName : element!.LayerName,
                IconGlyph = node.IsGroup ? "\uE8EF" : element switch
                {
                    BoardItem => "\uEB9F", BoardTextItem => "T", BoardDrawingItem => "\uE70F", _ => "\uE8A5"
                },
                IconFontFamily = element is BoardTextItem ? "Segoe UI" : "Segoe MDL2 Assets"
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
