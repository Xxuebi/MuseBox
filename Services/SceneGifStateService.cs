using ScreenshotCollector.Models;
namespace ScreenshotCollector.Services;
public static class SceneGifStateService
{
    // Only trusted local snapshots use this repair. Scene-file loading keeps strict validation.
    public static void PrepareLocalSnapshot(SceneDocument document, IReadOnlyDictionary<string,string> paths)
    {
        document.Assets = document.Assets.Select(asset =>
        {
            var actual = paths.TryGetValue(asset.Id,out var path) ? ImageFileFormatService.FromFile(path) : null;
            return actual is null ? asset : asset with { Extension = actual };
        }).ToList();
        var animatedAssets = document.Assets.Where(a=>a.Extension==".gif").Select(a=>a.Id).ToHashSet();
        var animatedItems = document.Images.Where(i=>animatedAssets.Contains(i.AssetId)).Select(i=>i.Id).ToHashSet();
        // Editing or replacing an external GIF can leave a playback row on a now-static image.
        document.Gifs = document.Gifs.Where(g=>animatedItems.Contains(g.ItemId))
            .GroupBy(g=>g.ItemId).Select(group=>
            {
                var state=group.First();
                return state with { Speed = double.IsFinite(state.Speed) ? Math.Clamp(state.Speed,.25,4) : 1,
                    FrameIndex = Math.Clamp(state.FrameIndex,0,1000000) };
            }).ToList();
    }
}

