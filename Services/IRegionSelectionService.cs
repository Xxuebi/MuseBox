using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public interface IRegionSelectionService
{
    Task<RegionSelectionResult?> SelectRegionAsync(
        IReadOnlyList<CapturedScreen> screens,
        CancellationToken cancellationToken = default);
}
