using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public interface IScreenCaptureService
{
    Task<IReadOnlyList<CapturedScreen>> CaptureAllScreensAsync(CancellationToken cancellationToken = default);
}
