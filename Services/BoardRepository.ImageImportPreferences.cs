using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;
namespace ScreenshotCollector.Services;
public sealed partial class BoardRepository
{
    private static async Task InitializeImageImportPreferencesAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS drawer_import_preferences(
                drawer_id TEXT PRIMARY KEY REFERENCES drawers(id) ON DELETE CASCADE,
                ask_every_time INTEGER NOT NULL DEFAULT 1,
                import_mode INTEGER NOT NULL DEFAULT 0);
            """;
        await command.ExecuteNonQueryAsync(token);
    }
    public async Task<ImageImportPreferences> GetImageImportPreferencesAsync(string drawerId, CancellationToken token=default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection=await OpenAsync(token);
            using var command=connection.CreateCommand();
            command.CommandText="SELECT ask_every_time,import_mode FROM drawer_import_preferences WHERE drawer_id=$id";
            command.Parameters.AddWithValue("$id",drawerId);
            await using var reader=await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return new();
            var mode=(ImageImportMode)reader.GetInt32(1);
            return new(reader.GetBoolean(0),Enum.IsDefined(mode)?mode:ImageImportMode.Copy);
        }
        finally { _gate.Release(); }
    }
    public async Task SaveImageImportPreferencesAsync(string drawerId, ImageImportPreferences preferences, CancellationToken token=default)
    {
        if (!Enum.IsDefined(preferences.Mode)) throw new ArgumentOutOfRangeException(nameof(preferences));
        await _gate.WaitAsync(token);
        try
        {
            await using var connection=await OpenAsync(token);
            using var command=connection.CreateCommand();
            command.CommandText="""
                INSERT INTO drawer_import_preferences(drawer_id,ask_every_time,import_mode) VALUES($id,$ask,$mode)
                ON CONFLICT(drawer_id) DO UPDATE SET ask_every_time=$ask,import_mode=$mode
                """;
            command.Parameters.AddWithValue("$id",drawerId);
            command.Parameters.AddWithValue("$ask",preferences.AskEveryTime);
            command.Parameters.AddWithValue("$mode",(int)preferences.Mode);
            await command.ExecuteNonQueryAsync(token);
        }
        finally { _gate.Release(); }
    }
}

