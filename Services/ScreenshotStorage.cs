using System.Drawing;
using System.Drawing.Imaging;

namespace ScreenshotCollector.Services;

public sealed class ScreenshotStorage : IScreenshotStorage
{
    public string ValidateTargetDirectory(string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new InvalidOperationException("请先选择截图保存目录。");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(targetDirectory.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("保存路径格式不正确。", exception);
        }

        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException("保存目录不存在，请重新选择。");
        }

        return fullPath;
    }

    public Task<string> SavePngAsync(
        Bitmap bitmap,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        var fullDirectory = ValidateTargetDirectory(targetDirectory);
        var finalPath = CreateUniquePath(fullDirectory);
        var temporaryPath = Path.Combine(
            fullDirectory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            bitmap.Save(temporaryPath, ImageFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath);
            return Task.FromResult(finalPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("没有权限写入所选目录，请选择其他目录。", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Do not hide the original save error because temp cleanup failed.
                }
            }
        }
    }

    private static string CreateUniquePath(string directory)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var candidate = Path.Combine(directory, $"Screenshot_{timestamp}.png");
        var suffix = 1;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"Screenshot_{timestamp}_{suffix++}.png");
        }

        return candidate;
    }
}
