using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace ScreenshotCollector.Services;

public sealed partial class ClipboardImageService : IClipboardImageService
{
    private static readonly string[] EncodedImageFormats =
    {
        "PNG",
        "image/png",
        "JFIF",
        "image/jpeg"
    };

    public ClipboardImageResult ReadImage()
    {
        try
        {
            var dataObject = GetDataObjectWithRetry();
            if (dataObject is null)
            {
                return new ClipboardImageResult(null, null, "剪贴板中没有可读取的内容。");
            }
            return ReadDataObject(dataObject);
        }
        catch (ExternalException)
        {
            return new ClipboardImageResult(null, null, "剪贴板暂时被其他程序占用，请重试。");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or FormatException or RegexMatchTimeoutException)
        {
            return new ClipboardImageResult(null, null, $"无法读取剪贴板图片：{exception.Message}");
        }
    }

    internal static ClipboardImageResult ReadDataObject(System.Windows.IDataObject dataObject)
    {
            var copiedFiles = GetImageFilePaths(dataObject);
            if (copiedFiles.Count > 0)
                return new ClipboardImageResult(TryReadImageFile(dataObject), "复制的图片文件", null) { FilePaths = copiedFiles };

            var gif = TryReadOriginalGif(dataObject, out var encodedCopies);
            if (gif is not null) return gif;
            var sourceGif = ReadHtmlGifUri(dataObject);
            foreach (var format in EncodedImageFormats)
            {
                var encodedImage = encodedCopies.TryGetValue(format, out var copy) ? DecodeImage(copy) : TryReadEncodedFormat(dataObject, format);
                if (encodedImage is not null)
                {
                    return new ClipboardImageResult(encodedImage, "剪贴板图片", null) { SourceGifUri = sourceGif };
                }
            }

            var bitmapImage = TryReadBitmap(dataObject);
            if (bitmapImage is not null)
            {
                return new ClipboardImageResult(bitmapImage, "剪贴板图片", null) { SourceGifUri = sourceGif };
            }

            var copiedFile = TryReadImageFile(dataObject);
            if (copiedFile is not null)
            {
                return new ClipboardImageResult(copiedFile, "复制的图片文件", null);
            }

            var embeddedImage = TryReadHtmlDataImage(dataObject);
            if (embeddedImage is not null)
            {
                return new ClipboardImageResult(embeddedImage, "网页复制图片", null) { SourceGifUri = sourceGif };
            }

            return new ClipboardImageResult(
                null,
                null,
                sourceGif is null ? "剪贴板中没有图片。请先截图或在网页中选择“复制图片”。" : null) { SourceGifUri = sourceGif };
    }

    internal static IReadOnlyList<string> GetImageFilePaths(System.Windows.IDataObject dataObject) =>
        dataObject.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
        dataObject.GetData(System.Windows.DataFormats.FileDrop) is string[] files
            ? files.Where(path => ImageFileFormatService.FromFile(path) is not null).ToArray()
            : Array.Empty<string>();

    public ClipboardClearResult Clear()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                System.Windows.Clipboard.Clear();
                return new ClipboardClearResult(true, null);
            }
            catch (ExternalException) when (attempt < 3)
            {
                Thread.Sleep(25);
            }
            catch (ExternalException)
            {
                return new ClipboardClearResult(false, "剪贴板正被其他程序占用，请稍后重试。");
            }
        }
        return new ClipboardClearResult(false, "无法清除剪贴板图片。");
    }

    private static System.Windows.IDataObject? GetDataObjectWithRetry()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return System.Windows.Clipboard.GetDataObject();
            }
            catch (ExternalException) when (attempt < 3)
            {
                Thread.Sleep(25);
            }
        }

        return System.Windows.Clipboard.GetDataObject();
    }

    private static Bitmap? TryReadEncodedFormat(System.Windows.IDataObject dataObject, string format)
    {
        if (!dataObject.GetDataPresent(format))
        {
            return null;
        }

        var data = dataObject.GetData(format, autoConvert: true);
        return data switch
        {
            byte[] bytes => DecodeImage(bytes),
            MemoryStream stream => DecodeImage(stream.ToArray()),
            Stream stream => DecodeStream(stream),
            Bitmap bitmap => new Bitmap(bitmap),
            BitmapSource bitmapSource => ConvertBitmapSource(bitmapSource),
            _ => null
        };
    }

    private static Bitmap? TryReadBitmap(System.Windows.IDataObject dataObject)
    {
        if (dataObject.GetDataPresent(System.Windows.DataFormats.Bitmap, autoConvert: true))
        {
            var data = dataObject.GetData(System.Windows.DataFormats.Bitmap, autoConvert: true);
            if (data is Bitmap bitmap)
            {
                return new Bitmap(bitmap);
            }

            if (data is BitmapSource bitmapSource)
            {
                return ConvertBitmapSource(bitmapSource);
            }
        }

        return null;
    }

    private static Bitmap? TryReadImageFile(System.Windows.IDataObject dataObject)
    {
        if (!dataObject.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return null;
        }

        if (dataObject.GetData(System.Windows.DataFormats.FileDrop) is not string[] files)
        {
            return null;
        }

        var imagePath = files.FirstOrDefault(file =>
            ImageFileFormatService.FromFile(file) is not null);
        if (imagePath is null)
        {
            return null;
        }

        using var loadedImage = new Bitmap(imagePath);
        return new Bitmap(loadedImage);
    }

    private static Bitmap? TryReadHtmlDataImage(System.Windows.IDataObject dataObject)
    {
        if (!dataObject.GetDataPresent(System.Windows.DataFormats.Html))
        {
            return null;
        }

        var html = dataObject.GetData(System.Windows.DataFormats.Html) as string;
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = DataImageRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        return DecodeImage(Convert.FromBase64String(match.Groups[1].Value));
    }

    private static Bitmap DecodeStream(Stream stream)
    {
        using var copy = new MemoryStream();
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }
        stream.CopyTo(copy);
        return DecodeImage(copy.ToArray());
    }

    private static Bitmap DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }

    private static Bitmap ConvertBitmapSource(BitmapSource bitmapSource)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return DecodeImage(stream.ToArray());
    }

    [GeneratedRegex(
        "data:image/(?:png|jpe?g|gif|bmp);base64,([A-Za-z0-9+/=]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataImageRegex();
}
