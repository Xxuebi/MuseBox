using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed class AssetLibraryService
{
    private readonly AppDataPaths _paths;
    private readonly IBoardRepository _repository;

    public AssetLibraryService(AppDataPaths paths, IBoardRepository repository)
    {
        _paths = paths;
        _repository = repository;
    }

    public bool IsSupportedFile(string path) =>
        ImageFileFormatService.FromFile(path) is not null;

    public async Task<ImportedAsset> ImportEncodedAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        var extension = ImageFileFormatService.FromHeader(bytes) ?? throw new InvalidDataException("无法识别图片的原始格式。");
        var temporary = Path.Combine(_paths.Root, $"import.{Guid.NewGuid():N}{extension}");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            return await ImportFileCoreAsync(temporary, extension, cancellationToken);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    public async Task<ImportedAsset> ImportBitmapAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(_paths.Root, $"import.{Guid.NewGuid():N}.png");
        try
        {
            bitmap.Save(temporary, ImageFormat.Png);
            return await ImportFileCoreAsync(temporary, ".png", cancellationToken);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public Task<ImportedAsset> ImportFileAsync(string path, CancellationToken cancellationToken = default) =>
        ImportFileCoreAsync(path, null, cancellationToken);

    private async Task<ImportedAsset> ImportFileCoreAsync(
        string path, string? forcedExtension, CancellationToken cancellationToken)
    {
        if (!IsSupportedFile(path) && forcedExtension is null)
            throw new InvalidOperationException($"不支持的图片格式：{Path.GetExtension(path)}");

        string hash;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }

        var existing = await _repository.FindAssetByHashAsync(hash, cancellationToken);
        if (existing is not null)
            return new ImportedAsset(existing, Path.Combine(_paths.Assets, existing.FileName));

        int width;
        int height;
        using (var image = Image.FromFile(path))
        {
            width = image.Width;
            height = image.Height;
        }

        var extension = ImageFileFormatService.FromFile(path) ?? (forcedExtension ?? Path.GetExtension(path)).ToLowerInvariant();
        if (extension == ".jpeg") extension = ".jpg";
        var fileName = $"{hash}{extension}";
        var finalPath = Path.Combine(_paths.Assets, fileName);
        var temporaryPath = Path.Combine(_paths.Assets, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (!File.Exists(finalPath))
            {
                File.Copy(path, temporaryPath, overwrite: false);
                File.Move(temporaryPath, finalPath, overwrite: false);
            }
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }

        var asset = new AssetRecord(
            Guid.NewGuid().ToString("N"), hash, extension, fileName, width, height, DateTime.UtcNow);
        await _repository.UpsertAssetAsync(asset, cancellationToken);
        var canonical = await _repository.FindAssetByHashAsync(hash, cancellationToken) ?? asset;
        return new ImportedAsset(canonical, Path.Combine(_paths.Assets, canonical.FileName));
    }
}
