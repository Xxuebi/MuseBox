using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenshotCollector.Services;

/// <summary>Clipboard representations are based on the original asset, never the board preview.</summary>
public static class BoardClipboardService
{
    public static DataObject CreateDataObject(string assetPath, string? cacheDirectory = null)
    {
        if (ImageFileFormatService.FromFile(assetPath) != ".gif")
        {
            using var bitmap = new System.Drawing.Bitmap(assetPath);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream; image.EndInit(); image.Freeze();
            var result = new DataObject(); result.SetImage(image); return result;
        }

        if (new FileInfo(assetPath).Length > 128L * 1024 * 1024)
            throw new IOException("GIF 过大，请通过导出图像保存原文件。");
        var bytes = File.ReadAllBytes(assetPath);
        if (ImageFileFormatService.FromHeader(bytes) != ".gif")
            throw new IOException("图片资源已改变，请刷新后重新复制。");
        var directory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "MuseBox", "Clipboard");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".gif");
        if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes))
        {
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        var data = new DataObject();
        // Do not offer PNG/DIB: many destinations prefer that flattened preview over GIF.
        data.SetData("GIF", new MemoryStream(bytes, writable: false), false);
        data.SetData("image/gif", new MemoryStream(bytes, writable: false), false);
        data.SetFileDropList(new StringCollection { path });
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)), false);
        data.SetData(DataFormats.Html, CreateHtml(new Uri(Path.GetFullPath(path)).AbsoluteUri));
        return data;
    }

    private static string CreateHtml(string uri)
    {
        const string format = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        var fragment = "<img src=\"" + WebUtility.HtmlEncode(uri) + "\" />";
        var start = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture,format,0,0,0,0));
        var startFragment = start + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var end = endFragment + Encoding.UTF8.GetByteCount(after);
        return string.Format(CultureInfo.InvariantCulture,format,start,end,startFragment,endFragment) + before + fragment + after;
    }
}

