using System.Drawing;

namespace ScreenshotCollector.Services;

public interface IClipboardImageService
{
    ClipboardImageResult ReadImage();
    ClipboardClearResult Clear();
}

public sealed record ClipboardImageResult(
    Bitmap? Bitmap,
    string? SourceDescription,
    string? ErrorMessage)
{
    // File imports preserve animation; Bitmap remains available as a static preview.
    public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();
    public byte[]? EncodedImageBytes { get; init; }
    public Uri? SourceGifUri { get; init; }
    public bool HasImage => Bitmap is not null || FilePaths.Count > 0 || EncodedImageBytes is not null || SourceGifUri is not null;
}

public sealed record ClipboardClearResult(bool Success, string? ErrorMessage);
