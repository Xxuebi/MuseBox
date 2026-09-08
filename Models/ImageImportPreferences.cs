namespace ScreenshotCollector.Models;
public sealed record ImageImportPreferences(bool AskEveryTime = true, ImageImportMode Mode = ImageImportMode.Copy);

