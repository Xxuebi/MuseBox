using System.Drawing;

namespace ScreenshotCollector.Services;

public interface IScreenshotStorage
{
    string ValidateTargetDirectory(string targetDirectory);

    Task<string> SavePngAsync(
        Bitmap bitmap,
        string targetDirectory,
        CancellationToken cancellationToken = default);
}
