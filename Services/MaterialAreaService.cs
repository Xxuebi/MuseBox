using System.Runtime.CompilerServices;
using System.Windows;
using ScreenshotCollector.Models;
namespace ScreenshotCollector.Services;

public sealed class MaterialAreaSession
{
    private static readonly ConditionalWeakTable<IBoardRepository, MaterialAreaSession> Sessions = new();
    public static MaterialAreaSession For(IBoardRepository repository) => Sessions.GetValue(repository, _ => new());
    // Pending history is scoped by drawer; enablement is persisted in each BoardViewport.
    private readonly Dictionary<string, List<MaterialChange>> _pending = new();
    public void Queue(MaterialChange change)
    {
        if (!_pending.TryGetValue(change.DrawerId, out var list)) _pending[change.DrawerId] = list = new();
        list.Add(change);
    }
    public IReadOnlyList<MaterialChange> Take(string drawer) => _pending.Remove(drawer, out var list) ? list : Array.Empty<MaterialChange>();
    public void Clear(string drawer) => _pending.Remove(drawer);
}
public static class MaterialAreaService
{
    public static async Task<MaterialChange> PlanMoveAsync(IBoardRepository repository, string drawerId,
        IReadOnlySet<string>? selected = null, Point? center = null)
    {
        var materials = (await repository.GetMaterialsAsync(drawerId)).Where(m => selected is null || selected.Contains(m.Id)).ToArray();
        var snapshot = await repository.CaptureSceneAsync(drawerId);
        return PlanMove(snapshot, drawerId, materials, center);
    }
    public static MaterialChange PlanMove(SceneSnapshot snapshot, string drawerId,
        IReadOnlyList<BoardMaterialItem> materials, Point? center = null)
    {
        var existing = snapshot.Document.Images.Cast<BoardElement>().Concat(snapshot.Document.Texts)
            .Concat(snapshot.Document.Drawings).ToArray();
        var images = materials.Select(m => {
            var item = m.ToImage(); item.DrawerId = drawerId;
            var asset = snapshot.Document.Assets.First(a => a.Id == item.AssetId);
            var size = BoardMath.FitSize(asset.Width, asset.Height);
            item.Width = size.Width; item.Height = size.Height;
            return item;
        }).ToArray();
        var z = existing.Select(i => i.ZIndex).DefaultIfEmpty(-1).Max() + 1;
        foreach (var image in images) image.ZIndex = z++;
        var arranged = BoardMath.ArrangeGrid(images, 18).ToDictionary(i => i.Id);
        foreach (var image in images) { image.X = arranged[image.Id].X; image.Y = arranged[image.Id].Y; }
        if (images.Length > 0)
        {
            var bounds = new Rect(images.Min(i => i.X), images.Min(i => i.Y),
                images.Max(i => i.X + i.Width) - images.Min(i => i.X), images.Max(i => i.Y + i.Height) - images.Min(i => i.Y));
            Point origin;
            if (center is Point drop) origin = new Point(drop.X - bounds.Width / 2, drop.Y - bounds.Height / 2);
            else if (existing.Length == 0) origin = new Point(0, 0);
            else
            {
                var allBounds = SceneThumbnailRenderer.GetBoardBounds(snapshot);
                origin = new Point(allBounds.Right + 32, allBounds.Top);
            }
            foreach (var image in images) { image.X += origin.X - bounds.Left; image.Y += origin.Y - bounds.Top; }
        }
        return new MaterialChange(drawerId, materials, Array.Empty<BoardMaterialItem>(), Array.Empty<BoardItem>(), images);
    }
}

