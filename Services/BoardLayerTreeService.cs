using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class BoardLayerTreeService
{
    public const int MaxDepth = 32;

    public sealed record Node(string Id, bool IsGroup, string ParentGroupId,
        BoardElement? Element, BoardGroup? Group, IReadOnlyList<Node> Children)
    {
        public int FrontZ => IsGroup
            ? Children.Select(child => child.FrontZ).DefaultIfEmpty(int.MinValue).Max()
            : Element!.ZIndex;
    }

    public static IReadOnlyList<Node> BuildTree(
        IReadOnlyList<BoardGroup> groups, IEnumerable<BoardElement> elements)
    {
        var groupMap = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var elementList = elements.ToArray();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        Node BuildGroup(BoardGroup group, int depth)
        {
            if (depth > MaxDepth || !visiting.Add(group.Id))
                throw new InvalidDataException("组合层级包含循环或超过 32 层。");
            var children = groupMap.Values.Where(candidate => candidate.ParentGroupId == group.Id)
                .Select(candidate => BuildGroup(candidate, depth + 1))
                .Concat(elementList.Where(element => element.GroupId == group.Id)
                    .Select(element => new Node(element.Id, false, group.Id, element, null, Array.Empty<Node>())))
                .OrderByDescending(node => node.FrontZ).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
            visiting.Remove(group.Id);
            return new Node(group.Id, true, group.ParentGroupId, null, group, children);
        }

        var roots = groupMap.Values.Where(group => string.IsNullOrEmpty(group.ParentGroupId))
            .Select(group => BuildGroup(group, 1))
            .Concat(elementList.Where(element => string.IsNullOrEmpty(element.GroupId))
                .Select(element => new Node(element.Id, false, "", element, null, Array.Empty<Node>())))
            .OrderByDescending(node => node.FrontZ).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
        return roots;
    }

    public static IReadOnlyList<BoardElement> DescendantElements(string groupId,
        IReadOnlyList<BoardGroup> groups, IEnumerable<BoardElement> elements)
    {
        var groupIds = DescendantGroupIds(groupId, groups);
        groupIds.Add(groupId);
        return elements.Where(element => groupIds.Contains(element.GroupId)).ToArray();
    }

    public static IReadOnlyList<BoardGroup> DescendantGroups(string groupId, IReadOnlyList<BoardGroup> groups)
    {
        var ids = DescendantGroupIds(groupId, groups);
        return groups.Where(group => ids.Contains(group.Id)).ToArray();
    }

    public static string? OutermostLockedAncestor(BoardElement element, IReadOnlyList<BoardGroup> groups)
    {
        if (string.IsNullOrEmpty(element.GroupId)) return null;
        var map = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var current = element.GroupId;
        string? locked = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(current) && map.TryGetValue(current, out var group) && visited.Add(current))
        {
            if (group.Locked) locked = group.Id;
            current = group.ParentGroupId;
        }
        return locked;
    }

    public static void NormalizeZIndices(IReadOnlyList<BoardGroup> groups, IEnumerable<BoardElement> elements)
    {
        var flattened = Flatten(BuildTree(groups, elements)).ToArray();
        for (var index = 0; index < flattened.Length; index++)
            flattened[index].ZIndex = flattened.Length - index - 1;
    }

    public static void SyncLegacyPresentation(IReadOnlyList<BoardGroup> groups,
        IEnumerable<BoardElement> elements)
    {
        var groupMap = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        foreach (var element in elements)
        {
            if (!groupMap.TryGetValue(element.GroupId, out var group))
            {
                element.GroupBackgroundColor = "#52FFFFFF";
                element.GroupBorderColor = "#807A7A7A";
                element.GroupBorderThickness = 1.2;
                element.GroupFramePadding = 14;
                element.GroupBackgroundVisible = true;
                element.GroupLocked = true;
                element.GroupAutoMembership = false;
                continue;
            }
            element.GroupBackgroundColor = group.BackgroundColor;
            element.GroupBorderColor = group.BorderColor;
            element.GroupBorderThickness = group.BorderThickness;
            element.GroupFramePadding = group.FramePadding;
            element.GroupBackgroundVisible = group.BackgroundVisible;
            element.GroupLocked = group.Locked;
            element.GroupAutoMembership = group.AutoMembership;
        }
    }

    public static bool MoveNode(List<BoardGroup> groups, IReadOnlyList<BoardElement> elements,
        string nodeId, bool isGroup, string targetParentGroupId, string? beforeNodeId)
    {
        var sourceGroup = isGroup ? groups.FirstOrDefault(group => group.Id == nodeId) : null;
        var sourceElement = isGroup ? null : elements.FirstOrDefault(element => element.Id == nodeId);
        if (sourceGroup is null && sourceElement is null) return false;
        if (targetParentGroupId.Length > 0 && groups.All(group => group.Id != targetParentGroupId)) return false;
        if (sourceGroup is not null && (targetParentGroupId == sourceGroup.Id ||
            DescendantGroupIds(sourceGroup.Id, groups).Contains(targetParentGroupId))) return false;

        var order = CaptureSiblingOrder(groups, elements);
        var oldParent = sourceGroup?.ParentGroupId ?? sourceElement!.GroupId;
        if (order.TryGetValue(oldParent, out var oldSiblings)) oldSiblings.RemoveAll(node => node.Id == nodeId && node.IsGroup == isGroup);
        if (!order.TryGetValue(targetParentGroupId, out var targetSiblings))
            order[targetParentGroupId] = targetSiblings = new List<(string Id, bool IsGroup)>();
        var insert = beforeNodeId is null ? targetSiblings.Count : targetSiblings.FindIndex(node => node.Id == beforeNodeId);
        if (insert < 0) insert = targetSiblings.Count;
        targetSiblings.Insert(insert, (nodeId, isGroup));
        if (sourceGroup is not null) sourceGroup.ParentGroupId = targetParentGroupId;
        else sourceElement!.GroupId = targetParentGroupId;

        if (!ValidateDepth(groups))
        {
            if (sourceGroup is not null) sourceGroup.ParentGroupId = oldParent;
            else sourceElement!.GroupId = oldParent;
            return false;
        }

        RemoveEmptyGroups(groups, elements);
        ApplyCapturedOrder(groups, elements, order);
        return true;
    }

    public static void RemoveEmptyGroups(List<BoardGroup> groups, IEnumerable<BoardElement> elements)
    {
        var elementList = elements.ToArray();
        bool removed;
        do
        {
            removed = false;
            foreach (var group in groups.ToArray())
            {
                if (elementList.Any(element => element.GroupId == group.Id) || groups.Any(child => child.ParentGroupId == group.Id)) continue;
                groups.Remove(group);
                removed = true;
            }
        } while (removed);
    }

    public static void Validate(IReadOnlyList<BoardGroup> groups, IEnumerable<BoardElement> elements)
    {
        var elementList = elements.ToArray();
        if (groups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != groups.Count)
            throw new InvalidDataException("组合编号重复。");
        var ids = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        if (elementList.Select(element => element.Id).Distinct(StringComparer.Ordinal).Count() != elementList.Length ||
            elementList.Any(element => ids.Contains(element.Id)))
            throw new InvalidDataException("图层编号重复。");
        if (groups.Any(group => group.ParentGroupId.Length > 0 && !ids.Contains(group.ParentGroupId)) ||
            elementList.Any(element => element.GroupId.Length > 0 && !ids.Contains(element.GroupId)))
            throw new InvalidDataException("组合父级不存在。");
        if (!ValidateDepth(groups)) throw new InvalidDataException("组合层级包含循环或超过 32 层。");
        if (groups.Any(group => !elementList.Any(element => IsDescendantOf(element, group.Id, groups))))
            throw new InvalidDataException("场景包含空组合。");
    }

    private static bool IsDescendantOf(BoardElement element, string groupId, IReadOnlyList<BoardGroup> groups)
    {
        var map = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var current = element.GroupId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current.Length > 0 && visited.Add(current))
        {
            if (current == groupId) return true;
            if (!map.TryGetValue(current, out var group)) return false;
            current = group.ParentGroupId;
        }
        return false;
    }

    private static HashSet<string> DescendantGroupIds(string groupId, IReadOnlyList<BoardGroup> groups)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(groupId);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in groups.Where(group => group.ParentGroupId == parent))
                if (result.Add(child.Id)) queue.Enqueue(child.Id);
        }
        return result;
    }

    private static bool ValidateDepth(IReadOnlyList<BoardGroup> groups)
    {
        var map = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var current = group;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var depth = 1;
            while (!string.IsNullOrEmpty(current.ParentGroupId))
            {
                if (!visited.Add(current.Id) || depth++ >= MaxDepth || !map.TryGetValue(current.ParentGroupId, out current!)) return false;
            }
        }
        return true;
    }

    private static IEnumerable<BoardElement> Flatten(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsGroup)
            {
                foreach (var child in Flatten(node.Children)) yield return child;
            }
            else yield return node.Element!;
        }
    }

    private static Dictionary<string, List<(string Id, bool IsGroup)>> CaptureSiblingOrder(
        IReadOnlyList<BoardGroup> groups, IReadOnlyList<BoardElement> elements)
    {
        var result = new Dictionary<string, List<(string Id, bool IsGroup)>>(StringComparer.Ordinal);
        void Capture(string parent, IEnumerable<Node> nodes)
        {
            result[parent] = nodes.Select(node => (node.Id, node.IsGroup)).ToList();
            foreach (var node in nodes.Where(node => node.IsGroup)) Capture(node.Id, node.Children);
        }
        Capture("", BuildTree(groups, elements));
        return result;
    }

    private static void ApplyCapturedOrder(IReadOnlyList<BoardGroup> groups, IReadOnlyList<BoardElement> elements,
        Dictionary<string, List<(string Id, bool IsGroup)>> order)
    {
        var groupMap = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var elementMap = elements.ToDictionary(element => element.Id, StringComparer.Ordinal);
        var flat = new List<BoardElement>();
        void Append(string parent)
        {
            if (!order.TryGetValue(parent, out var nodes))
            {
                nodes = groupMap.Values.Where(group => group.ParentGroupId == parent).Select(group => (Id: group.Id, IsGroup: true))
                    .Concat(elementMap.Values.Where(element => element.GroupId == parent).Select(element => (Id: element.Id, IsGroup: false)))
                    .OrderByDescending(node => node.IsGroup
                        ? DescendantElements(node.Id, groups, elements).Select(element => element.ZIndex).DefaultIfEmpty(int.MinValue).Max()
                        : elementMap[node.Id].ZIndex).ToList();
            }
            foreach (var node in nodes)
            {
                if (node.IsGroup && groupMap.ContainsKey(node.Id)) Append(node.Id);
                else if (!node.IsGroup && elementMap.TryGetValue(node.Id, out var element)) flat.Add(element);
            }
        }
        Append("");
        for (var index = 0; index < flat.Count; index++) flat[index].ZIndex = flat.Count - index - 1;
    }
}
