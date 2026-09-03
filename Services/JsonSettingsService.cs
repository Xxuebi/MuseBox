using System.Text.Json;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly string[] _legacySettingsPaths;

    public JsonSettingsService()
    {
        _settingsDirectory = AppDataPaths.DefaultRoot;
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        _legacySettingsPaths =
        [
            Path.Combine(AppDataPaths.LegacyDefaultRoot, "settings.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenshotCollector",
                "settings.json")
        ];
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var sourcePath = File.Exists(_settingsPath)
            ? _settingsPath
            : _legacySettingsPaths.FirstOrDefault(File.Exists);
        if (sourcePath is null)
        {
            var defaults = new AppSettings();
            PrepareLegacyStorageMigration(defaults);
            return defaults;
        }

        try
        {
            await using var stream = File.OpenRead(sourcePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                SerializerOptions,
                cancellationToken) ?? new AppSettings();
            PrepareLegacyStorageMigration(settings);
            if (!string.Equals(sourcePath, _settingsPath, StringComparison.OrdinalIgnoreCase))
            {
                await SaveAsync(settings, cancellationToken);
            }
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    private static void PrepareLegacyStorageMigration(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.BoardStoragePath) ||
            !string.IsNullOrWhiteSpace(settings.PendingStorageMigrationFrom) ||
            HasBoardData(AppDataPaths.DefaultRoot) ||
            !HasBoardData(AppDataPaths.LegacyDefaultRoot)) return;
        settings.PendingStorageMigrationFrom = AppDataPaths.LegacyDefaultRoot;
    }

    private static bool HasBoardData(string root)
    {
        try
        {
            if (File.Exists(Path.Combine(root, "boards.db"))) return true;
            var assets = Path.Combine(root, "Assets");
            return Directory.Exists(assets) && Directory.EnumerateFiles(assets).Any();
        }
        catch
        {
            return false;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(_settingsDirectory);
        var temporaryPath = Path.Combine(_settingsDirectory, $"settings.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
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
                    // A stale temp file does not affect the last valid settings file.
                }
            }
        }
    }
}
