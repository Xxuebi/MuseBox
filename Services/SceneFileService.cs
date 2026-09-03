using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed class SceneFileConflictException : IOException
{
    public SceneFileConflictException() : base("场景文件已被其他程序修改或移走，未覆盖原文件。") { }
}

public static class SceneFileService
{
    public const string Extension = ".mubo";
    public const string LegacyExtension = ".iscene";
    public static bool IsSupportedExtension(string? extension) =>
        string.Equals(extension, Extension, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, LegacyExtension, StringComparison.OrdinalIgnoreCase);
    private const long MaxManifestBytes = 64L * 1024 * 1024;
    private const long MaxAssetBytes = 512L * 1024 * 1024;
    private const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxThumbnailBytes = 16L * 1024 * 1024;
    public static async Task<string> HashFileAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
    private static string AssetEntry(SceneAsset asset) => $"assets/{asset.Hash}{asset.Extension}";

    public static async Task<PreparedScene> ReadAsync(string path, CancellationToken token = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MuseBox-scene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Deny writers for the entire hash/read operation, not just the header.
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
            input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
            if (zip.Entries.Count > SceneValidation.MaxAssets + 1) throw new InvalidDataException("场景文件数量超出安全上限。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (!names.Add(entry.FullName) || entry.Length < 0 || entry.Length > MaxAssetBytes ||
                    (total = checked(total + entry.Length)) > MaxTotalBytes)
                    throw new InvalidDataException("场景包含重复文件或解压数据超出安全上限。");
            }
            var manifest = zip.GetEntry("scene.json") ?? throw new InvalidDataException("文件不是有效的 .mubo 场景。");
            if (manifest.Length > MaxManifestBytes) throw new InvalidDataException("场景描述过大。");
            using var json = new MemoryStream();
            await using (var source = manifest.Open()) await CopyBoundedAsync(source, json, manifest.Length, null, token);
            json.Position = 0;
            var document = await JsonSerializer.DeserializeAsync<SceneDocument>(json, cancellationToken: token)
                ?? throw new InvalidDataException("场景描述为空。");
            SceneMigration.UpgradeToCurrent(document);
            ValidateThumbnail(document.ThumbnailPng);
            SceneValidation.Validate(document);
            var allowed = document.Assets.Select(AssetEntry).Append("scene.json").ToHashSet(StringComparer.Ordinal);
            if (!allowed.SetEquals(zip.Entries.Select(e => e.FullName))) throw new InvalidDataException("场景包含非法路径、多余文件或缺少图片。");
            var paths = new Dictionary<string, string>();
            foreach (var asset in document.Assets)
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(AssetEntry(asset))!;
                // Only generated, validated hash names reach the filesystem.
                var outputPath = Path.Combine(directory, asset.Hash + asset.Extension);
                await using (var source = entry.Open())
                await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await CopyBoundedAsync(source, output, entry.Length, asset.Hash, token);
                ValidateImage(outputPath, asset);
                paths.Add(asset.Id, outputPath);
            }
            return new PreparedScene(document, paths, hash, directory);
        }
        catch
        {
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static async Task<string> WriteAsync(string path, SceneSnapshot snapshot, string? expectedFileHash = null, CancellationToken token = default)
    {
        SceneMigration.UpgradeToCurrent(snapshot.Document);
        var thumbnail = SceneThumbnailRenderer.Render(snapshot);
        if (thumbnail.LongLength > MaxThumbnailBytes) throw new InvalidDataException("场景缩略图过大。");
        snapshot.Document.ThumbnailPng = Convert.ToBase64String(thumbnail);
        SceneValidation.Validate(snapshot.Document);
        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("目标文件夹不存在，请另存到可用位置。");
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await AssertUnchangedAsync(path, expectedFileHash, token);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    await using (var manifest = zip.CreateEntry("scene.json", CompressionLevel.Optimal).Open())
                        await JsonSerializer.SerializeAsync(manifest, snapshot.Document, cancellationToken: token);
                    long total = 0;
                    foreach (var asset in snapshot.Document.Assets)
                    {
                        token.ThrowIfCancellationRequested();
                        var sourcePath = snapshot.AssetPaths[asset.Id];
                        ValidateImage(sourcePath, asset);
                        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                        if (source.Length > MaxAssetBytes || (total += source.Length) > MaxTotalBytes - MaxManifestBytes)
                            throw new InvalidDataException("场景资源超过单图 512MB 或总计 4GB 的安全上限。");
                        await using var output = zip.CreateEntry(AssetEntry(asset), CompressionLevel.Fastest).Open();
                        await CopyBoundedAsync(source, output, source.Length, asset.Hash, token);
                    }
                }
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            // Verify central-directory completeness before publishing the new file.
            using (var check = ZipFile.OpenRead(temporary))
                if (check.Entries.Count != snapshot.Document.Assets.Count + 1 ||
                    check.GetEntry("scene.json")!.Length > MaxManifestBytes)
                    throw new InvalidDataException("场景写入验证失败。");
            var hash = await HashFileAsync(temporary, token);
            await AssertUnchangedAsync(path, expectedFileHash, token);
            token.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            return hash;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task AssertUnchangedAsync(string path, string? expected, CancellationToken token)
    {
        if (expected is not null && (!File.Exists(path) || await HashFileAsync(path, token) != expected))
            throw new SceneFileConflictException();
    }
    private static async Task CopyBoundedAsync(Stream source, Stream destination, long expectedLength, string? hash, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            length += read;
            if (length > expectedLength) throw new InvalidDataException("场景解压长度异常。");
            digest.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (length != expectedLength || hash is not null && Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() != hash)
            throw new InvalidDataException("图片资源不完整或校验不符。");
    }
    private static void ValidateImage(string path, SceneAsset asset)
    {
        var format = ImageFileFormatService.FromFile(path);
        if (format != asset.Extension && !(format == ".tiff" && asset.Extension == ".tif"))
            throw new InvalidDataException("图片真实格式与场景描述不符。");
        using var image = System.Drawing.Image.FromFile(path);
        if (image.Width != asset.Width || image.Height != asset.Height || (long)image.Width * image.Height > 100_000_000)
            throw new InvalidDataException("图片实际尺寸与场景描述不符。");
    }

    private static void ValidateThumbnail(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return;
        if (encoded.Length > (MaxThumbnailBytes * 4 / 3) + 8)
            throw new InvalidDataException("场景缩略图大小无效。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new InvalidDataException("场景缩略图编码无效。"); }
        if (bytes.Length < 24 || bytes.LongLength > MaxThumbnailBytes)
            throw new InvalidDataException("场景缩略图大小无效。");
        ReadOnlySpan<byte> header = bytes.AsSpan(0, 24);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (!header[..8].SequenceEqual(signature) ||
            width is < 16 or > 2048 || height is < 16 or > 2048)
            throw new InvalidDataException("场景缩略图不是安全的 PNG。");
    }
}
