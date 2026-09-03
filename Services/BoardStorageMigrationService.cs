using Microsoft.Data.Sqlite;

namespace ScreenshotCollector.Services;

public static class BoardStorageMigrationService
{
    public static async Task MigrateAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);
        if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase)) return;

        Directory.CreateDirectory(destinationRoot);
        var sourceDatabase = Path.Combine(sourceRoot, "boards.db");
        var destinationDatabase = Path.Combine(destinationRoot, "boards.db");
        if (File.Exists(sourceDatabase) && !File.Exists(destinationDatabase))
        {
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourceDatabase,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationDatabase,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }

        var sourceAssets = Path.Combine(sourceRoot, "Assets");
        var destinationAssets = Path.Combine(destinationRoot, "Assets");
        Directory.CreateDirectory(destinationAssets);
        if (!Directory.Exists(sourceAssets)) return;
        foreach (var sourceFile in Directory.EnumerateFiles(sourceAssets))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationFile = Path.Combine(destinationAssets, Path.GetFileName(sourceFile));
            if (File.Exists(destinationFile)) continue;
            var temporary = $"{destinationFile}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(sourceFile, temporary, overwrite: false);
                File.Move(temporary, destinationFile, overwrite: false);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
