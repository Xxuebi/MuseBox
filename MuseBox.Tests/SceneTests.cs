using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Bitmap = System.Drawing.Bitmap;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static (BoardRepository Repository, BoardImportService Imports) SceneRepository(string root)
    {
        var paths = new AppDataPaths(root);
        var repository = new BoardRepository(paths);
        repository.InitializeAsync().GetAwaiter().GetResult();
        return (repository, new BoardImportService(new AssetLibraryService(paths, repository), repository));
    }
    private static void ConfigureSceneGroup(BoardElement element)
    {
        element.GroupId = "group1";
        element.GroupBackgroundColor = "#80445566";
        element.GroupBorderColor = "#FF12A0E0";
        element.GroupBorderThickness = 40;
        element.GroupFramePadding = 180;
        element.GroupBackgroundVisible = true;
        element.GroupLocked = false;
        element.GroupAutoMembership = true;
    }
    private static SceneSnapshot PopulateScene(BoardRepository repository, BoardImportService imports, string directory)
    {
        using var bitmap = CoverTestBitmap();
        var image = imports.ImportBitmapAsync("A", bitmap).GetAwaiter().GetResult().Single();
        var duplicate = imports.ImportBitmapAsync("A", bitmap).GetAwaiter().GetResult().Single();
        image.X = -120; image.Y = 380; image.Rotation = 37;
        foreach (var member in new[] { image, duplicate }) ConfigureSceneGroup(member);
        image.WebLink = "https://example.com/art"; image.FileLink = @"Z:\unavailable-scene-file\reference.psd";
        repository.UpdateItemsAsync(new[] { image, duplicate }).GetAwaiter().GetResult();
        var gifPath = WriteTestGif(directory);
        var gif = imports.ImportFilesAsync("A", new[] { gifPath }).GetAwaiter().GetResult().Single();
        repository.SaveGifStatesAsync(new[] { new GifSceneState(gif.Id, 2, false, 2) }).GetAwaiter().GetResult();
        var text = RichTextDocumentService.CreateDefault();
        text.Blocks.Clear();
        text.Blocks.Add(new Paragraph(new Bold(new Run("场景注释：跨电脑继续编辑"))) { TextAlignment = TextAlignment.Center });
        var textItem = new BoardTextItem { DocumentData = RichTextDocumentService.Save(text),
            BackgroundColor = "#AA112233", X = 40, Y = 80, Rotation = -12, WebLink = "https://example.com/note", FileLink = image.FileLink };
        ConfigureSceneGroup(textItem);
        repository.AddTextItemsAsync(new[] { textItem }).GetAwaiter().GetResult();
        var drawing = SampleDrawing(BoardDrawingKind.CurveArrow);
        var grouped = SampleDrawing();
        grouped.Kind = BoardDrawingKind.Group;
        grouped.PointsJson = JsonSerializer.Serialize(DrawingGroupService.Read(drawing));
        ConfigureSceneGroup(drawing);
        ConfigureSceneGroup(grouped);
        repository.AddDrawingItemsAsync(new[] { drawing, grouped }).GetAwaiter().GetResult();
        repository.UpdateDrawerNameAsync("A", "场景测试").GetAwaiter().GetResult();
        repository.SaveViewportAsync(new BoardViewport { DrawerId = "A", BackgroundColor = "#FF123456", WindowOpacity = .73,
            OpacityAffectsImages = true, PanX = 140, PanY = -65, Zoom = 1.8, WindowLeft = -9000, WindowTop = -9000,
            WindowWidth = 1250, WindowHeight = 800, Topmost = true, ShowWindowFrame = false }).GetAwaiter().GetResult();
        var crop = new CoverCropState { Zoom = 1.5, FlipX = true };
        using var rendered = DrawerCoverRenderer.Render(DrawerCoverRenderer.Orient(DrawerCoverRenderer.Load(image.AssetPath), crop), crop);
        imports.SaveDrawerCoverAsync("A", image.AssetPath, rendered, crop).GetAwaiter().GetResult();
        return repository.CaptureSceneAsync("A").GetAwaiter().GetResult();
    }
    private static void ScenePortableRoundTrip()
    {
        var root = CreateTempDirectory();
        try
        {
            var (source, imports) = SceneRepository(Path.Combine(root, "source"));
            var snapshot = PopulateScene(source, imports, root);
            var file = Path.Combine(root, "portable.mubo");
            var hash = SceneFileService.WriteAsync(file, snapshot).GetAwaiter().GetResult();
            Equal("MuseBox.Scene", snapshot.Document.Format);
            source.MarkSceneSavedAsync(new SceneBinding("A", file, snapshot.Revision, hash)).GetAwaiter().GetResult();
            Equal(3, snapshot.Document.Assets.Count); // duplicate image + cover source reuse the same bytes
            using (var zip = ZipFile.OpenRead(file))
            {
                Equal(4, zip.Entries.Count);
                using var reader = new StreamReader(zip.GetEntry("scene.json")!.Open());
                var manifest = reader.ReadToEnd();
                True(!manifest.Contains(root.Replace("\\", "\\\\")), "场景包含机器专用图片路径");
                using var manifestDocument = JsonDocument.Parse(manifest);
                Equal(2, manifestDocument.RootElement.GetProperty(nameof(SceneDocument.Version)).GetInt32());
                Equal(1, manifestDocument.RootElement.GetProperty(nameof(SceneDocument.Groups)).GetArrayLength());
                var thumbnailBytes = Convert.FromBase64String(
                    manifestDocument.RootElement.GetProperty(nameof(SceneDocument.ThumbnailPng)).GetString()!);
                using var thumbnail = new Bitmap(new MemoryStream(thumbnailBytes, writable: false));
                Equal(SceneThumbnailRenderer.Edge, thumbnail.Width);
                Equal(SceneThumbnailRenderer.Edge, thumbnail.Height);
                True(thumbnail.GetPixel(470, 470).A == 255, "场景缩略图右下角没有应用图标标记");
            }
            Directory.Move(Path.Combine(root, "source"), Path.Combine(root, "source-offline"));
            var (target, _) = SceneRepository(Path.Combine(root, "target"));
            using var scene = SceneFileService.ReadAsync(file).GetAwaiter().GetResult();
            var id = target.ImportSceneAsync("C", scene, file).GetAwaiter().GetResult();
            Equal("C", id);
            var result = target.CaptureSceneAsync(id).GetAwaiter().GetResult();
            Equal(snapshot.Document.Name, result.Document.Name);
            Equal(1, result.Document.Groups.Count);
            Equal(3, result.Document.Images.Count); Equal(1, result.Document.Texts.Count); Equal(2, result.Document.Drawings.Count);
            Equal(snapshot.Document.Texts[0].DocumentData, result.Document.Texts[0].DocumentData);
            Equal(snapshot.Document.Cover!.Crop, result.Document.Cover!.Crop);
            Equal("#FF123456", result.Document.Viewport.BackgroundColor);
            Equal(.73, result.Document.Viewport.WindowOpacity); Equal(1.8, result.Document.Viewport.Zoom);
            True(result.Document.Viewport.OpacityAffectsImages && result.Document.Viewport.Topmost, "画板设置丢失");
            True(!result.Document.Viewport.ShowWindowFrame, "场景往返后画板边框和阴影设置丢失");
            var images = target.GetItemsAsync(id).GetAwaiter().GetResult();
            True(images.All(i => File.Exists(i.AssetPath) && i.AssetPath.StartsWith(Path.Combine(root, "target"))), "资源仍依赖旧电脑");
            var migrated = images.Single(i => i.WebLink.Length > 0);
            Equal(37d, migrated.Rotation); Equal(-120d, migrated.X); Equal(snapshot.Document.Images.First(i => i.WebLink.Length > 0).FileLink, migrated.FileLink);
            var importedElements = images.Cast<BoardElement>()
                .Concat(target.GetTextItemsAsync(id).GetAwaiter().GetResult())
                .Concat(target.GetDrawingItemsAsync(id).GetAwaiter().GetResult()).ToArray();
            var importedGroup = importedElements.Where(i => i.GroupId.Length > 0).ToArray();
            Equal(5, importedGroup.Length);
            True(importedGroup.Select(i => i.GroupId).Distinct().Count() == 1 && migrated.GroupId != "group1",
                "图片、文字与绘制分组未统一重新映射");
            True(importedGroup.All(i => i.GroupBackgroundColor == "#80445566" &&
                i.GroupBorderColor == "#FF12A0E0" && i.GroupBorderThickness == 40 && i.GroupFramePadding == 180 &&
                i.GroupBackgroundVisible && !i.GroupLocked && i.GroupAutoMembership),
                "混合组合背景和交互设置没有随场景恢复");
            True(!snapshot.Document.Images.Select(i => i.Id).Intersect(images.Select(i => i.Id)).Any(), "跨资料库对象 ID 冲突");
            Equal(2, result.Document.Gifs.Single().FrameIndex); Equal(2d, result.Document.Gifs.Single().Speed);
            True(!result.Document.Gifs.Single().IsPlaying, "暂停状态丢失");
            var gif = images.Single(i => i.AssetPath.EndsWith(".gif"));
            Equal(4, GifAnimationService.LoadAsync(gif.AssetPath).GetAwaiter().GetResult().Frames.Count);
            True(!target.GetDrawersAsync().GetAwaiter().GetResult().Single(d => d.Id == id).HasUnsavedScene, "导入后错误标记未保存");
            var second = Path.Combine(root, "edited.mubo");
            target.UpdateDrawerNameAsync(id, "再次编辑").GetAwaiter().GetResult();
            var edited = target.CaptureSceneAsync(id).GetAwaiter().GetResult();
            SceneFileService.WriteAsync(second, edited).GetAwaiter().GetResult();
            using var reopened = SceneFileService.ReadAsync(second).GetAwaiter().GetResult();
            Equal("再次编辑", reopened.Document.Name);
            Equal(hash, SceneFileService.HashFileAsync(file).GetAwaiter().GetResult());
            var freshId = target.ImportSceneAsync(null, scene, file).GetAwaiter().GetResult();
            Equal("E", freshId);
            Equal(6, target.GetItemCountAsync(freshId).GetAwaiter().GetResult());
        }
        finally { Directory.Delete(root, true); }
    }

    private static void SceneRevisionAndAtomicSave()
    {
        var root = CreateTempDirectory();
        try
        {
            var (repository, imports) = SceneRepository(Path.Combine(root, "library"));
            var snapshot = PopulateScene(repository, imports, root);
            var file = Path.Combine(root, "saved.iscene");
            var hash = SceneFileService.WriteAsync(file, snapshot).GetAwaiter().GetResult();
            repository.MarkSceneSavedAsync(new SceneBinding("A", file, snapshot.Revision, hash)).GetAwaiter().GetResult();
            var before = repository.CaptureSceneAsync("A").GetAwaiter().GetResult();
            var viewport = repository.GetViewportAsync("A").GetAwaiter().GetResult();
            repository.SaveViewportAsync(viewport).GetAwaiter().GetResult();
            Equal(before.Revision, repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision);
            var gif = before.Document.Gifs.Single() with { IsPlaying = true };
            repository.SaveGifStatesAsync(new[] { gif }).GetAwaiter().GetResult();
            var playingRevision = repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision;
            repository.SaveGifStatesAsync(new[] { gif with { FrameIndex = 3 } }).GetAwaiter().GetResult();
            Equal(playingRevision, repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision);
            repository.SaveGifStatesAsync(new[] { gif with { IsPlaying = false } }).GetAwaiter().GetResult();
            True(repository.GetDrawersAsync().GetAwaiter().GetResult()[0].HasUnsavedScene, "GIF 控制修改没有未保存标记");
            viewport.BackgroundColor = "#FFABCDEF";
            repository.SaveViewportAsync(viewport).GetAwaiter().GetResult();
            var changed = repository.CaptureSceneAsync("A").GetAwaiter().GetResult();
            // A bad resource must not damage a previously valid scene.
            var bad = new SceneSnapshot(changed.Document, changed.AssetPaths.ToDictionary(p => p.Key, _ => Path.Combine(root, "missing.png")), changed.Revision);
            ExpectSceneFailure(() => SceneFileService.WriteAsync(file, bad, hash).GetAwaiter().GetResult());
            Equal(hash, SceneFileService.HashFileAsync(file).GetAwaiter().GetResult());
            var externallyChanged = SceneFileService.WriteAsync(file, changed, hash).GetAwaiter().GetResult();
            ExpectSceneFailure(() => SceneFileService.WriteAsync(file, snapshot, hash).GetAwaiter().GetResult());
            Equal(externallyChanged, SceneFileService.HashFileAsync(file).GetAwaiter().GetResult());
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            ExpectSceneFailure(() => SceneFileService.WriteAsync(file, snapshot, externallyChanged, cancelled.Token).GetAwaiter().GetResult());
            True(!Directory.EnumerateFiles(root, "*.tmp").Any(), "失败保存残留临时文件");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            try { ExpectSceneFailure(() => SceneFileService.WriteAsync(file, changed).GetAwaiter().GetResult()); }
            finally { File.SetAttributes(file, FileAttributes.Normal); }
            Equal(externallyChanged, SceneFileService.HashFileAsync(file).GetAwaiter().GetResult());
        }
        finally { Directory.Delete(root, true); }
    }
    private static void ExpectSceneFailure(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or JsonException or ArgumentException or System.Xml.XmlException) { return; }
        throw new Exception("无效场景或失败写入未被拒绝");
    }
    private static void SceneRejectsUnsafeFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var (repository, imports) = SceneRepository(Path.Combine(root, "library"));
            var snapshot = PopulateScene(repository, imports, root);
            var good = Path.Combine(root, "good.iscene");
            SceneFileService.WriteAsync(good, snapshot).GetAwaiter().GetResult();
            foreach (var kind in new[] { "traversal", "missing", "corrupt", "duplicate", "version", "xaml", "dimension", "thumbnail", "oversized" })
            {
                var file = Path.Combine(root, kind + ".iscene");
                File.Copy(good, file);
                using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
                {
                    if (kind is "traversal" or "duplicate") zip.CreateEntry(kind == "traversal" ? "../escape.png" : "scene.json");
                    else if (kind == "missing") zip.Entries.First(e => e.FullName.StartsWith("assets/")).Delete();
                    else if (kind == "corrupt")
                    {
                        var asset = zip.Entries.First(e => e.FullName.StartsWith("assets/")); var name = asset.FullName; asset.Delete();
                        using var stream = zip.CreateEntry(name).Open(); stream.Write(new byte[] { 1,2,3 });
                    }
                    else if (kind != "oversized")
                    {
                        var doc = JsonSerializer.Deserialize<SceneDocument>(JsonSerializer.Serialize(snapshot.Document))!;
                        if (kind == "version") doc.Version = 999;
                        if (kind == "dimension") doc.Images[0].Width = -2;
                        if (kind == "thumbnail") doc.ThumbnailPng = "not-valid-base64";
                        if (kind == "xaml") doc.Texts[0].DocumentData = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                            "<ObjectDataProvider xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />"));
                        zip.GetEntry("scene.json")!.Delete();
                        using var stream = zip.CreateEntry("scene.json").Open(); JsonSerializer.Serialize(stream, doc);
                    }
                }
                if (kind == "oversized")
                {
                    // Forge a ZIP central-directory length, without allocating a huge fixture.
                    var bytes = File.ReadAllBytes(file);
                    var changed = false;
                    for (var i = 0; i < bytes.Length - 46; i++)
                        if (BitConverter.ToUInt32(bytes, i) == 0x02014b50)
                        {
                            BitConverter.GetBytes(600u * 1024 * 1024).CopyTo(bytes, i + 24);
                            changed = true; break;
                        }
                    True(changed, "未找到 ZIP 中央目录");
                    File.WriteAllBytes(file, bytes);
                }
                ExpectSceneFailure(() => { using var _ = SceneFileService.ReadAsync(file).GetAwaiter().GetResult(); });
                Equal(6, repository.GetItemCountAsync("A").GetAwaiter().GetResult());
            }
            True(!File.Exists(Path.Combine(root, "escape.png")), "压缩包路径穿越");
            foreach (var xml in new[]
            {
                "<!DOCTYPE Section [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><Section xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>&e;</Section>",
                "<Section xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' FontFamily='file:///C:/bad.ttf#Font' />",
                "<Section xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Tag='{Binding}' />"
            }) ExpectSceneFailure(() => SceneValidation.ValidateRichText(Convert.ToBase64String(Encoding.UTF8.GetBytes(xml))));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void SceneActivationRoundTrip()
    {
        var pipe = "MuseBox.Tests." + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new SceneActivationService(paths => received.TrySetResult(paths), pipe);
        service.Start();
        var path = Path.Combine(Path.GetTempPath(), "场景 含空格.mubo");
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ScreenshotCollector.Tests.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--scene-send-test"); start.ArgumentList.Add(pipe); start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start)!;
        True(process.WaitForExit(15000) && process.ExitCode == 0, "第二进程转交路径失败");
        True(received.Task.Wait(TimeSpan.FromSeconds(3)), "运行中实例没有收到打开消息");
        Equal(path, received.Task.Result.Single());
        var legacy = Path.ChangeExtension(path, SceneFileService.LegacyExtension);
        Equal(2, SceneActivationService.ScenePaths(new[] { path, path, legacy, "ignored.txt" }).Length);
        Equal(".mubo", SceneFileService.Extension);
    }

    private static void SceneThumbnailProviderRoundTrip()
    {
        var root = CreateTempDirectory();
        IntPtr handle = IntPtr.Zero;
        try
        {
            var archivePath = Path.Combine(root, "thumbnail.mubo");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            using (var source = new Bitmap(128, 128))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(source))
                {
                    graphics.Clear(System.Drawing.Color.FromArgb(255, 18, 52, 90));
                    using var accent = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 119, 243, 181));
                    graphics.FillEllipse(accent, 30, 30, 68, 68);
                }
                using var encoded = new MemoryStream();
                source.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
                using var output = new StreamWriter(archive.CreateEntry("scene.json").Open(), Encoding.UTF8);
                output.Write(JsonSerializer.Serialize(new SceneDocument
                {
                    ThumbnailPng = Convert.ToBase64String(encoded.ToArray())
                }));
            }
            var providerPath = Path.Combine(Directory.GetCurrentDirectory(), "MuseBox.ThumbnailProvider",
                "bin", "Release", "net48", "MuseBox.ThumbnailProvider.dll");
            True(File.Exists(providerPath), "缩略图处理器没有生成");
            var name = AssemblyName.GetAssemblyName(providerPath);
            Equal(new Version(1, 1, 15, 0), name.Version!);
            var assembly = Assembly.LoadFile(providerPath);
            var type = assembly.GetType("MuseBox.ThumbnailProvider.SceneThumbnailProvider", true)!;
            Equal(new Guid("6F67433A-1EA6-47D0-982B-30EFAE588F38"), type.GUID);
            True(type.GetCustomAttribute<ComVisibleAttribute>()?.Value == true, "缩略图处理器未公开为 COM 类");
            string json;
            using (var archive = ZipFile.OpenRead(archivePath))
            using (var reader = new StreamReader(archive.GetEntry("scene.json")!.Open()))
                json = reader.ReadToEnd();
            True(json.Contains("\"ThumbnailPng\"", StringComparison.Ordinal), "测试场景没有缩略图字段");
            var encodedValue = (string)type.GetMethod("ReadThumbnailValue",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { json })!;
            True(encodedValue.Length > 0, "缩略图处理器没有找到场景缩略图字段");
            True(Convert.FromBase64String(encodedValue).Length > 0, "缩略图处理器没有反转义 Base64 字段");
            var instance = Activator.CreateInstance(type)!;
            var initialize = type.GetInterfaces().Single(i => i.Name == "IInitializeWithStream");
            var thumbnail = type.GetInterfaces().Single(i => i.Name == "IThumbnailProvider");
            using var stream = new TestComReadStream(File.OpenRead(archivePath));
            Equal(0, (int)initialize.GetMethod("Initialize")!.Invoke(instance, new object?[] { stream, 0u })!);
            object?[] arguments = [64u, null, null];
            var thumbnailResult = (int)thumbnail.GetMethod("GetThumbnail")!.Invoke(instance, arguments)!;
            var providerError = (string)type.GetField("_lastError",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            True(thumbnailResult == 0, "缩略图处理器失败：" + providerError);
            handle = (IntPtr)arguments[1]!;
            True(handle != IntPtr.Zero, "缩略图处理器没有返回位图");
            using var result = System.Drawing.Image.FromHbitmap(handle);
            Equal(64, result.Width);
            Equal(64, result.Height);
        }
        finally
        {
            if (handle != IntPtr.Zero) DeleteTestBitmap(handle);
            Directory.Delete(root, true);
        }
    }

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteTestBitmap(IntPtr handle);

    private sealed class TestComReadStream : IStream, IDisposable
    {
        private readonly Stream _stream;
        public TestComReadStream(Stream stream) => _stream = stream;
        public void Read(byte[] value, int count, IntPtr read)
        {
            var length = _stream.Read(value, 0, count);
            if (read != IntPtr.Zero) Marshal.WriteInt32(read, length);
        }
        public void Seek(long move, int origin, IntPtr position)
        {
            var value = _stream.Seek(move, (SeekOrigin)origin);
            if (position != IntPtr.Zero) Marshal.WriteInt64(position, value);
        }
        public void Stat(out STATSTG stat, int flags) => stat = new STATSTG { cbSize = _stream.Length };
        public void Clone(out IStream stream) => throw new NotSupportedException();
        public void Commit(int flags) { }
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int type) { }
        public void Revert() => throw new NotSupportedException();
        public void SetSize(long value) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int type) { }
        public void Write(byte[] value, int count, IntPtr written) => throw new NotSupportedException();
        public void Dispose() => _stream.Dispose();
    }
}
