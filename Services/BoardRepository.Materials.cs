using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;
namespace ScreenshotCollector.Services;
public sealed partial class BoardRepository
{
    private static async Task InitializeMaterialsAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS board_materials(
                id TEXT PRIMARY KEY, drawer_id TEXT NOT NULL REFERENCES drawers(id) ON DELETE CASCADE,
                asset_id TEXT NOT NULL REFERENCES assets(id), layer_name TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL, sort_order INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS material_drawer_order ON board_materials(drawer_id,sort_order DESC);
            """;
        await command.ExecuteNonQueryAsync(token);
    }
    public async Task<IReadOnlyList<BoardMaterialItem>> GetMaterialsAsync(string drawerId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            var rows = await SceneRowsAsync<BoardMaterialItem>(connection, null, """
                SELECT m.id Id,m.drawer_id DrawerId,m.asset_id AssetId,m.layer_name LayerName,
                    m.created_utc CreatedUtc,m.sort_order SortOrder
                FROM board_materials m WHERE m.drawer_id=$id ORDER BY m.sort_order DESC,m.id
                """, drawerId, token);
            using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT m.id,a.file_name,a.source_kind,a.pixel_width,a.pixel_height
                FROM board_materials m JOIN assets a ON a.id=m.asset_id WHERE m.drawer_id=$id
                """;
            query.Parameters.AddWithValue("$id", drawerId);
            var map = rows.ToDictionary(i => i.Id);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var item = map[reader.GetString(0)];
                item.AssetPath = AssetPathResolver.Resolve(_assetDirectory, reader.GetString(1), (AssetSourceKind)reader.GetInt32(2));
                item.PixelWidth = reader.GetInt32(3); item.PixelHeight = reader.GetInt32(4);
            }
            return rows;
        }
        finally { _gate.Release(); }
    }
    private static Task InsertMaterialAsync(SqliteConnection connection, SqliteTransaction transaction, BoardMaterialItem item, CancellationToken token)
        => ExecuteSceneAsync(connection, transaction,
            "INSERT INTO board_materials(id,drawer_id,asset_id,layer_name,created_utc,sort_order) VALUES($id,$drawer,$asset,$name,$date,$sort)",
            token, ("$id", item.Id), ("$drawer", item.DrawerId), ("$asset", item.AssetId), ("$name", item.LayerName),
            ("$date", item.CreatedUtc.ToString("O")), ("$sort", item.SortOrder));

    public async Task AddMaterialsAsync(IReadOnlyList<BoardMaterialItem> items, CancellationToken token = default)
    {
        if (items.Count == 0) return;
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var transaction = connection.BeginTransaction();
            foreach (var batch in items.GroupBy(i => i.DrawerId))
            {
                using var query = connection.CreateCommand();
                query.Transaction = transaction;
                query.CommandText = "SELECT COALESCE(MAX(sort_order),0) FROM board_materials WHERE drawer_id=$id";
                query.Parameters.AddWithValue("$id", batch.Key);
                var order = checked(Math.Max(DateTime.UtcNow.Ticks, Convert.ToInt64(await query.ExecuteScalarAsync(token)) + 1) + batch.Count());
                foreach (var item in batch)
                {
                    item.SortOrder = order--;
                    await InsertMaterialAsync(connection, transaction, item, token);
                }
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }
    public Task ApplyMaterialChangesAsync(IReadOnlyList<MaterialChange> changes, Func<Task>? beforeCommit = null, CancellationToken token = default)
        => ApplyMaterialChangesCoreAsync(changes, beforeCommit, token);

    public Task SetMaterialAreaEnabledAsync(string drawerId, bool enabled, MaterialChange? change = null, CancellationToken token = default)
    {
        if (change is not null && change.DrawerId != drawerId) throw new ArgumentException("素材目标画板不一致。");
        return ApplyMaterialChangesCoreAsync(change is null ? Array.Empty<MaterialChange>() : new[] { change }, null, token, drawerId, enabled);
    }

    private async Task ApplyMaterialChangesCoreAsync(IReadOnlyList<MaterialChange> changes, Func<Task>? beforeCommit, CancellationToken token, string? settingDrawer = null, bool enabled = true)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var transaction = connection.BeginTransaction();
            foreach (var change in changes)
            {
                foreach (var item in change.BeforeMaterials)
                    await DeleteExpectedAsync("board_materials", item.Id, change.DrawerId);
                foreach (var item in change.BeforeImages.Where(i => change.AfterImages.All(a => a.Id != i.Id)))
                    await DeleteExpectedAsync("items", item.Id, change.DrawerId);
                foreach (var item in change.AfterMaterials)
                {
                    if (item.DrawerId != change.DrawerId) throw new InvalidDataException("素材目标画板不一致。");
                    await InsertMaterialAsync(connection, transaction, item, token);
                }
                foreach (var item in change.AfterImages)
                {
                    if (change.BeforeImages.Any(i => i.Id == item.Id))
                    {
                        await ExecuteSceneAsync(connection, transaction, "UPDATE items SET asset_id=$asset WHERE id=$id AND drawer_id=$drawer",
                            token, ("$asset", item.AssetId), ("$id", item.Id), ("$drawer", change.DrawerId));
                        continue;
                    }
                    if (item.DrawerId != change.DrawerId || item.GroupId.Length != 0) throw new InvalidDataException("素材只能移入当前画板根级。");
                    await ExecuteSceneAsync(connection, transaction, """
                        INSERT INTO items(id,drawer_id,asset_id,x,y,width,height,rotation,z_index,created_utc,layer_name,web_link,file_link)
                        VALUES($id,$drawer,$asset,$x,$y,$w,$h,$r,$z,$date,$name,$web,$file)
                        """, token, ("$id", item.Id), ("$drawer", item.DrawerId), ("$asset", item.AssetId),
                        ("$x", item.X), ("$y", item.Y), ("$w", item.Width), ("$h", item.Height), ("$r", item.Rotation),
                        ("$z", item.ZIndex), ("$date", item.CreatedUtc.ToString("O")), ("$name", item.LayerName),
                        ("$web", item.WebLink), ("$file", item.FileLink));
                }
            }
            if (settingDrawer is not null)
                await ExecuteSceneAsync(connection, transaction,
                    "INSERT INTO viewports(drawer_id,material_area_enabled) VALUES($id,$enabled) ON CONFLICT(drawer_id) DO UPDATE SET material_area_enabled=$enabled",
                    token, ("$id", settingDrawer), ("$enabled", enabled));
            if (beforeCommit is not null) await beforeCommit();
            transaction.Commit();
            async Task DeleteExpectedAsync(string table, string id, string drawer)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table} WHERE id=$id AND drawer_id=$drawer";
                command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$drawer", drawer);
                if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("素材已发生变化，请刷新后重试。");
            }
        }
        finally { _gate.Release(); }
    }
}

