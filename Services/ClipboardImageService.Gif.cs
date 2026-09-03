using System.Net;
using System.Text.RegularExpressions;

namespace ScreenshotCollector.Services;

public sealed partial class ClipboardImageService
{
    private const int MaximumEncodedSize = 128 * 1024 * 1024;

    private static ClipboardImageResult? TryReadOriginalGif(System.Windows.IDataObject data, out Dictionary<string, byte[]> encodedCopies)
    {
        encodedCopies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Prefer the original encoded payload over a simultaneously offered PNG
        // or DIB preview. QQ/other apps may expose it as FileContents.
        foreach (var format in new[] { "GIF", "image/gif", "FileContents", "application/x-qt-image", "PNG", "image/png", System.Windows.DataFormats.Bitmap })
        {
            try
            {
                if (!data.GetDataPresent(format, autoConvert: false)) continue;
                var bytes = ReadEncodedBytes(data.GetData(format, autoConvert: false));
                if (bytes is not null) encodedCopies[format] = bytes;
                if (bytes is not null && ImageFileFormatService.FromHeader(bytes) == ".gif")
                    return new ClipboardImageResult(DecodeImage(bytes), "GIF 动图", null) { EncodedImageBytes = bytes };
            }
            catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            { /* A failed optional format must not hide another usable representation. */ }
        }
        var html = ReadHtml(data);
        var match = GifDataRegex().Match(html);
        if (match.Success && match.Groups[1].Length <= MaximumEncodedSize * 4L / 3 + 4)
        {
            try
            {
                var bytes = Convert.FromBase64String(match.Groups[1].Value);
                if (ImageFileFormatService.FromHeader(bytes) == ".gif")
                    return new ClipboardImageResult(DecodeImage(bytes), "网页 GIF 动图", null) { EncodedImageBytes = bytes };
            }
            catch (Exception error) when (error is FormatException or ArgumentException) { }
        }
        var imageSource = HtmlImageSourceRegex().Match(html);
        if (imageSource.Success && Uri.TryCreate(WebUtility.HtmlDecode(imageSource.Groups["src"].Value), UriKind.Absolute, out var fileUri) &&
            fileUri.IsFile && ImageFileFormatService.FromFile(fileUri.LocalPath) == ".gif")
        {
            using var original = new System.Drawing.Bitmap(fileUri.LocalPath);
            return new ClipboardImageResult(new System.Drawing.Bitmap(original), "GIF 图片文件", null) { FilePaths = new[] { fileUri.LocalPath } };
        }
        return null;
    }

    private static byte[]? ReadEncodedBytes(object? payload)
    {
        if (payload is byte[] bytes) return bytes.Length <= MaximumEncodedSize ? bytes : null;
        if (payload is System.Drawing.Image image && image.RawFormat.Guid == System.Drawing.Imaging.ImageFormat.Gif.Guid)
        {
            using var copy = new MemoryStream();
            image.Save(copy, System.Drawing.Imaging.ImageFormat.Gif);
            return copy.Length <= MaximumEncodedSize ? copy.ToArray() : null;
        }
        if (payload is not Stream stream || !stream.CanRead) return null;
        if (stream.CanSeek && stream.Length > MaximumEncodedSize) return null;
        var position = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (copy.Length + count > MaximumEncodedSize) return null;
                copy.Write(buffer, 0, count);
            }
            return copy.ToArray();
        }
        finally { if (stream.CanSeek) stream.Position = position; }
    }

    private static string ReadHtml(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(System.Windows.DataFormats.Html, false)) return "";
        return data.GetData(System.Windows.DataFormats.Html, false) switch
        {
            string html => html,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => ""
        };
    }

    private static Uri? ReadHtmlGifUri(System.Windows.IDataObject data)
    {
        var html = ReadHtml(data);
        var match = HtmlImageSourceRegex().Match(html);
        if (!match.Success) return null;
        var text = WebUtility.HtmlDecode(match.Groups["src"].Value);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var source = HtmlSourceUrlRegex().Match(html);
            if (!source.Success || !Uri.TryCreate(source.Groups[1].Value.Trim(), UriKind.Absolute, out var page) ||
                !Uri.TryCreate(page, text, out uri)) return null;
        }
        return uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
            uri.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? uri : null;
    }

    [GeneratedRegex("data:image/gif;base64,([A-Za-z0-9+/=\\r\\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex GifDataRegex();
    [GeneratedRegex("<img\\b[^>]*\\bsrc\\s*=\\s*(?:\"(?<src>[^\"]*)\"|'(?<src>[^']*)'|(?<src>[^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HtmlImageSourceRegex();
    [GeneratedRegex("^SourceURL:([^\\r\\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HtmlSourceUrlRegex();
}
