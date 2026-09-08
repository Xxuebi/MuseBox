using System.Security.Cryptography;
using System.Text;
using ScreenshotCollector.Models;
namespace ScreenshotCollector.Services;

public static class AssetPathResolver
{
    // Legacy UNIQUE hash stores a namespaced identity; content_hash stores the image digest.
    public static string Identity(AssetRecord asset) => asset.SourceKind == AssetSourceKind.External
        ? ExternalIdentity(asset.FileName) : asset.Hash;
    public static string ExternalIdentity(string path) => "link:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeExternalPath(path).ToUpperInvariant()))).ToLowerInvariant();
    public static string Resolve(string directory, AssetRecord asset) => Resolve(directory, asset.FileName, asset.SourceKind);
    public static string Resolve(string directory, string filename, AssetSourceKind kind)
    {
        if (kind == AssetSourceKind.External) return NormalizeExternalPath(filename);
        if (kind != AssetSourceKind.Internal || Path.GetFileName(filename) != filename)
            throw new InvalidDataException("内部资源路径无效。");
        return Path.Combine(directory, filename);
    }
    public static string NormalizeExternalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") || path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl))
            throw new InvalidDataException("外部图片必须使用普通文件的绝对路径。");
        var full = Path.GetFullPath(path);
        if (!new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(Path.GetExtension(full).ToLowerInvariant()))
            throw new InvalidDataException("外部路径不是支持的图片文件。");
        return full;
    }
    public static void ValidateReadableImage(string path)
    {
        try
        {
            if (ImageFileFormatService.FromFile(path) is null) throw new IOException("无法识别图片格式。");
            using var image = System.Drawing.Image.FromFile(path);
            if ((long)image.Width * image.Height > 100_000_000) throw new IOException("图片尺寸超出安全上限。");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        { throw new IOException($"图片缺失或无法读取：{path}", e); }
    }
}

