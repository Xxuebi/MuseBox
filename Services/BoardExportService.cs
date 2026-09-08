using System.Drawing;
using System.Drawing.Imaging;
using ScreenshotCollector.Models;
using DrawingColor = System.Drawing.Color;

namespace ScreenshotCollector.Services;

public enum BoardExportMode { IndividualFiles, CompositePng }
public sealed record BoardExportResult(int FileCount, bool WasScaledDown = false, int PixelWidth = 0, int PixelHeight = 0);

public static class BoardExportService
{
    public static async Task<BoardExportResult> ExportImagesAsync(ImageExportRequest request,
        ImageExportOptions options, ImageExportConflictAction conflictAction, CancellationToken token = default)
    {
        var plan = ImageExportTemplateService.CreatePlan(request, options);
        if (plan.Conflicts.Count > 0 && conflictAction == ImageExportConflictAction.Cancel)
            throw new OperationCanceledException("已取消导出。", token);
        var entries = conflictAction == ImageExportConflictAction.SkipConflicts
            ? plan.Entries.Where(entry => !File.Exists(entry.DestinationPath)).ToArray()
            : plan.Entries.ToArray();
        if (entries.Length == 0) return new BoardExportResult(0);

        var directory = Path.GetDirectoryName(entries[0].DestinationPath)!;
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, ".MuseBox-export-" + Guid.NewGuid().ToString("N"));
        var backups = Path.Combine(staging, "backups");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backups);
        var committed = new List<string>();
        var replaced = new List<(string Backup, string Destination)>();
        try
        {
            var staged = new List<(string Temporary, string Destination)>();
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + entry.Extension);
                if (options.Format == ImageExportFormat.Original) await CopyAsync(entry.SourcePath, temporary, token);
                else await Task.Run(() => Encode(request.Snapshot, entry, options.Format, temporary, token), token);
                staged.Add((temporary, entry.DestinationPath));
            }
            foreach (var file in staged)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(file.Destination))
                {
                    if (conflictAction != ImageExportConflictAction.OverwriteAll)
                        throw new IOException($"目标文件已存在：{Path.GetFileName(file.Destination)}");
                    var backup = Path.Combine(backups, Guid.NewGuid().ToString("N") + Path.GetExtension(file.Destination));
                    File.Move(file.Destination, backup, false);
                    replaced.Add((backup, file.Destination));
                }
                File.Move(file.Temporary, file.Destination, false);
                committed.Add(file.Destination);
            }
            return new BoardExportResult(committed.Count);
        }
        catch
        {
            foreach (var file in committed) try { File.Delete(file); } catch { }
            foreach (var file in replaced.AsEnumerable().Reverse())
                try { if (File.Exists(file.Backup)) File.Move(file.Backup, file.Destination, true); } catch { }
            throw;
        }
        finally { try { Directory.Delete(staging, true); } catch { } }
    }

    public static async Task<BoardExportResult> ExportCompositeAsync(SceneSnapshot snapshot,
        IReadOnlySet<string>? selectedIds, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        foreach (var image in snapshot.Document.Images.Where(i => selectedIds is null || selectedIds.Contains(i.Id)))
            AssetPathResolver.ValidateReadableImage(snapshot.AssetPaths[image.AssetId]);
        var completion = new TaskCompletionSource<SceneThumbnailRenderer.CompositeRender>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderer = new Thread(() =>
        {
            try { completion.TrySetResult(SceneThumbnailRenderer.RenderComposite(snapshot, selectedIds)); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "MuseBox composite export" };
        renderer.SetApartmentState(ApartmentState.STA);
        renderer.Start();
        var rendered = await completion.Task.WaitAsync(token);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, rendered.Png, token);
            File.Move(temporary, path, true);
        }
        finally { try { File.Delete(temporary); } catch { } }
        return new BoardExportResult(1, rendered.WasScaledDown, rendered.PixelWidth, rendered.PixelHeight);
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, token);
        await output.FlushAsync(token);
    }
    private static void Encode(SceneSnapshot snapshot, ImageExportPlanEntry entry, ImageExportFormat format,
        string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Image source = GifAnimationService.IsGif(entry.SourcePath) &&
            snapshot.Document.Gifs.FirstOrDefault(state => state.ItemId == entry.Item.Id) is { } gif
                ? GifAnimationService.ExtractFrame(entry.SourcePath, gif.FrameIndex)
                : Image.FromFile(entry.SourcePath);
        token.ThrowIfCancellationRequested();
        if (format == ImageExportFormat.Png)
        {
            using var png = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(png))
            {
                graphics.Clear(DrawingColor.Transparent);
                graphics.DrawImageUnscaled(source, 0, 0);
            }
            png.Save(destination, ImageFormat.Png);
            return;
        }
        using var flattened = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(flattened))
        {
            graphics.Clear(DrawingColor.White);
            graphics.DrawImageUnscaled(source, 0, 0);
        }
        if (format == ImageExportFormat.Bmp) flattened.Save(destination, ImageFormat.Bmp);
        else
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(value => value.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
            flattened.Save(destination, codec, parameters);
        }
    }
}
