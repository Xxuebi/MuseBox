using System.Drawing;
using System.Drawing.Imaging;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using DrawingColor = System.Drawing.Color;
using System.Windows.Controls;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static void BoardSaveExportMenuHierarchy() => WithDrawingBoard((window, _) =>
    {
        var save = (MenuItem)window.FindName("SaveBoardMenuItem");
        Equal("保存", save.Header);
        Equal("保存|另存为|导出", string.Join('|', save.Items.OfType<MenuItem>().Select(item => item.Header)));
        var export = save.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "导出"));
        Equal("导出所有图像|导出所选图像", string.Join('|', export.Items.OfType<MenuItem>().Select(item => item.Header)));
        foreach (var scope in export.Items.OfType<MenuItem>())
            Equal("导出图像|合成 PNG", string.Join('|', scope.Items.OfType<MenuItem>().Select(item => item.Header)));
    });

    private static void BoardExportOriginalsPreserveFormatAndNeverOverwrite()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.png");
            using (var image = new Bitmap(3, 2)) { image.SetPixel(0, 0, DrawingColor.Red); image.Save(source, ImageFormat.Png); }
            var output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "同名.png"), "keep");
            var first = new BoardItem { Id = "one", AssetId = "asset", LayerName = "同名", ZIndex = 0 };
            var second = new BoardItem { Id = "two", AssetId = "asset", LayerName = "第二", ZIndex = 1 };
            var snapshot = new SceneSnapshot(new SceneDocument { Images = new() { first, second } },
                new Dictionary<string, string> { ["asset"] = source }, 0);
            var request = new ImageExportRequest(snapshot, null);
            var options = new ImageExportOptions(output, ImageExportFormat.Original, "{original_name}");
            var result = BoardExportService.ExportImagesAsync(request, options,
                ImageExportConflictAction.SkipConflicts).GetAwaiter().GetResult();
            Equal(1, result.FileCount);
            Equal("keep", File.ReadAllText(Path.Combine(output, "同名.png")));
            True(File.Exists(Path.Combine(output, "第二.png")), "跳过冲突时未导出无冲突图片");
            True(string.Equals(".png", ImageFileFormatService.FromFile(Path.Combine(output, "第二.png")), StringComparison.Ordinal),
                "导出文件未保留 PNG 格式");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ImageExportTemplateFieldsEscapesAndValidation()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.png");
            using (var image = new Bitmap(2, 2)) image.Save(source, ImageFormat.Png);
            var created = new DateTime(2026, 9, 7, 1, 2, 3, DateTimeKind.Local).ToUniversalTime();
            var images = Enumerable.Range(1, 105).Select(index => new BoardItem
            { Id = index.ToString("D3"), AssetId = "asset", LayerName = "名称" + index, ZIndex = index, CreatedUtc = created }).ToList();
            var snapshot = new SceneSnapshot(new SceneDocument { Name = "场景", Images = images },
                new Dictionary<string, string> { ["asset"] = source }, 0);
            var template = "{{{scene_name}}} {created_date} {created_time} {element_type} {sequence:02} {original_name}";
            var plan = ImageExportTemplateService.CreatePlan(new ImageExportRequest(snapshot, null, "图片"),
                new ImageExportOptions(root, ImageExportFormat.Png, template));
            Equal("{场景} 2026-09-07 01-02-03 图片 001 名称105.png", Path.GetFileName(plan.Entries[0].DestinationPath));
            Equal("105", plan.Entries[104].Sequence.ToString());
            var duplicateFailed = false;
            try { ImageExportTemplateService.CreatePlan(new ImageExportRequest(snapshot, null),
                new ImageExportOptions(root, ImageExportFormat.Png, "same")); }
            catch (InvalidDataException) { duplicateFailed = true; }
            True(duplicateFailed, "批次重名没有被模板验证拒绝");
            var unknownFailed = false;
            try { ImageExportTemplateService.CreatePlan(new ImageExportRequest(snapshot, null),
                new ImageExportOptions(root, ImageExportFormat.Png, "{unknown}")); }
            catch (InvalidDataException) { unknownFailed = true; }
            True(unknownFailed, "未知模板字段没有被拒绝");
            var compact = ImageExportTemplateService.CreatePlan(new ImageExportRequest(snapshot, null, "图片"),
                new ImageExportOptions(root, ImageExportFormat.Png, "%% %s %d %t %e %03i %n"));
            Equal("% 场景 2026-09-07 01-02-03 图片 001 名称105.png", Path.GetFileName(compact.Entries[0].DestinationPath));
            Equal("%02i - %n", ImageExportTemplateService.ToCompactTemplate("{sequence:02} - {original_name}"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ImageExportOverwriteFailureRestoresExistingFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.png");
            using (var image = new Bitmap(2, 2)) image.Save(source, ImageFormat.Png);
            var output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "first.png"), "old content");
            Directory.CreateDirectory(Path.Combine(output, "blocked.png"));
            var snapshot = new SceneSnapshot(new SceneDocument { Images = new()
            {
                new BoardItem { Id = "one", AssetId = "asset", LayerName = "first", ZIndex = 2 },
                new BoardItem { Id = "two", AssetId = "asset", LayerName = "blocked", ZIndex = 1 }
            } }, new Dictionary<string, string> { ["asset"] = source }, 0);
            var failed = false;
            try
            {
                BoardExportService.ExportImagesAsync(new ImageExportRequest(snapshot, null),
                    new ImageExportOptions(output, ImageExportFormat.Png, "{original_name}"),
                    ImageExportConflictAction.OverwriteAll).GetAwaiter().GetResult();
            }
            catch (IOException) { failed = true; }
            True(failed, "故障注入未中断批量覆盖导出");
            Equal("old content", File.ReadAllText(Path.Combine(output, "first.png")));
            True(!Directory.EnumerateDirectories(output, ".MuseBox-export-*").Any(), "失败后遗留了暂存目录");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ImageExportWindowConstructsWithFormatsAndPresets()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.png");
            using (var image = new Bitmap(2, 2)) image.Save(source, ImageFormat.Png);
            var snapshot = new SceneSnapshot(new SceneDocument { Images = new()
            { new BoardItem { Id = "one", AssetId = "asset", LayerName = "sample" } } },
                new Dictionary<string, string> { ["asset"] = source }, 0);
            var window = new ImageExportWindow(new ImageExportRequest(snapshot, null),
                new ImageExportOptions(root, ImageExportFormat.Original, ImageExportOptions.DefaultTemplate),
                _ => Task.FromResult(new ImageExportAttempt(true)));
            window.Measure(new System.Windows.Size(620, 448));
            window.Arrange(new System.Windows.Rect(0, 0, 620, 448));
            window.UpdateLayout();
            Equal("导出图像", window.Title);
            Equal(System.Windows.WindowStyle.None, window.WindowStyle);
            True(window.AllowsTransparency, "导出窗口没有使用应用内圆角透明窗体");
            True(window.FindName("HeaderDragRegion") is Grid, "导出窗口缺少可拖动标题区域");
            True(window.Height < 500, "导出窗口仍保留了过多垂直空白");
            var formatInput = (System.Windows.Controls.ComboBox)window.FindName("FormatInput");
            Equal(4, formatInput.Items.Count);
            Equal("原始", formatInput.SelectionBoxItem.ToString()!);
            True(((System.Windows.Controls.ComboBox)window.FindName("TemplateInput")).IsEditable, "命名模板下拉框不可编辑");
            Equal(5, ((System.Windows.Controls.ComboBox)window.FindName("TemplateInput")).Items.Count);
            window.Close();
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ImageExportConversionsPreservePixelsAndFlattenTransparency()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.png");
            using (var image = new Bitmap(3, 2, PixelFormat.Format32bppArgb))
            { image.SetPixel(0, 0, DrawingColor.FromArgb(0, 255, 0, 0)); image.SetPixel(1, 0, DrawingColor.Blue); image.Save(source, ImageFormat.Png); }
            var item = new BoardItem { Id = "one", AssetId = "asset", LayerName = "sample", ZIndex = 1 };
            var snapshot = new SceneSnapshot(new SceneDocument { Images = new() { item } },
                new Dictionary<string, string> { ["asset"] = source }, 0);
            foreach (var format in new[] { ImageExportFormat.Png, ImageExportFormat.Jpg, ImageExportFormat.Bmp })
            {
                var output = Path.Combine(root, format.ToString());
                var options = new ImageExportOptions(output, format, "{original_name}");
                BoardExportService.ExportImagesAsync(new ImageExportRequest(snapshot, null), options,
                    ImageExportConflictAction.OverwriteAll).GetAwaiter().GetResult();
                var extension = format == ImageExportFormat.Png ? ".png" : format == ImageExportFormat.Jpg ? ".jpg" : ".bmp";
                using var exported = new Bitmap(Path.Combine(output, "sample" + extension));
                Equal(3, exported.Width); Equal(2, exported.Height);
                if (format == ImageExportFormat.Png) Equal(0, exported.GetPixel(0, 0).A);
                else True(exported.GetPixel(0, 0).R > 220 && exported.GetPixel(0, 0).G > 220,
                    "JPG/BMP 没有把透明区域填充为白色");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BoardCompositeExportIsTransparentTwoXAndFiltersGroups()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "red.png");
            using (var image = new Bitmap(10, 10)) { using var brush = Graphics.FromImage(image); brush.Clear(DrawingColor.Red); image.Save(source, ImageFormat.Png); }
            var selected = new BoardItem { Id = "selected", AssetId = "asset", GroupId = "group", Width = 10, Height = 10, ZIndex = 0 };
            var other = new BoardItem { Id = "other", AssetId = "asset", GroupId = "group", X = 30, Width = 10, Height = 10, ZIndex = 1 };
            var group = new BoardGroup { Id = "group", BackgroundVisible = true, BackgroundColor = "#FF0000FF", FramePadding = 5 };
            var document = new SceneDocument { Images = new() { selected, other }, Groups = new() { group } };
            var snapshot = new SceneSnapshot(document, new Dictionary<string, string> { ["asset"] = source }, 0);
            var rendered = SceneThumbnailRenderer.RenderComposite(snapshot, new HashSet<string> { "selected" });
            Equal(20, rendered.PixelWidth); Equal(20, rendered.PixelHeight);
            using (var stream = new MemoryStream(rendered.Png))
            using (var bitmap = new Bitmap(stream)) Equal(255, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A);
            document.Images.Remove(other);
            rendered = SceneThumbnailRenderer.RenderComposite(snapshot, new HashSet<string> { "selected" });
            True(rendered.PixelWidth > 20 && rendered.PixelHeight > 20, "完整选中组合时未包含组合背景范围");
            var drawingOnly = new SceneSnapshot(new SceneDocument { Drawings = new() { new BoardDrawingItem { Id = "draw", Width = 10, Height = 10 } } },
                new Dictionary<string, string>(), 0);
            rendered = SceneThumbnailRenderer.RenderComposite(drawingOnly);
            using var transparentStream = new MemoryStream(rendered.Png);
            using var transparent = new Bitmap(transparentStream);
            Equal(0, transparent.GetPixel(0, 0).A);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BoardCompositeExportPreservesRichText()
    {
        var root = CreateTempDirectory();
        try
        {
            var document = RichTextDocumentService.CreateDefault();
            var paragraph = (System.Windows.Documents.Paragraph)document.Blocks.FirstBlock!;
            paragraph.Inlines.Add(new System.Windows.Documents.Run("MuseBox")
            {
                Foreground = System.Windows.Media.Brushes.Red,
                FontSize = RichTextDocumentService.ToDip(20),
                FontWeight = System.Windows.FontWeights.Bold
            });
            var text = new BoardTextItem { Id = "text", Width = 160, Height = 60,
                BackgroundColor = "#00000000", DocumentData = RichTextDocumentService.Save(document) };
            var snapshot = new SceneSnapshot(new SceneDocument { Texts = new() { text } }, new Dictionary<string, string>(), 0);
            var output = Path.Combine(root, "text.png");
            var result = BoardExportService.ExportCompositeAsync(snapshot, null, output).GetAwaiter().GetResult();
            Equal(320, result.PixelWidth); Equal(120, result.PixelHeight);
            using var bitmap = new Bitmap(output);
            True(Enumerable.Range(0, bitmap.Width).Any(x => Enumerable.Range(0, bitmap.Height)
                .Any(y => bitmap.GetPixel(x, y) is var pixel && pixel.A > 0 && pixel.R > pixel.B)),
                "合成 PNG 未呈现富文本颜色或内容");
        }
        finally { Directory.Delete(root, true); }
    }
}
