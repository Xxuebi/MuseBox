using System.Collections.Concurrent;

namespace ScreenshotCollector.Services;

public static class ImageFileFormatService
{
    private sealed record CachedFormat(long Length, long Modified, string? Extension);
    private static readonly ConcurrentDictionary<string, CachedFormat> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? FromHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return ".gif";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return ".jpg";
        if (bytes.Length >= 2 && bytes[0] == 66 && bytes[1] == 77) return ".bmp";
        if (bytes.Length >= 4 && (bytes[..4].SequenceEqual(new byte[] { 73,73,42,0 }) ||
                                 bytes[..4].SequenceEqual(new byte[] { 77,77,0,42 }))) return ".tiff";
        return null;
    }

    public static string? FromFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (Cache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc.Ticks)
                return cached.Extension;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[8];
            var length = stream.Read(header);
            var extension = FromHeader(header[..length]);
            if (Cache.Count > 4096) Cache.Clear();
            Cache[path] = new CachedFormat(info.Length, info.LastWriteTimeUtc.Ticks, extension);
            return extension;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
}
