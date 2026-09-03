using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private const double GroupRemovalDistance = 28;
    private readonly Dictionary<string, Border> _groupVisuals = new();
    private string? _pendingMembershipGroupId;
    private string? _pendingRemovalGroupId;
    private string? _individualGroupSelectionId;
    private string? _explicitSelectedGroupId;
    private string? _dragMembershipSourceGroupId;
    private Rect _dragMembershipSourceBounds = Rect.Empty;
    private bool _syncingGroupBorder;
    private BoardSnapshot? _groupBorderEditSnapshot;
    private string? _groupBorderEditGroupId;
    private double _groupBorderEditOriginal;
    private double _groupPaddingEditOriginal;
    private string? _focusedImageId;
    private double _focusReturnZoom;
    private double _focusReturnPanX;
    private double _focusReturnPanY;

    private IEnumerable<BoardElement> ImageSelectionUnit(BoardElement item)
    {
        if (item.Id == _individualGroupSelectionId) return new[] { item };
        if (DirectlySelectedGroupContaining(item) is { } selectedGroup)
            return GroupMembers(selectedGroup.Id);
        var locked = BoardLayerTreeService.OutermostLockedAncestor(item, _groups);
        return locked is null ? new[] { item } : GroupMembers(locked);
    }

    private BoardGroup? DirectlySelectedGroupContaining(BoardElement item)
    {
        if (!_layerDirectSelectionActive || _explicitSelectedGroupId is not { } groupId) return null;
        var group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null) return null;
        var members = GroupMembers(groupId);
        return members.Any(member => member.Id == item.Id) && members.All(member => _selected.Contains(member.Id))
            ? group : null;
    }

    private bool IsExplicitGroupSelection(string groupId) =>
        _explicitSelectedGroupId == groupId &&
        GroupMembers(groupId) is { Length: > 0 } members && members.All(member => _selected.Contains(member.Id));

    private bool IsDirectGroupSelection(string groupId) =>
        _layerDirectSelectionActive && IsExplicitGroupSelection(groupId);

    private BoardGroup? NextNestedGroupFor(BoardElement item)
    {
        if (item.GroupId.Length == 0) return null;
        var byId = _groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var path = new List<BoardGroup>();
        var currentId = item.GroupId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (currentId.Length > 0 && visited.Add(currentId) && byId.TryGetValue(currentId, out var current))
        {
            path.Add(current);
            currentId = current.ParentGroupId;
        }
        path.Reverse();
        if (path.Count < 2) return null;
        if (_explicitSelectedGroupId is { } selectedId && IsExplicitGroupSelection(selectedId))
        {
            var selectedIndex = path.FindIndex(group => group.Id == selectedId);
            if (selectedIndex >= 0 && selectedIndex + 1 < path.Count) return path[selectedIndex + 1];
            if (selectedIndex == path.Count - 1) return null;
        }
        return path[1];
    }

    private void SelectNestedGroupDirectly(string groupId)
    {
        _individualGroupSelectionId = null;
        _explicitSelectedGroupId = groupId;
        _layerDirectSelectionActive = true;
        _selected.Clear();
        _selected.UnionWith(GroupMembers(groupId).Select(element => element.Id));
        UpdateSelectionVisuals();
    }

    private bool IsImageGroupLocked(string groupId) =>
        _groups.FirstOrDefault(group => group.Id == groupId)?.Locked ?? true;

    private void ExpandSelectedImageGroups()
    {
        if (_layerDirectSelectionActive) return;
        var groups = AllElements.Where(element => _selected.Contains(element.Id) && element.Id != _individualGroupSelectionId)
            .Select(element => BoardLayerTreeService.OutermostLockedAncestor(element, _groups))
            .Where(groupId => groupId is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var groupId in groups)
            foreach (var element in GroupMembers(groupId)) _selected.Add(element.Id);
    }

    private bool CanGroupImages()
    {
        return SelectedLayerNodes().Count >= 2;
    }

    private async void OnGroupImagesClick(object sender, RoutedEventArgs e) => await GroupImagesAsync();
    private async void OnUngroupImagesClick(object sender, RoutedEventArgs e) => await UngroupImagesAsync();

    private async Task GroupImagesAsync()
    {
        await FlushPendingDrawingAsync();
        ExpandSelectedImageGroups();
        if (!CanGroupImages()) return;
        PushUndoSnapshot();
        var nodes = SelectedLayerNodes();
        if (nodes.Count < 2) return;
        var elements = AllElements.Where(x => _selected.Contains(x.Id)).ToArray();
        var id = Guid.NewGuid().ToString("N");
        _individualGroupSelectionId = null;
        var parents = nodes.Select(node => node.IsGroup
            ? _groups.Single(group => group.Id == node.Id).ParentGroupId
            : AllElements.Single(element => element.Id == node.Id).GroupId).Distinct().ToArray();
        var group = new BoardGroup
        {
            Id = id, DrawerId = _drawerId, ParentGroupId = parents.Length == 1 ? parents[0] : string.Empty,
            LayerName = NextGroupName()
        };
        _groups.Add(group);
        _expandedLayerGroups.Add(id);
        foreach (var node in nodes)
            if (node.IsGroup) _groups.Single(existing => existing.Id == node.Id).ParentGroupId = id;
            else AllElements.Single(element => element.Id == node.Id).GroupId = id;
        BoardLayerTreeService.NormalizeZIndices(_groups, AllElements);
        _explicitSelectedGroupId = id;
        await PersistLayerTreeAsync();
        RenderGroupBackgrounds();
        UpdateSelectionVisuals();
        RefreshLayersPanel();
        BoardStatus.Text = $"已将 {elements.Length} 个元素组合";
    }

    private async Task UngroupImagesAsync()
    {
        await FlushPendingDrawingAsync();
        ExpandSelectedImageGroups();
        var group = SelectedGroup();
        if (group is null) return;
        _individualGroupSelectionId = null;
        PushUndoSnapshot();
        foreach (var child in _groups.Where(child => child.ParentGroupId == group.Id)) child.ParentGroupId = group.ParentGroupId;
        foreach (var element in AllElements.Where(element => element.GroupId == group.Id)) element.GroupId = group.ParentGroupId;
        _groups.Remove(group);
        _explicitSelectedGroupId = null;
        BoardLayerTreeService.NormalizeZIndices(_groups, AllElements);
        await PersistLayerTreeAsync();
        RenderGroupBackgrounds();
        UpdateSelectionVisuals();
        RefreshLayersPanel();
        BoardStatus.Text = "已解散组合，元素位置保持不变";
    }

    private static void ResetGroupPresentation(BoardElement element)
    {
        element.GroupBackgroundColor = "#52FFFFFF";
        element.GroupBorderColor = "#807A7A7A";
        element.GroupBorderThickness = 1.2;
        element.GroupFramePadding = 14;
        element.GroupBackgroundVisible = true;
        element.GroupLocked = true;
        element.GroupAutoMembership = false;
    }

    private static void CopyGroupPresentation(BoardElement source, BoardElement target)
    {
        target.GroupBackgroundColor = source.GroupBackgroundColor;
        target.GroupBorderColor = source.GroupBorderColor;
        target.GroupBorderThickness = source.GroupBorderThickness;
        target.GroupFramePadding = source.GroupFramePadding;
        target.GroupBackgroundVisible = source.GroupBackgroundVisible;
        target.GroupLocked = source.GroupLocked;
        target.GroupAutoMembership = source.GroupAutoMembership;
    }

    private BoardElement[] GroupMembers(string groupId) =>
        BoardLayerTreeService.DescendantElements(groupId, _groups, AllElements).ToArray();

    private BoardGroup? SelectedGroup()
    {
        if (_explicitSelectedGroupId is { } explicitId)
        {
            var explicitGroup = _groups.FirstOrDefault(group => group.Id == explicitId);
            if (explicitGroup is not null && GroupMembers(explicitId).All(element => _selected.Contains(element.Id)))
                return explicitGroup;
        }
        var selected = AllElements.Where(element => _selected.Contains(element.Id)).Select(element => element.Id).ToHashSet();
        return _groups.Select(group => (Group: group, Members: GroupMembers(group.Id)))
            .Where(candidate => candidate.Members.Length == selected.Count && candidate.Members.All(element => selected.Contains(element.Id)))
            .OrderByDescending(candidate => GroupDepth(candidate.Group.Id)).Select(candidate => candidate.Group).FirstOrDefault();
    }

    private List<(string Id, bool IsGroup)> SelectedLayerNodes()
    {
        var selected = AllElements.Where(element => _selected.Contains(element.Id)).Select(element => element.Id).ToHashSet();
        var fullGroups = _groups.Where(group =>
        {
            var members = GroupMembers(group.Id);
            return members.Length > 0 && members.All(element => selected.Contains(element.Id));
        }).ToArray();
        var roots = fullGroups.Where(group => !fullGroups.Any(parent => IsGroupAncestor(parent.Id, group.Id))).ToArray();
        var covered = roots.SelectMany(group => GroupMembers(group.Id)).Select(element => element.Id).ToHashSet();
        return roots.Select(group => (group.Id, true))
            .Concat(AllElements.Where(element => selected.Contains(element.Id) && !covered.Contains(element.Id))
                .Select(element => (element.Id, false))).ToList();
    }

    private bool IsGroupAncestor(string ancestorId, string groupId)
    {
        var current = _groups.FirstOrDefault(group => group.Id == groupId);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && current.ParentGroupId.Length > 0 && visited.Add(current.Id))
        {
            if (current.ParentGroupId == ancestorId) return true;
            current = _groups.FirstOrDefault(group => group.Id == current.ParentGroupId);
        }
        return false;
    }

    private int GroupDepth(string groupId)
    {
        var depth = 0;
        var current = _groups.FirstOrDefault(group => group.Id == groupId);
        while (current is not null && current.ParentGroupId.Length > 0)
        {
            depth++;
            current = _groups.FirstOrDefault(group => group.Id == current.ParentGroupId);
        }
        return depth;
    }

    private string RootGroupId(string groupId)
    {
        var current = _groups.FirstOrDefault(group => group.Id == groupId);
        while (current is not null && current.ParentGroupId.Length > 0)
            current = _groups.FirstOrDefault(group => group.Id == current.ParentGroupId);
        return current?.Id ?? groupId;
    }

    private string NextGroupName()
    {
        var used = _groups.Select(group => group.LayerName).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var index = 1; ; index++) if (!used.Contains($"组合 {index}")) return $"组合 {index}";
    }

    private Task PersistLayerTreeAsync() =>
        _repository.ApplyLayerTreeAsync(_drawerId, _groups, AllElements.ToArray());

    private Rect GroupBounds(string groupId, IReadOnlySet<string>? excluded = null, bool padded = true)
    {
        var group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null) return Rect.Empty;
        var bounds = AllElements.Where(element => element.GroupId == groupId && (excluded is null || !excluded.Contains(element.Id)))
            .Select(RotatedImageBounds).Aggregate(Rect.Empty, (current, next) =>
        {
            current.Union(next);
            return current;
        });
        foreach (var child in _groups.Where(child => child.ParentGroupId == groupId))
        {
            var childBounds = GroupBounds(child.Id, excluded);
            if (!childBounds.IsEmpty) bounds.Union(childBounds);
        }
        if (bounds.IsEmpty) return Rect.Empty;
        if (padded)
        {
            var padding = Math.Clamp(group.FramePadding, 0, 10000);
            bounds.Inflate(padding, padding);
        }
        return bounds;
    }

    private void RenderGroupBackgrounds()
    {
        foreach (var visual in _groupVisuals.Values) WorldCanvas.Children.Remove(visual);
        _groupVisuals.Clear();
        foreach (var group in _groups.OrderBy(group => GroupDepth(group.Id))) AddGroupVisual(group);
    }

    private void AddGroupVisual(BoardGroup group)
    {
        var visual = new Border
        {
            Tag = new GroupBackgroundTag(group.Id),
            Background = ParseBrush(group.BackgroundColor, Brushes.Transparent),
            BorderBrush = ParseBrush(group.BorderColor, Brushes.Transparent),
            BorderThickness = new Thickness(Math.Clamp(group.BorderThickness, 0, 10000)),
            CornerRadius = new CornerRadius(9),
            Cursor = Cursors.SizeAll,
            Visibility = group.BackgroundVisible ? Visibility.Visible : Visibility.Collapsed
        };
        visual.PreviewMouseLeftButtonDown += (_, e) => OnGroupBackgroundMouseDown(group.Id, e);
        WorldCanvas.Children.Add(visual);
        _groupVisuals[group.Id] = visual;
        UpdateGroupVisual(group.Id);
    }

    private void UpdateGroupVisuals()
    {
        var current = _groups.Select(group => group.Id).ToHashSet();
        foreach (var stale in _groupVisuals.Keys.Where(x => !current.Contains(x)).ToArray())
        {
            WorldCanvas.Children.Remove(_groupVisuals[stale]);
            _groupVisuals.Remove(stale);
        }
        foreach (var groupId in current)
        {
            var group = _groups.Single(candidate => candidate.Id == groupId);
            if (!_groupVisuals.ContainsKey(groupId)) AddGroupVisual(group);
            else UpdateGroupVisual(groupId);
        }
    }

    private void UpdateGroupVisual(string groupId)
    {
        if (!_groupVisuals.TryGetValue(groupId, out var visual)) return;
        var members = GroupMembers(groupId);
        var group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (members.Length == 0 || group is null) return;
        var bounds = GroupBounds(groupId);
        visual.Width = Math.Max(1, bounds.Width);
        visual.Height = Math.Max(1, bounds.Height);
        Canvas.SetLeft(visual, bounds.Left);
        Canvas.SetTop(visual, bounds.Top);
        // Group chrome must never cover any board element. Child group backgrounds
        // remain above their parents, while every real element stays at Z >= 0.
        Panel.SetZIndex(visual, -BoardLayerTreeService.MaxDepth - 2 + GroupDepth(groupId));
        visual.Background = ParseBrush(group.BackgroundColor, Brushes.Transparent);
        visual.BorderBrush = ParseBrush(group.BorderColor, Brushes.Transparent);
        visual.BorderThickness = new Thickness(Math.Clamp(group.BorderThickness, 0, 10000));
        visual.Visibility = group.BackgroundVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAncestorGroupVisuals(string groupId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (groupId.Length > 0 && visited.Add(groupId))
        {
            UpdateGroupVisual(groupId);
            groupId = _groups.FirstOrDefault(group => group.Id == groupId)?.ParentGroupId ?? string.Empty;
        }
    }

    private void OnGroupBackgroundMouseDown(string groupId, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _spaceDown || _toolMode != BoardToolMode.Select) return;
        // A double click on a nested group''s own chrome is an explicit request for
        // that group, even when one of its ancestors is locked.
        _layerDirectSelectionActive = e.ClickCount >= 2 || IsDirectGroupSelection(groupId);
        _individualGroupSelectionId = null;
        _explicitSelectedGroupId = groupId;
        var ids = GroupMembers(groupId).Select(x => x.Id).ToArray();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && ids.All(_selected.Contains))
            _selected.ExceptWith(ids);
        else
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) _selected.Clear();
            _selected.UnionWith(ids);
        }
        UpdateSelectionVisuals();
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            return;
        }
        if (_selected.Overlaps(ids))
        {
            PrepareGroupMembershipDrag();
            _gestureSnapshot = Snapshot();
            _mouseStart = _lastMouse = e.GetPosition(BoardSurface);
            _draggingItems = true;
            _itemsDragMoved = false;
            BeginContinuousInteraction();
            Mouse.Capture(BoardSurface);
        }
        e.Handled = true;
    }

    private BoardElement[] SelectedGroupMembers()
    {
        var group = SelectedGroup();
        return group is null ? Array.Empty<BoardElement>() : GroupMembers(group.Id);
    }

    private void UpdateGroupToolbar()
    {
        var members = SelectedGroupMembers();
        if (members.Length == 0 || IsDrawingTool(_toolMode))
        {
            GroupPalette.Visibility = Visibility.Collapsed;
            GroupBorderOptionsPopup.IsOpen = false;
            GroupOptionsPopup.IsOpen = false;
            return;
        }
        var presentation = SelectedGroup()!;
        GroupPalette.Visibility = Visibility.Visible;
        GroupBackgroundColorPreview.Background = ParseBrush(presentation.BackgroundColor, Brushes.Transparent);
        GroupBorderColorPreview.Background = ParseBrush(presentation.BorderColor, Brushes.Transparent);
        _syncingGroupBorder = true;
        GroupBorderThicknessSlider.Value = Math.Clamp(presentation.BorderThickness,
            GroupBorderThicknessSlider.Minimum, GroupBorderThicknessSlider.Maximum);
        GroupBorderThicknessText.Text = FormatGroupBorderThickness(presentation.BorderThickness);
        GroupFramePaddingSlider.Value = Math.Clamp(presentation.FramePadding,
            GroupFramePaddingSlider.Minimum, GroupFramePaddingSlider.Maximum);
        GroupFramePaddingText.Text = FormatGroupBorderThickness(presentation.FramePadding);
        _syncingGroupBorder = false;
        GroupLockButton.ToolTip = "锁定后选中组合内任意元素将选择整个组，双击某一个元素可以临时选中单个元素";
        GroupLockButton.Background = presentation.Locked
            ? (Brush)FindResource("AccentSubtleBrush") : Brushes.Transparent;
        GroupLockIcon.Stroke = presentation.Locked
            ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextBrush");
        GroupBackgroundVisibilityButton.ToolTip = presentation.BackgroundVisible
            ? "关闭组合背景" : "显示组合背景";
        GroupBackgroundVisibilityButton.Background = presentation.BackgroundVisible
            ? Brushes.Transparent : (Brush)FindResource("AccentSubtleBrush");
        GroupBackgroundVisibilityIcon.Opacity = presentation.BackgroundVisible ? 1 : .5;
        SwitchAnimation.SetWithoutAnimation(GroupAutoMembershipToggle, presentation.AutoMembership);
        PositionGroupToolbar(presentation.Id);
    }

    private void PositionGroupToolbar(string groupId)
    {
        var bounds = GroupBounds(groupId);
        GroupPalette.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(GroupPalette.ActualWidth, GroupPalette.DesiredSize.Width);
        var height = Math.Max(GroupPalette.ActualHeight, GroupPalette.DesiredSize.Height);
        var center = (bounds.Left + bounds.Right) / 2 * _viewZoom + _viewPanX;
        var top = bounds.Top * _viewZoom + _viewPanY;
        var bottom = bounds.Bottom * _viewZoom + _viewPanY;
        var x = Math.Clamp(center - width / 2, 8, Math.Max(8, BoardSurface.ActualWidth - width - 8));
        var y = top - height - 10;
        if (y < 8) y = Math.Min(BoardSurface.ActualHeight - height - 8, bottom + 10);
        Canvas.SetLeft(GroupPalette, x);
        Canvas.SetTop(GroupPalette, Math.Max(8, y));
        if (GroupBorderOptionsPopup.IsOpen) PopupTransitions.Reposition(GroupBorderOptionsPopup);
        if (GroupOptionsPopup.IsOpen) PopupTransitions.Reposition(GroupOptionsPopup);
    }

    private async void OnGroupBackgroundColorClick(object sender, RoutedEventArgs e) =>
        await EditGroupColorAsync(background: true);

    private async void OnGroupBorderColorClick(object sender, RoutedEventArgs e) =>
        await EditGroupColorAsync(background: false);

    private void OnGroupBorderOptionsClick(object sender, RoutedEventArgs e)
    {
        if (GroupBorderOptionsPopup.IsOpen)
        {
            GroupBorderOptionsPopup.IsOpen = false;
            return;
        }
        CloseToolPopups();
        var members = SelectedGroupMembers();
        if (members.Length == 0) return;
        UpdateGroupToolbar();
        GroupBorderOptionsPopup.IsOpen = true;
    }

    private static string FormatGroupBorderThickness(double value) =>
        Math.Clamp(value, 0, 10000).ToString("0.#", CultureInfo.CurrentCulture);

    private static bool TryParseGroupMetric(string text, out double value)
    {
        var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return parsed && double.IsFinite(value);
    }

    private void BeginGroupBorderThicknessEdit()
    {
        if (_groupBorderEditSnapshot is not null) return;
        var group = SelectedGroup();
        if (group is null) return;
        _groupBorderEditSnapshot = Snapshot();
        _groupBorderEditGroupId = group.Id;
        _groupBorderEditOriginal = group.BorderThickness;
        _groupPaddingEditOriginal = group.FramePadding;
    }

    private void PreviewGroupBorderThickness(double value)
    {
        var groupId = _groupBorderEditGroupId ?? SelectedGroup()?.Id;
        if (string.IsNullOrEmpty(groupId)) return;
        if (!double.IsFinite(value)) return;
        value = Math.Clamp(value, 0, 10000);
        var group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null) return;
        group.BorderThickness = value;
        UpdateGroupVisual(groupId);
        _syncingGroupBorder = true;
        GroupBorderThicknessSlider.Value = Math.Clamp(value,
            GroupBorderThicknessSlider.Minimum, GroupBorderThicknessSlider.Maximum);
        GroupBorderThicknessText.Text = FormatGroupBorderThickness(value);
        _syncingGroupBorder = false;
    }

    private void PreviewGroupFramePadding(double value)
    {
        var groupId = _groupBorderEditGroupId ?? SelectedGroup()?.Id;
        if (string.IsNullOrEmpty(groupId) || !double.IsFinite(value)) return;
        value = Math.Clamp(value, 0, 10000);
        var group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null) return;
        group.FramePadding = value;
        UpdateGroupVisual(groupId);
        PositionGroupToolbar(groupId);
        _syncingGroupBorder = true;
        GroupFramePaddingSlider.Value = Math.Clamp(value,
            GroupFramePaddingSlider.Minimum, GroupFramePaddingSlider.Maximum);
        GroupFramePaddingText.Text = FormatGroupBorderThickness(value);
        _syncingGroupBorder = false;
    }

    private async Task CompleteGroupBorderThicknessEditAsync()
    {
        var before = _groupBorderEditSnapshot;
        var groupId = _groupBorderEditGroupId;
        _groupBorderEditSnapshot = null;
        _groupBorderEditGroupId = null;
        if (before is null || string.IsNullOrEmpty(groupId)) return;
        var group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null || (Math.Abs(group.BorderThickness - _groupBorderEditOriginal) < .0001 &&
             Math.Abs(group.FramePadding - _groupPaddingEditOriginal) < .0001)) return;
        PushUndoSnapshot(before);
        await PersistLayerTreeAsync();
        BoardStatus.Text = $"边框粗细 {FormatGroupBorderThickness(group.BorderThickness)} px · 背景框宽度 {FormatGroupBorderThickness(group.FramePadding)} px";
    }

    private void CancelGroupBorderThicknessEdit()
    {
        var groupId = _groupBorderEditGroupId;
        if (!string.IsNullOrEmpty(groupId))
        {
            var group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
            if (group is not null) { group.BorderThickness = _groupBorderEditOriginal; group.FramePadding = _groupPaddingEditOriginal; }
            UpdateGroupVisual(groupId);
        }
        _groupBorderEditSnapshot = null;
        _groupBorderEditGroupId = null;
        UpdateGroupToolbar();
    }

    private void OnGroupBorderThicknessEditStart(object sender, MouseButtonEventArgs e) =>
        BeginGroupBorderThicknessEdit();

    private async void OnGroupBorderThicknessEditComplete(object sender, MouseButtonEventArgs e) =>
        await CompleteGroupBorderThicknessEditAsync();

    private void OnGroupBorderThicknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingGroupBorder) return;
        BeginGroupBorderThicknessEdit();
        PreviewGroupBorderThickness(e.NewValue);
    }

    private void OnGroupBorderThicknessSliderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            BeginGroupBorderThicknessEdit();
    }

    private async void OnGroupBorderThicknessSliderKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            await CompleteGroupBorderThicknessEditAsync();
    }

    private void OnGroupBorderThicknessTextGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        BeginGroupBorderThicknessEdit();
        GroupBorderThicknessText.SelectAll();
    }

    private async void OnGroupBorderThicknessTextKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelGroupBorderThicknessEdit();
            BoardSurface.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        if (TryParseGroupMetric(GroupBorderThicknessText.Text, out var value))
            PreviewGroupBorderThickness(value);
        else UpdateGroupToolbar();
        await CompleteGroupBorderThicknessEditAsync();
        BoardSurface.Focus();
        e.Handled = true;
    }

    private async void OnGroupBorderThicknessTextLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_groupBorderEditSnapshot is null) return;
        if (TryParseGroupMetric(GroupBorderThicknessText.Text, out var value))
            PreviewGroupBorderThickness(value);
        else UpdateGroupToolbar();
        await CompleteGroupBorderThicknessEditAsync();
    }

    private void OnGroupFramePaddingEditStart(object sender, MouseButtonEventArgs e) =>
        BeginGroupBorderThicknessEdit();

    private async void OnGroupFramePaddingEditComplete(object sender, MouseButtonEventArgs e) =>
        await CompleteGroupBorderThicknessEditAsync();

    private void OnGroupFramePaddingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingGroupBorder) return;
        BeginGroupBorderThicknessEdit();
        PreviewGroupFramePadding(e.NewValue);
    }

    private void OnGroupFramePaddingSliderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            BeginGroupBorderThicknessEdit();
    }

    private async void OnGroupFramePaddingSliderKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            await CompleteGroupBorderThicknessEditAsync();
    }

    private void OnGroupFramePaddingTextGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        BeginGroupBorderThicknessEdit();
        GroupFramePaddingText.SelectAll();
    }

    private async void OnGroupFramePaddingTextKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelGroupBorderThicknessEdit();
            BoardSurface.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        if (TryParseGroupMetric(GroupFramePaddingText.Text, out var value))
            PreviewGroupFramePadding(value);
        else UpdateGroupToolbar();
        await CompleteGroupBorderThicknessEditAsync();
        BoardSurface.Focus();
        e.Handled = true;
    }

    private async void OnGroupFramePaddingTextLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_groupBorderEditSnapshot is null) return;
        if (TryParseGroupMetric(GroupFramePaddingText.Text, out var value))
            PreviewGroupFramePadding(value);
        else UpdateGroupToolbar();
        await CompleteGroupBorderThicknessEditAsync();
    }

    private async void OnGroupBorderOptionsClosed(object? sender, EventArgs e) =>
        await CompleteGroupBorderThicknessEditAsync();

    private async Task EditGroupColorAsync(bool background)
    {
        var group = SelectedGroup();
        if (group is null) return;
        var before = Snapshot();
        var original = background ? group.BackgroundColor : group.BorderColor;
        var picked = PickColor(original, color =>
        {
            if (background) group.BackgroundColor = color;
            else group.BorderColor = color;
            UpdateGroupVisual(group.Id);
            UpdateGroupToolbar();
        });
        if (picked is null)
        {
            if (background) group.BackgroundColor = original;
            else group.BorderColor = original;
            UpdateGroupVisual(group.Id);
            UpdateGroupToolbar();
            return;
        }
        PushUndoSnapshot(before);
        if (background) group.BackgroundColor = picked;
        else group.BorderColor = picked;
        await PersistLayerTreeAsync();
        UpdateGroupVisual(group.Id);
        BoardStatus.Text = background ? "组合背景颜色已更新" : "组合边框颜色已更新";
    }

    private async void OnGroupLockClick(object sender, RoutedEventArgs e)
    {
        var group = SelectedGroup();
        if (group is null) return;
        PushUndoSnapshot();
        var locked = group.Locked = !group.Locked;
        await PersistLayerTreeAsync();
        UpdateGroupToolbar();
        BoardStatus.Text = locked ? "组合已锁定，单击成员选择整个组合" : "组合已解锁，可单独选择成员";
    }

    private async void OnGroupBackgroundVisibilityClick(object sender, RoutedEventArgs e)
    {
        var group = SelectedGroup();
        if (group is null) return;
        PushUndoSnapshot();
        var visible = group.BackgroundVisible = !group.BackgroundVisible;
        await PersistLayerTreeAsync();
        UpdateGroupVisual(group.Id);
        UpdateGroupToolbar();
        BoardStatus.Text = visible ? "已显示组合背景" : "已关闭组合背景";
    }

    private void OnGroupOptionsClick(object sender, RoutedEventArgs e)
    {
        if (GroupOptionsPopup.IsOpen) GroupOptionsPopup.IsOpen = false;
        else
        {
            CloseToolPopups();
            var group = SelectedGroup();
            if (group is null) return;
            SwitchAnimation.SetWithoutAnimation(GroupAutoMembershipToggle, group.AutoMembership);
            GroupOptionsPopup.IsOpen = true;
        }
    }

    private async void OnGroupAutoMembershipClick(object sender, RoutedEventArgs e)
    {
        var group = SelectedGroup();
        if (group is null) return;
        PushUndoSnapshot();
        var enabled = GroupAutoMembershipToggle.IsChecked == true;
        group.AutoMembership = enabled;
        await PersistLayerTreeAsync();
        BoardStatus.Text = enabled ? "拖入组合范围的元素将在松手后加入" : "已关闭自动加入组合";
    }

    private void EvaluateGroupMembershipDrop()
    {
        _pendingMembershipGroupId = null;
        _pendingRemovalGroupId = null;
        var moving = AllElements.Where(x => _selected.Contains(x.Id)).ToArray();
        if (moving.Length == 0) { ClearGroupDropHint(); return; }
        var movingIds = moving.Select(x => x.Id).ToHashSet();
        var movingBounds = moving.Select(RotatedImageBounds).Aggregate(Rect.Empty, (bounds, next) =>
        { bounds.Union(next); return bounds; });
        var center = new Point((movingBounds.Left + movingBounds.Right) / 2,
            (movingBounds.Top + movingBounds.Bottom) / 2);
        var movingGroup = SelectedGroup();
        var target = _groups.Where(group => group.AutoMembership &&
                group.Id != movingGroup?.Id && (movingGroup is null || !IsGroupAncestor(movingGroup.Id, group.Id)))
            .Select(group => (Id: group.Id, Bounds: GroupBounds(group.Id, movingIds)))
            .Where(candidate => !candidate.Bounds.IsEmpty && candidate.Bounds.Contains(center))
            .OrderBy(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(target.Id))
        {
            _pendingMembershipGroupId = target.Id;
            ShowGroupDropHint("松手加入组合", movingBounds);
            return;
        }

        if (_dragMembershipSourceGroupId is { Length: > 0 } source &&
            moving.All(x => x.GroupId == source) && !_dragMembershipSourceBounds.IsEmpty)
        {
            var remaining = GroupMembers(source).Where(x => !movingIds.Contains(x.Id)).ToArray();
            if (remaining.Length > 0 && RectangleGap(movingBounds, _dragMembershipSourceBounds) >= GroupRemovalDistance)
            {
                _pendingRemovalGroupId = source;
                ShowGroupDropHint("松手移出组合", movingBounds);
                return;
            }
        }
        ClearGroupDropHint();
    }

    private static double RectangleGap(Rect first, Rect second)
    {
        var dx = Math.Max(0, Math.Max(second.Left - first.Right, first.Left - second.Right));
        var dy = Math.Max(0, Math.Max(second.Top - first.Bottom, first.Top - second.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void ShowGroupDropHint(string text, Rect movingBounds)
    {
        GroupDropHintText.Text = text;
        GroupDropHint.Visibility = Visibility.Visible;
        GroupDropHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = GroupDropHint.DesiredSize.Width;
        var height = GroupDropHint.DesiredSize.Height;
        var center = (movingBounds.Left + movingBounds.Right) / 2 * _viewZoom + _viewPanX;
        var top = movingBounds.Top * _viewZoom + _viewPanY;
        Canvas.SetLeft(GroupDropHint, Math.Clamp(center - width / 2, 8,
            Math.Max(8, BoardSurface.ActualWidth - width - 8)));
        Canvas.SetTop(GroupDropHint, Math.Max(8, top - height - 12));
        GroupDropHint.BeginAnimation(OpacityProperty,
            new DoubleAnimation(GroupDropHint.Opacity, 1, TimeSpan.FromMilliseconds(120)));
    }

    private void ClearGroupDropHint()
    {
        GroupDropHint.Visibility = Visibility.Collapsed;
        GroupDropHint.BeginAnimation(OpacityProperty, null);
        GroupDropHint.Opacity = 1;
        _pendingMembershipGroupId = null;
        _pendingRemovalGroupId = null;
    }

    private bool ApplyPendingGroupMembership()
    {
        var moving = AllElements.Where(x => _selected.Contains(x.Id)).ToArray();
        if (_pendingMembershipGroupId is { Length: > 0 } targetId)
        {
            if (_groups.All(group => group.Id != targetId)) return false;
            var movingGroup = SelectedGroup();
            if (movingGroup is not null && GroupMembers(movingGroup.Id).Length == moving.Length)
                movingGroup.ParentGroupId = targetId;
            else foreach (var element in moving) element.GroupId = targetId;
            BoardLayerTreeService.RemoveEmptyGroups(_groups, AllElements);
            BoardLayerTreeService.NormalizeZIndices(_groups, AllElements);
            BoardLayerTreeService.SyncLegacyPresentation(_groups, AllElements);
            UpdateGroupVisuals();
            RefreshLayersPanel();
            BoardStatus.Text = $"已将 {moving.Length} 个元素加入组合";
            return true;
        }
        if (_pendingRemovalGroupId is { Length: > 0 } sourceId)
        {
            var source = _groups.FirstOrDefault(group => group.Id == sourceId);
            if (source is null) return false;
            var movingGroup = SelectedGroup();
            var removed = moving.Where(x => x.GroupId == sourceId).ToArray();
            if (movingGroup is not null && movingGroup.ParentGroupId == sourceId)
                movingGroup.ParentGroupId = source.ParentGroupId;
            else foreach (var element in removed) element.GroupId = source.ParentGroupId;
            BoardLayerTreeService.RemoveEmptyGroups(_groups, AllElements);
            BoardLayerTreeService.NormalizeZIndices(_groups, AllElements);
            BoardLayerTreeService.SyncLegacyPresentation(_groups, AllElements);
            UpdateGroupVisuals();
            RefreshLayersPanel();
            BoardStatus.Text = $"已将 {removed.Length} 个元素移出组合";
            return removed.Length > 0 || movingGroup is not null;
        }
        return false;
    }

    private void PrepareGroupMembershipDrag()
    {
        _dragMembershipSourceGroupId = null;
        _dragMembershipSourceBounds = Rect.Empty;
        var moving = AllElements.Where(x => _selected.Contains(x.Id)).ToArray();
        var selectedGroup = SelectedGroup();
        var parentId = selectedGroup?.ParentGroupId;
        if (parentId is null)
        {
            var groups = moving.Select(x => x.GroupId).Distinct().ToArray();
            if (groups.Length != 1) return;
            parentId = groups[0];
        }
        if (string.IsNullOrEmpty(parentId)) return;
        _dragMembershipSourceGroupId = parentId;
        _dragMembershipSourceBounds = GroupBounds(parentId);
    }

    private void FinishGroupMembershipDrag()
    {
        _dragMembershipSourceGroupId = null;
        _dragMembershipSourceBounds = Rect.Empty;
    }

    private sealed record GroupBackgroundTag(string GroupId);

    private static BoardItem CreateSelectionBoundsItem(IReadOnlyList<BoardElement> items)
    {
        if (items.Count < 2 || items.Any(x => x is not BoardItem))
            return CreateBoundsItem(GetBounds(items));
        var images = items.Cast<BoardItem>().ToArray();
        if (images[0].GroupId.Length == 0 || images.Any(x => x.GroupId != images[0].GroupId))
            return CreateBoundsItem(GetBounds(items));

        // Use one member as the group's persistent local axis. Every group rotation
        // adds the same delta to all members, so this frame follows the group exactly.
        var angle = images[0].Rotation;
        var origin = new Point(0, 0);
        var localPoints = new List<Point>(images.Length * 4);
        foreach (var image in images)
        {
            var center = new Point(image.X + image.Width / 2, image.Y + image.Height / 2);
            foreach (var corner in new[]
                     {
                         new Point(image.X, image.Y), new Point(image.X + image.Width, image.Y),
                         new Point(image.X + image.Width, image.Y + image.Height), new Point(image.X, image.Y + image.Height)
                     })
            {
                var world = BoardMath.RotatePoint(corner, center, image.Rotation);
                localPoints.Add(BoardMath.RotatePoint(world, origin, -angle));
            }
        }
        var left = localPoints.Min(x => x.X);
        var top = localPoints.Min(x => x.Y);
        var right = localPoints.Max(x => x.X);
        var bottom = localPoints.Max(x => x.Y);
        var localCenter = new Point((left + right) / 2, (top + bottom) / 2);
        var worldCenter = BoardMath.RotatePoint(localCenter, origin, angle);
        return new BoardItem
        {
            X = worldCenter.X - (right - left) / 2,
            Y = worldCenter.Y - (bottom - top) / 2,
            Width = Math.Max(1, right - left),
            Height = Math.Max(1, bottom - top),
            Rotation = angle
        };
    }

    private static Rect RotatedImageBounds(BoardElement item)
    {
        var bounds = new Rect(item.X, item.Y, item.Width, item.Height);
        return new RotateTransform(item.Rotation, item.X + item.Width / 2, item.Y + item.Height / 2)
            .TransformBounds(bounds);
    }

    private void ToggleImageFocus(BoardItem item, bool preserveZoom = false) =>
        ToggleElementFocus(item, preserveZoom);

    private void ToggleElementFocus(BoardElement item, bool preserveZoom = false)
    {
        _draggingItems = false;
        _gestureSnapshot = null;
        System.Windows.Input.Mouse.Capture(null);
        EndContinuousInteraction();
        if (_focusedImageId == item.Id)
        {
            _focusedImageId = null;
            _viewZoom = _focusReturnZoom;
            _viewPanX = _focusReturnPanX;
            _viewPanY = _focusReturnPanY;
            ApplyViewportTransform();
            UpdateSelectionVisuals();
            QueueViewportSave();
            BoardStatus.Text = item is BoardItem ? "已退出图片聚焦" : "已退出元素定位";
            return;
        }
        if (_focusedImageId is null)
        {
            _focusReturnZoom = _viewZoom;
            _focusReturnPanX = _viewPanX;
            _focusReturnPanY = _viewPanY;
        }
        _focusedImageId = item.Id;
        if (item.GroupId.Length > 0) _individualGroupSelectionId = item.Id;
        _selected.Clear();
        _selected.Add(item.Id);
        var bounds = RotatedImageBounds(item);
        var width = Math.Max(1, BoardSurface.ActualWidth - 40);
        var height = Math.Max(1, BoardSurface.ActualHeight - 40);
        if (!preserveZoom)
            _viewZoom = Math.Clamp(Math.Min(width / Math.Max(1, bounds.Width), height / Math.Max(1, bounds.Height)), .05, 8);
        _viewPanX = BoardSurface.ActualWidth / 2 - (bounds.X + bounds.Width / 2) * _viewZoom;
        _viewPanY = BoardSurface.ActualHeight / 2 - (bounds.Y + bounds.Height / 2) * _viewZoom;
        ApplyViewportTransform();
        UpdateSelectionVisuals();
        QueueViewportSave();
        var typeName = item switch
        {
            BoardTextItem => "文字",
            BoardDrawingItem => "绘制",
            _ => "图片"
        };
        BoardStatus.Text = preserveZoom
            ? $"已定位{typeName} · 保持当前缩放"
            : $"已聚焦{typeName} · 滚轮调整缩放";
    }

    private void ToggleGroupFocus(string groupId)
    {
        var group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null || GroupMembers(groupId).Length == 0) return;
        _draggingItems = false;
        _gestureSnapshot = null;
        System.Windows.Input.Mouse.Capture(null);
        EndContinuousInteraction();
        if (_focusedImageId == groupId)
        {
            _focusedImageId = null;
            _viewZoom = _focusReturnZoom;
            _viewPanX = _focusReturnPanX;
            _viewPanY = _focusReturnPanY;
            ApplyViewportTransform();
            UpdateSelectionVisuals();
            QueueViewportSave();
            BoardStatus.Text = "已退出组合定位";
            return;
        }
        if (_focusedImageId is null)
        {
            _focusReturnZoom = _viewZoom;
            _focusReturnPanX = _viewPanX;
            _focusReturnPanY = _viewPanY;
        }
        _focusedImageId = groupId;
        var bounds = GroupBounds(groupId);
        _viewPanX = BoardSurface.ActualWidth / 2 - (bounds.X + bounds.Width / 2) * _viewZoom;
        _viewPanY = BoardSurface.ActualHeight / 2 - (bounds.Y + bounds.Height / 2) * _viewZoom;
        ApplyViewportTransform();
        UpdateSelectionVisuals();
        QueueViewportSave();
        BoardStatus.Text = $"已定位组合“{group.LayerName}” · 保持当前缩放";
    }

    private async Task ArrangeImagesAsync()
    {
        await FlushPendingDrawingAsync();
        ExpandSelectedImageGroups();
        var images = _items.Where(x => _selected.Count == 0 || _selected.Contains(x.Id)).ToArray();
        if (images.Length == 0) { BoardStatus.Text = "当前范围内没有图片"; return; }
        if (images.Length == 1) { BoardStatus.Text = "至少需要两张图片进行排列"; return; }
        // A saved group is one layout unit, including text and drawings that share
        // the image's group. Arranging never breaks a mixed group's internal layout.
        var units = images.GroupBy(x => x.GroupId.Length == 0 ? "image:" + x.Id : "group:" + RootGroupId(x.GroupId))
            .Select(group =>
            {
                var elements = group.First().GroupId.Length == 0
                    ? group.Cast<BoardElement>().ToArray()
                    : GroupMembers(RootGroupId(group.First().GroupId));
                var bounds = elements.Select(RotatedImageBounds)
                    .Aggregate(Rect.Empty, (current, next) => { current.Union(next); return current; });
                return (Elements: elements, Bounds: bounds);
            }).ToArray();
        if (units.Length < 2) { BoardStatus.Text = "当前只有一个图片组，无需排列"; return; }
        PushUndoSnapshot();
        var boxes = units.Select((unit, i) => new BoardItem { Id = i.ToString(), X = unit.Bounds.X,
            Y = unit.Bounds.Y, Width = unit.Bounds.Width, Height = unit.Bounds.Height, ZIndex = i }).ToList();
        var arranged = BoardMath.ArrangeGrid(boxes);
        var originX = units.Min(x => x.Bounds.Left);
        var originY = units.Min(x => x.Bounds.Top);
        foreach (var box in arranged)
        {
            var unit = units[int.Parse(box.Id)];
            var dx = originX + box.X - unit.Bounds.X;
            var dy = originY + box.Y - unit.Bounds.Y;
            foreach (var element in unit.Elements) { element.X += dx; element.Y += dy; UpdateItemVisual(element); }
        }
        await PersistElementsAsync(units.SelectMany(unit => unit.Elements));
        UpdateSelectionVisuals();
        if (_selected.Count == 0) FitAll();
        BoardStatus.Text = _selected.Count == 0 ? "已自动排布全部图片" : "已自动排布选中图片";
    }
}
