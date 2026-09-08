using System.Text.Json.Serialization;
namespace ScreenshotCollector.Models;

public enum ImageImportDestination { Board, Materials }
public sealed class ImageImportBatch : List<BoardItem>
{
    public ImageImportDestination Destination { get; }
    public ImageImportBatch(IEnumerable<BoardItem> items, ImageImportDestination destination) : base(items) => Destination = destination;
}
public sealed class BoardMaterialItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DrawerId { get; set; } = "A";
    public string AssetId { get; set; } = "";
    public string LayerName { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public long SortOrder { get; set; }
    [JsonIgnore] public string AssetPath { get; set; } = "";
    [JsonIgnore] public int PixelWidth { get; set; } = 320;
    [JsonIgnore] public int PixelHeight { get; set; } = 240;
    public BoardMaterialItem Clone() => (BoardMaterialItem)MemberwiseClone();
    public BoardItem ToImage()
    {
        var size = Services.BoardMath.FitSize(PixelWidth, PixelHeight);
        return new BoardItem { Id = Id, DrawerId = DrawerId, AssetId = AssetId, AssetPath = AssetPath,
            LayerName = LayerName, CreatedUtc = CreatedUtc, Width = size.Width, Height = size.Height };
    }
}
public sealed record MaterialChange(string DrawerId, IReadOnlyList<BoardMaterialItem> BeforeMaterials,
    IReadOnlyList<BoardMaterialItem> AfterMaterials, IReadOnlyList<BoardItem> BeforeImages, IReadOnlyList<BoardItem> AfterImages)
{
    public MaterialChange DeepCopy() => new(DrawerId, BeforeMaterials.Select(m => m.Clone()).ToArray(), AfterMaterials.Select(m => m.Clone()).ToArray(), BeforeImages.Select(m => m.Clone()).ToArray(), AfterImages.Select(m => m.Clone()).ToArray());
    public MaterialChange Inverse() => new(DrawerId, AfterMaterials, BeforeMaterials, AfterImages, BeforeImages);
}

