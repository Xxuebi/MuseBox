using System.Drawing;

namespace ScreenshotCollector.Models;

public sealed record RegionSelectionResult(CapturedScreen Screen, Rectangle PixelBounds);
