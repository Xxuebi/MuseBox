using System.Text.Json;
using ScreenshotCollector.Models;
namespace ScreenshotCollector.Services;

public static class LinkedSceneSaveService
{
    public static async Task<bool> SaveAsync(IBoardRepository repository, BoardImportService imports,
        string drawerId, string path, SceneSnapshot snapshot, ExternalImageSaveMode mode,
        string? expectedHash, CancellationToken token = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MuseBox-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stagedFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".MuseBox-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var document = JsonSerializer.Deserialize<SceneDocument>(JsonSerializer.Serialize(snapshot.Document))!;
            var paths = new Dictionary<string, string>();
            var converted = new Dictionary<string, string>();
            var assets = new Dictionary<string, SceneAsset>();
            foreach (var source in document.Assets)
            {
                token.ThrowIfCancellationRequested();
                var asset = source;
                var original = snapshot.AssetPaths[source.Id];
                var frozen = Path.Combine(directory, source.Id + source.Extension);
                try
                {
                    if (source.SourceKind == AssetSourceKind.External) AssetPathResolver.ValidateReadableImage(original);
                    // FileShare.Read prevents rewriting during this snapshot; replacement afterwards is harmless.
                    await using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    await using (var output = new FileStream(frozen, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                        await input.CopyToAsync(output, token);
                    AssetPathResolver.ValidateReadableImage(frozen);
                    if (source.SourceKind == AssetSourceKind.External)
                    {
                        using var image = System.Drawing.Image.FromFile(frozen);
                        asset = source with { Hash = await SceneFileService.HashFileAsync(frozen, token),
                            Width = image.Width, Height = image.Height, Extension = ImageFileFormatService.FromFile(frozen)! };
                    }
                }
                catch (Exception e) when (source.SourceKind == AssetSourceKind.External && mode == ExternalImageSaveMode.KeepLinks &&
                    e is IOException or UnauthorizedAccessException)
                {
                    // Missing links remain valid scene references. Thumbnail uses its placeholder.
                    if (File.Exists(frozen)) File.Delete(frozen);
                }
                if (source.SourceKind == AssetSourceKind.External && mode == ExternalImageSaveMode.Copy)
                {
                    var imported = await imports.StageInternalAssetAsync(frozen, token);
                    converted.Add(source.Id, imported.Asset.Id);
                    asset = new SceneAsset(imported.Asset.Id, imported.Asset.Hash, imported.Asset.Extension,
                        imported.Asset.PixelWidth, imported.Asset.PixelHeight);
                }
                assets[asset.Id] = asset;
                paths[asset.Id] = frozen;
            }
            foreach (var image in document.Images)
                if (converted.TryGetValue(image.AssetId, out var replacement)) image.AssetId = replacement;
            foreach (var material in document.Materials)
                if (converted.TryGetValue(material.AssetId, out var replacement)) material.AssetId = replacement;
            document.Assets = assets.Values.ToList();
            SceneGifStateService.PrepareLocalSnapshot(document, paths);
            var frozenSnapshot = new SceneSnapshot(document, paths, snapshot.Revision);
            var hash = await SceneFileService.WriteAsync(stagedFile, frozenSnapshot, token: token);
            await repository.CommitSceneSaveAsync(drawerId, Path.GetFullPath(path), stagedFile, hash,
                snapshot.Revision, converted, expectedHash, token);
            return converted.Count > 0;
        }
        finally
        {
            try { if (File.Exists(stagedFile)) File.Delete(stagedFile); } catch (IOException) { }
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}

