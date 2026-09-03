using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Bitmap = System.Drawing.Bitmap;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static ClipboardImageResult ReadTestClipboard(DataObject data) =>
        (ClipboardImageResult)typeof(ClipboardImageService).GetMethod("ReadDataObject", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { data })!;

    private sealed class NonSeekClipboardStream : MemoryStream
    {
        public NonSeekClipboardStream(byte[] bytes) : base(bytes) { }
        public override bool CanSeek => false;
    }

    private static void GifEncodedClipboardPreservesAnimation()
    {
        var directory = CreateTempDirectory();
        try
        {
            var gif = File.ReadAllBytes(WriteTestGif(directory));
            using var preview = new Bitmap(4, 3);
            using var png = new MemoryStream();
            preview.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            foreach (var format in new[] { "GIF", "image/gif", "FileContents", "PNG" })
            {
                var data = new DataObject();
                data.SetData("image/png", png.ToArray());
                using var payload = new MemoryStream(gif);
                payload.Position = 7;
                data.SetData(format, payload);
                var read = ReadTestClipboard(data);
                using var bitmap = read.Bitmap;
                True(read.EncodedImageBytes is not null && read.EncodedImageBytes.SequenceEqual(gif), $"{format} 原GIF被静态预览覆盖");
                Equal(7L, payload.Position);
                True(read.HasImage, "原始GIF未标记可置入");
            }
            using var gifStream = new MemoryStream(gif);
            using var nativeGif = new Bitmap(gifStream);
            var nativeData = new DataObject(DataFormats.Bitmap, nativeGif);
            var nativeRead = ReadTestClipboard(nativeData);
            using var nativePreview = nativeRead.Bitmap;
            True(nativeRead.EncodedImageBytes is not null, "原生GIF位图对象被直接平面化");
            using var decoded = new MemoryStream(nativeRead.EncodedImageBytes!);
            Equal(4, new System.Windows.Media.Imaging.GifBitmapDecoder(decoded,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames.Count);
            using var nonSeek = new NonSeekClipboardStream(png.ToArray());
            var pngData = new DataObject("PNG", nonSeek);
            var pngRead = ReadTestClipboard(pngData);
            using var pngPreview = pngRead.Bitmap;
            True(pngPreview is not null && pngPreview.Width == 4, "格式探测耗尽了不可回退的PNG剪贴板流");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void GifHtmlClipboardPreservesAnimation()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = WriteTestGif(directory);
            var gif = File.ReadAllBytes(path);
            using var preview = new Bitmap(4, 3);
            var embedded = new DataObject(DataFormats.Bitmap, preview);
            embedded.SetData(DataFormats.Html, $"<img src='data:image/gif;base64,{Convert.ToBase64String(gif)}'>");
            var read = ReadTestClipboard(embedded);
            using var bitmap = read.Bitmap;
            True(read.EncodedImageBytes!.SequenceEqual(gif), "网页内嵌GIF未优先于静态位图");
            var remote = new DataObject(DataFormats.Bitmap, preview);
            remote.SetData(DataFormats.Html, "SourceURL:https://example.com/images/page\r\n<img src='../original.gif?a=1&amp;b=2'>");
            var remoteRead = ReadTestClipboard(remote);
            using var remoteBitmap = remoteRead.Bitmap;
            Equal("https://example.com/original.gif?a=1&b=2", remoteRead.SourceGifUri!.AbsoluteUri);
            var local = new DataObject();
            local.SetData(DataFormats.Html, $"<img src='{new Uri(path).AbsoluteUri}'>");
            var localRead = ReadTestClipboard(local);
            using var localBitmap = localRead.Bitmap;
            Equal(path, localRead.FilePaths.Single());
            var unsafeData = new DataObject();
            unsafeData.SetData(DataFormats.Html, "<img src='javascript:evil.gif'>");
            var unsafeRead = ReadTestClipboard(unsafeData);
            using var unsafeBitmap = unsafeRead.Bitmap;
            True(unsafeRead.SourceGifUri is null, "接受了非HTTP网页图片地址");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void GifContentDetectionAndLegacyAssets()
    {
        var directory = CreateTempDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            var repository = new BoardRepository(paths);
            repository.InitializeAsync().GetAwaiter().GetResult();
            var assets = new AssetLibraryService(paths, repository);
            var source = WriteTestGif(directory);
            var bytes = File.ReadAllBytes(source);
            var disguised = Path.Combine(directory, "not-really-a-png.png");
            File.Copy(source, disguised);
            True(GifAnimationService.IsGif(disguised), "真实GIF因PNG后缀未被识别");
            var imported = assets.ImportFileAsync(disguised).GetAwaiter().GetResult();
            Equal(".gif", imported.Asset.Extension);
            True(File.ReadAllBytes(imported.FullPath).SequenceEqual(bytes), "导入重写了GIF帧数据");
            var raw = assets.ImportEncodedAsync(bytes).GetAwaiter().GetResult();
            Equal(imported.Asset.Id, raw.Asset.Id);
            // Simulate a pre-fix asset with correct GIF bytes but the wrong suffix.
            var legacyPath = Path.Combine(paths.Assets, "legacy.png");
            File.Copy(source, legacyPath);
            var legacyId = Guid.NewGuid().ToString("N");
            var legacy = new AssetRecord(legacyId, "legacy-fixture", ".png", "legacy.png", 4, 3, DateTime.UtcNow);
            repository.UpsertAssetAsync(legacy).GetAwaiter().GetResult();
            repository.AddItemsAsync(new[] { new BoardItem { AssetId = legacyId, DrawerId = "A" } }).GetAwaiter().GetResult();
            var restored = repository.GetItemsAsync("A").GetAwaiter().GetResult().Single();
            True(GifAnimationService.IsGif(restored.AssetPath), "历史资源按后缀错误分流到静态图");
            Equal(4, GifAnimationService.LoadAsync(restored.AssetPath).GetAwaiter().GetResult().Frames.Count);
            using var staticImage = new Bitmap(10, 10);
            staticImage.Save(disguised, System.Drawing.Imaging.ImageFormat.Png);
            True(!GifAnimationService.IsGif(disguised), "格式缓存未随文件变化更新");
            var fakeGif = Path.Combine(directory, "static.gif");
            staticImage.Save(fakeGif, System.Drawing.Imaging.ImageFormat.Png);
            True(!GifAnimationService.IsGif(fakeGif), "仅因GIF后缀误报静态图");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class GifClipboardStub : IClipboardImageService
    {
        private readonly byte[] _bytes;
        public GifClipboardStub(byte[] bytes) => _bytes = bytes;
        public ClipboardImageResult ReadImage()
        {
            var data = new DataObject();
            data.SetData("image/gif", _bytes);
            return ReadTestClipboard(data);
        }
        public ClipboardClearResult Clear() => new(true, null);
    }

    private static void GifClipboardToBoardToolbar() => WithDrawingBoard((window, _) =>
    {
        var staticItem = AddEditableImage(window);
        var path = WriteTestGif(Path.GetDirectoryName(staticItem.AssetPath)!);
        var bytes = File.ReadAllBytes(path);
        typeof(BoardWindow).GetField("_clipboard", PrivateInstance)!.SetValue(window, new GifClipboardStub(bytes));
        AwaitImageUiTask(window, "PasteAsync");
        var gifItem = LiveImages(window).Single(item => item.Id != staticItem.Id);
        True(GifAnimationService.IsGif(gifItem.AssetPath), "实际粘贴流程仍保存为PNG");
        True(File.ReadAllBytes(gifItem.AssetPath).SequenceEqual(bytes), "实际粘贴流程丢失动画编码");
        var image = ((Grid)DrawingBorder(window, gifItem.Id).Child).Children.OfType<Image>().Single();
        AwaitImageUiTask(window, "AttachGifAsync", gifItem, image);
        BoardSelection(window).Clear(); BoardSelection(window).Add(gifItem.Id);
        CallDrawing(window, "UpdateSelectionVisuals");
        ((Button)window.FindName("ImageEditToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(Visibility.Visible, ((StackPanel)window.FindName("GifImageTools")).Visibility);
        Equal(Visibility.Collapsed, ((StackPanel)window.FindName("StaticImageTools")).Visibility);
        var playback = (GifPlaybackState)CallDrawing(window, "SelectedGif")!;
        Equal(4, playback.Animation.Frames.Count);
        True(playback.IsPlaying, "粘贴后未自动播放");
        True(playback.Advance(100), "粘贴GIF没有推进帧");
        Equal(1, playback.FrameIndex);
        True(BoardSurfaceEnabled(window), "导入后画板未恢复交互");
        AwaitImageUiTask(window, "UndoAsync");
        Equal(1, LiveImages(window).Count);
        AwaitImageUiTask(window, "RedoAsync");
        Equal(2, LiveImages(window).Count);
    });

    private sealed class GifHttpStub : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        public GifHttpStub(byte[] bytes) => _bytes = bytes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) });
    }
    private static void GifOriginalDownloadValidation()
    {
        var directory = CreateTempDirectory();
        try
        {
            var bytes = File.ReadAllBytes(WriteTestGif(directory));
            var method = typeof(OriginalGifDownloadService).GetMethod("DownloadAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            using var client = new HttpClient(new GifHttpStub(bytes));
            var task = (Task<byte[]>)method.Invoke(null, new object[] { new Uri("https://example.com/original.gif"), client, CancellationToken.None })!;
            True(task.GetAwaiter().GetResult().SequenceEqual(bytes), "原图下载修改了GIF内容");
            using var badClient = new HttpClient(new GifHttpStub(System.Text.Encoding.ASCII.GetBytes("not a gif")));
            var rejected = false;
            try
            {
                ((Task<byte[]>)method.Invoke(null, new object[] { new Uri("https://example.com/original.gif"), badClient, CancellationToken.None })!)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidDataException) { rejected = true; }
            True(rejected, "网页静态/错误响应被伪装成GIF");
        }
        finally { Directory.Delete(directory, true); }
    }
}
