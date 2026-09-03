using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class SceneMigration
{
    public static void UpgradeToCurrent(SceneDocument scene)
    {
        scene.Groups ??= new List<BoardGroup>();
        var elements = scene.Images.Cast<BoardElement>().Concat(scene.Texts).Concat(scene.Drawings).ToArray();
        if (scene.Version is 1 or 2)
        {
            var needsNormalization = scene.Version == 1;
            var existingIds = scene.Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
            var index = scene.Groups.Count + 1;
            foreach (var legacy in elements.Where(element => element.GroupId.Length > 0 && !existingIds.Contains(element.GroupId))
                         .GroupBy(element => element.GroupId).OrderByDescending(group => group.Max(element => element.ZIndex)))
            {
                var presentation = legacy.First();
                scene.Groups.Add(new BoardGroup
                {
                    Id = legacy.Key,
                    DrawerId = string.Empty,
                    LayerName = $"组合 {index++}",
                    BackgroundColor = presentation.GroupBackgroundColor,
                    BorderColor = presentation.GroupBorderColor,
                    BorderThickness = presentation.GroupBorderThickness,
                    FramePadding = presentation.GroupFramePadding,
                    BackgroundVisible = presentation.GroupBackgroundVisible,
                    Locked = presentation.GroupLocked,
                    AutoMembership = presentation.GroupAutoMembership
                });
                existingIds.Add(legacy.Key);
                needsNormalization = true;
            }
            scene.Version = 2;
            if (needsNormalization)
            {
                BoardLayerTreeService.Validate(scene.Groups, elements);
                BoardLayerTreeService.NormalizeZIndices(scene.Groups, elements);
            }
        }
        BoardLayerNameService.EnsureNames(elements, scene.Groups);
        BoardLayerTreeService.SyncLegacyPresentation(scene.Groups, elements);
    }
}
