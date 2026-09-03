namespace ScreenshotCollector.Services;

public sealed class AppDataPaths
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseBox");

    public static string LegacyDefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InspirationCollector");

    public static string ResolveRoot(string? rootOverride) =>
        string.IsNullOrWhiteSpace(rootOverride) ? DefaultRoot : Path.GetFullPath(rootOverride);

    public AppDataPaths(string? rootOverride = null)
    {
        Root = ResolveRoot(rootOverride);
        Assets = Path.Combine(Root, "Assets");
        Database = Path.Combine(Root, "boards.db");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Assets);
    }

    public string Root { get; }
    public string Assets { get; }
    public string Database { get; }
}
