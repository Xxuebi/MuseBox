using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed partial class BoardRepository
{
    private DrawerCover? ReadDrawerCover(SqliteDataReader reader)
    {
        if (reader.IsDBNull(4)) return null;
        CoverCropState state;
        try { state = JsonSerializer.Deserialize<CoverCropState>(reader.GetString(6)) ?? new(); }
        catch (JsonException) { state = new(); }
        return new DrawerCover(reader.GetString(4), reader.GetString(5), state,
            Path.Combine(_assetDirectory, reader.GetString(7)), Path.Combine(_assetDirectory, reader.GetString(8)));
    }

    public async Task UpdateDrawerCoverAsync(string drawerId, DrawerCover? cover, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$drawer", drawerId);
            if (cover is null) command.CommandText = "DELETE FROM drawer_covers WHERE drawer_id=$drawer";
            else
            {
                command.CommandText = """
                    INSERT INTO drawer_covers(drawer_id,source_asset_id,preview_asset_id,crop_json)
                    VALUES($drawer,$source,$preview,$crop)
                    ON CONFLICT(drawer_id) DO UPDATE SET source_asset_id=$source,preview_asset_id=$preview,crop_json=$crop
                    """;
                command.Parameters.AddWithValue("$source", cover.SourceAssetId);
                command.Parameters.AddWithValue("$preview", cover.PreviewAssetId);
                command.Parameters.AddWithValue("$crop", JsonSerializer.Serialize(cover.Crop));
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }
}
