namespace ScreenshotCollector.Models;

public enum ImageExportFormat
{
    Original,
    Png,
    Jpg,
    Bmp
}

public enum ImageExportConflictAction
{
    Cancel,
    OverwriteAll,
    SkipConflicts
}

public sealed record ImageExportOptions(string Directory, ImageExportFormat Format, string NamingTemplate)
{
    public const string DefaultTemplate = "%02i - %n";
}

public sealed record ImageExportRequest(SceneSnapshot Snapshot, IReadOnlySet<string>? SelectedIds,
    string ElementTypeName = "图片");

public sealed record ImageExportPlanEntry(BoardItem Item, string SourcePath, string DestinationPath,
    string Extension, int Sequence);

public sealed record ImageExportPlan(IReadOnlyList<ImageExportPlanEntry> Entries,
    IReadOnlyList<string> Conflicts, bool HasMixedOriginalExtensions);
