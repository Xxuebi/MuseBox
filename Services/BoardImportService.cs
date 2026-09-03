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
        string? layerName = null)
    {
        var imported = await _assets.ImportBitmapAsync(bitmap, cancellationToken);
        return await AddAssetsAsync(drawerId, new[] { imported }, center, cancellationToken,
            new[] { layerName ?? "剪贴板图片" });
    }

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
        PointF? center = null, CancellationToken cancellationToken = default)
    {
        if (clipboard.FilePaths.Count > 0)
            return await ImportFilesAsync(drawerId, clipboard.FilePaths, center, cancellationToken);
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
                new[] { clipboard.SourceDescription });
        }
        if (clipboard.Bitmap is not null) return await ImportBitmapAsync(drawerId, clipboard.Bitmap, center, cancellationToken,
            clipboard.SourceDescription);
        throw new InvalidOperationException(clipboard.ErrorMessage ?? "剪贴板中没有可收集的图片。");
    }

    public async Task<IReadOnlyList<BoardItem>> ImportFilesAsync(
        string drawerId, IEnumerable<string> files, PointF? center = null, CancellationToken cancellationToken = default)
    {
        var imported = new List<ImportedAsset>();
        foreach (var file in files.Where(_assets.IsSupportedFile))
            imported.Add(await _assets.ImportFileAsync(file, cancellationToken));
        if (imported.Count == 0) throw new InvalidOperationException("没有可导入的图片文件。");
        return await AddAssetsAsync(drawerId, imported, center, cancellationToken,
            files.Where(_assets.IsSupportedFile).Select(file => Path.GetFileNameWithoutExtension(file)).ToArray());
    }

    private async Task<IReadOnlyList<BoardItem>> AddAssetsAsync(
        string drawerId, IReadOnlyList<ImportedAsset> assets, PointF? center, CancellationToken cancellationToken,
        IReadOnlyList<string?>? layerNames = null)
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
        await _repository.AddItemsAsync(result, cancellationToken);
        return result;
    }
}
