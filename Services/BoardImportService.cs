using System.Drawing;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed class BoardImportService
{
    private readonly AssetLibraryService _assets;
    private readonly IBoardRepository _repository;

    public BoardImportService(AssetLibraryService assets, IBoardRepository repository)
    {
        _assets = assets;
        _repository = repository;
    }

    public async Task<IReadOnlyList<BoardItem>> ImportBitmapAsync(
        string drawerId, Bitmap bitmap, PointF? center = null, CancellationToken cancellationToken = default,
        string? layerName = null, ImageImportDestination destination = ImageImportDestination.Board)
    {
        var imported = await _assets.ImportBitmapAsync(bitmap, cancellationToken);
        return await AddAssetsAsync(drawerId, new[] { imported }, center, cancellationToken,
            new[] { layerName ?? "剪贴板图片" }, destination);
    }

    public Task<ImportedAsset> StageInternalAssetAsync(string path, CancellationToken token = default)
        => _assets.ImportFileAsync(path, token);

    // Editing creates a new immutable asset, leaving the old image available to undo.
    public Task<ImportedAsset> SaveEditedBitmapAsync(Bitmap bitmap) => _assets.ImportBitmapAsync(bitmap);

    public async Task<DrawerCover> SaveDrawerCoverAsync(string drawerId, string sourcePath, Bitmap preview, CoverCropState crop)
    {
        var source = await _assets.ImportFileAsync(sourcePath);
        var rendered = await _assets.ImportBitmapAsync(preview);
        var cover = new DrawerCover(source.Asset.Id, rendered.Asset.Id, crop, source.FullPath, rendered.FullPath);
        await _repository.UpdateDrawerCoverAsync(drawerId, cover);
        return cover;
    }

    public async Task<IReadOnlyList<BoardItem>> ImportClipboardAsync(string drawerId, ClipboardImageResult clipboard,
        PointF? center = null, CancellationToken cancellationToken = default, ImageImportDestination destination = ImageImportDestination.Board)
    {
        if (clipboard.FilePaths.Count > 0)
            return await ImportFilesAsync(drawerId, clipboard.FilePaths, center, cancellationToken, destination: destination);
        var encoded = clipboard.EncodedImageBytes;
        if (encoded is null && clipboard.SourceGifUri is { } source)
        {
            try { encoded = await OriginalGifDownloadService.DownloadAsync(source, cancellationToken); }
            catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException or IOException)
            { throw new InvalidOperationException("无法读取网页 GIF 原图，请保存原 GIF 文件后拖入画板。", error); }
        }
        if (encoded is not null)
        {
            var asset = await _assets.ImportEncodedAsync(encoded, cancellationToken);
            return await AddAssetsAsync(drawerId, new[] { asset }, center, cancellationToken,
                new[] { clipboard.SourceDescription }, destination);
        }
        if (clipboard.Bitmap is not null) return await ImportBitmapAsync(drawerId, clipboard.Bitmap, center, cancellationToken,
            clipboard.SourceDescription, destination);
        throw new InvalidOperationException(clipboard.ErrorMessage ?? "剪贴板中没有可收集的图片。");
    }

    public async Task<IReadOnlyList<BoardItem>> ImportFilesAsync(
        string drawerId, IEnumerable<string> files, PointF? center = null, CancellationToken cancellationToken = default,
        ImageImportMode mode = ImageImportMode.Copy, bool validateWholeBatch = false,
        ImageImportDestination destination = ImageImportDestination.Board)
    {
        var candidates = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Existing board/clipboard drops ignore non-images. Drawer batches are all-or-nothing.
        var batch = validateWholeBatch || mode == ImageImportMode.Link ? candidates : candidates.Where(_assets.IsSupportedFile).ToArray();
        foreach (var file in batch) AssetPathResolver.ValidateReadableImage(file);
        var imported = new List<ImportedAsset>();
        foreach (var file in batch)
            imported.Add(mode == ImageImportMode.Link ? await _assets.LinkFileAsync(file, cancellationToken)
                : await _assets.ImportFileAsync(file, cancellationToken));
        if (imported.Count == 0) throw new InvalidOperationException("没有可导入的图片文件。");
        return await AddAssetsAsync(drawerId, imported, center, cancellationToken,
            batch.Select(file => Path.GetFileNameWithoutExtension(file)).ToArray(), destination);
    }

    private async Task<IReadOnlyList<BoardItem>> AddAssetsAsync(
        string drawerId, IReadOnlyList<ImportedAsset> assets, PointF? center, CancellationToken cancellationToken,
        IReadOnlyList<string?>? layerNames = null, ImageImportDestination destination = ImageImportDestination.Board)
    {
        var existing = await _repository.GetItemsAsync(drawerId, cancellationToken);
        var textItems = await _repository.GetTextItemsAsync(drawerId, cancellationToken);
        var drawings = await _repository.GetDrawingItemsAsync(drawerId, cancellationToken);
        var z = existing.Cast<BoardElement>().Concat(textItems).Concat(drawings)
            .Select(x => x.ZIndex).DefaultIfEmpty(-1).Max() + 1;
        var origin = center ?? new PointF(0, 0);
        var result = assets.Select((imported, index) =>
        {
            var size = BoardMath.FitSize(imported.Asset.PixelWidth, imported.Asset.PixelHeight);
            var item = new BoardItem
            {
                DrawerId = drawerId,
                AssetId = imported.Asset.Id,
                AssetPath = imported.FullPath,
                Width = size.Width,
                Height = size.Height,
                X = origin.X - (float)(size.Width / 2) + index * 32,
                Y = origin.Y - (float)(size.Height / 2) + index * 32,
                ZIndex = z + index
            };
            var requestedName = layerNames is not null && index < layerNames.Count ? layerNames[index] : null;
            item.LayerName = BoardLayerNameService.Normalize(requestedName);
            if (requestedName is not null && item.LayerName is
                "剪贴板图片" or "网页复制图片" or "GIF 动图" or "网页 GIF 动图" or "复制的图片文件" or "GIF 图片文件")
                item.LayerName = BoardLayerNameService.ClipboardName(item.LayerName, item.CreatedUtc);
            return item;
        }).ToArray();
        if (destination == ImageImportDestination.Materials)
            await _repository.AddMaterialsAsync(result.Select(i => new BoardMaterialItem { Id = i.Id, DrawerId = i.DrawerId,
                AssetId = i.AssetId, AssetPath = i.AssetPath, LayerName = i.LayerName, CreatedUtc = i.CreatedUtc }).ToArray(), cancellationToken);
        else await _repository.AddItemsAsync(result, cancellationToken);
        return new ImageImportBatch(result, destination);
    }
}
