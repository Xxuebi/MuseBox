using System.Windows;
using System.Windows.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private bool _layoutBusy;
    private sealed record LayoutTarget(BoardLayoutUnit Unit, BoardElement[] Elements);

    private Rect LayoutGroupBounds(string groupId)
    {
        var group = _groups.Single(group => group.Id == groupId);
        // Visible chrome must match its actual rendered rectangle, including nested padding.
        if (group.BackgroundVisible) return GroupBounds(groupId);
        var bounds = Rect.Empty;
        foreach (var element in AllElements.Where(element => element.GroupId == groupId))
            bounds.Union(RotatedImageBounds(element));
        foreach (var child in _groups.Where(child => child.ParentGroupId == groupId))
            bounds.Union(LayoutGroupBounds(child.Id));
        return bounds;
    }

    private LayoutTarget[] GetLayoutTargets()
    {
        var elements = AllElements.Where(element => _selected.Count == 0 || _selected.Contains(element.Id)).ToArray();
        var ids = elements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        var fullGroups = _groups.Where(group =>
        {
            var members = GroupMembers(group.Id);
            return members.Length > 0 && members.All(member => ids.Contains(member.Id));
        }).ToArray();
        // A directly selected inner group must not be promoted to a same-members ancestor.
        if (_selected.Count > 0 && _explicitSelectedGroupId is { } explicitId &&
            ids.SetEquals(GroupMembers(explicitId).Select(element => element.Id)))
            fullGroups = fullGroups.Where(group => !IsGroupAncestor(group.Id, explicitId)).ToArray();
        var roots = fullGroups.Where(group => !fullGroups.Any(parent => IsGroupAncestor(parent.Id, group.Id))).ToArray();
        var covered = roots.SelectMany(group => GroupMembers(group.Id)).Select(element => element.Id).ToHashSet();
        var targets = roots.Select(group =>
        {
            var members = GroupMembers(group.Id);
            return new LayoutTarget(new BoardLayoutUnit("group:" + group.Id, LayoutGroupBounds(group.Id),
                members.Min(element => element.ZIndex)), members);
        }).Concat(elements.Where(element => !covered.Contains(element.Id)).Select(element =>
            new LayoutTarget(new BoardLayoutUnit("element:" + element.Id, RotatedImageBounds(element),
                element.ZIndex), new[] { element })));
        return targets.OrderBy(target => target.Unit.ZIndex).ThenBy(target => target.Unit.Id, StringComparer.Ordinal).ToArray();
    }

    private void UpdateLayoutMenu()
    {
        var count = GetLayoutTargets().Length;
        ArrangeMenuItem.IsEnabled = !_layoutBusy && count >= 2;
        UpdateChildren(ArrangeMenuItem);
        void UpdateChildren(MenuItem parent)
        {
            foreach (var menu in parent.Items.OfType<MenuItem>())
            {
                menu.IsEnabled = !_layoutBusy && count >=
                    (menu.Tag is string name && Enum.TryParse<BoardLayoutOperation>(name, out var operation)
                        ? BoardLayoutService.MinimumUnits(operation) : 2);
                UpdateChildren(menu);
            }
        }
    }

    private async void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } && Enum.TryParse<BoardLayoutOperation>(name, out var operation))
            await ApplyLayoutAsync(operation);
        e.Handled = true;
    }

    private Task ArrangeImagesAsync() => ApplyLayoutAsync(BoardLayoutOperation.AutoArrange);

    private async Task ApplyLayoutAsync(BoardLayoutOperation operation)
    {
        if (_layoutBusy) return;
        _layoutBusy = true;
        var hadSelection = _selected.Count > 0;
        var selectedIds = _selected.ToHashSet(StringComparer.Ordinal);
        var enabledValue = ReadLocalValue(IsEnabledProperty);
        IsEnabled = false;
        try
        {
            await CommitTextEditingAsync();
            await FlushPendingDrawingAsync();
            _selected.Clear();
            if (hadSelection)
            {
                _selected.UnionWith(AllElements.Where(element => selectedIds.Contains(element.Id)).Select(element => element.Id));
                if (_selected.Count == 0) { BoardStatus.Text = "选中的元素已不存在"; return; }
            }
            var targets = GetLayoutTargets();
            if (targets.Length < BoardLayoutService.MinimumUnits(operation))
            {
                BoardStatus.Text = $"至少需要 {BoardLayoutService.MinimumUnits(operation)} 个排列单位";
                return;
            }
            var offsets = BoardLayoutService.Calculate(targets.Select(target => target.Unit).ToArray(), operation);
            var moves = targets.SelectMany(target => target.Elements.Select(element =>
                (Element: element, Offset: offsets[target.Unit.Id])))
                .Where(move => Math.Abs(move.Offset.X) > .000001 || Math.Abs(move.Offset.Y) > .000001).ToArray();
            if (moves.Length == 0) { BoardStatus.Text = "元素已处于目标位置"; return; }
            var before = Snapshot();
            var positions = moves.Select(move => new BoardElementPosition(move.Element.Id, move.Element switch
            {
                BoardItem => BoardElementKind.Image,
                BoardTextItem => BoardElementKind.Text,
                BoardDrawingItem => BoardElementKind.Drawing,
                _ => throw new InvalidOperationException("不支持的排列元素。")
            }, move.Element.X + move.Offset.X, move.Element.Y + move.Offset.Y)).ToArray();
            await _repository.ApplyElementPositionsAsync(_drawerId, positions);
            foreach (var move in moves)
            {
                move.Element.X += move.Offset.X;
                move.Element.Y += move.Offset.Y;
                UpdateItemVisual(move.Element);
            }
            PushUndoSnapshot(before);
            RenderGroupBackgrounds();
            UpdateSelectionVisuals();
            RefreshLayersPanel();
            if (!hadSelection && operation == BoardLayoutOperation.AutoArrange) FitAll();
            BoardStatus.Text = !hadSelection ? "已排列全部元素" : "已排列选中元素";
        }
        catch (Exception exception)
        {
            BoardStatus.Text = $"排列失败：{exception.Message}";
        }
        finally
        {
            _layoutBusy = false;
            if (enabledValue == DependencyProperty.UnsetValue) ClearValue(IsEnabledProperty);
            else SetValue(IsEnabledProperty, enabledValue);
            UpdateLayoutMenu();
        }
    }
}
