namespace ScreenshotCollector.Models;

public sealed record CoverCropState
{
    public const double DrawerAspect = 25d / 18;
    public int QuarterTurns { get; init; }
    public bool FlipX { get; init; }
    public bool FlipY { get; init; }
    public double Zoom { get; init; } = 1;
    // Offset from the centred image, as fractions of the fixed crop frame.
    public double PanX { get; init; }
    public double PanY { get; init; }
}

public sealed record DrawerCover(string SourceAssetId, string PreviewAssetId, CoverCropState Crop,
    string SourcePath = "", string PreviewPath = "");
