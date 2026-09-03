using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed partial class BoardRepository
{
    private static async Task InitializeScenesAsync(SqliteConnection connection, CancellationToken token)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS scene_bindings(
                drawer_id TEXT PRIMARY KEY REFERENCES drawers(id) ON DELETE CASCADE,
                file_path TEXT NOT NULL, saved_revision INTEGER NOT NULL, file_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS scene_revisions(
                drawer_id TEXT PRIMARY KEY REFERENCES drawers(id) ON DELETE CASCADE,
                revision INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS gif_states(
                item_id TEXT PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
                speed REAL NOT NULL, is_playing INTEGER NOT NULL, frame_index INTEGER NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(token);
        foreach (var table in new[] { "items", "text_items", "drawing_items", "board_groups", "drawer_covers", "viewports", "drawers", "gif_states" })
        {
            var columns = table switch
            {
                "drawers" => new[] { "display_name" },
                "viewports" => new[] { "pan_x", "pan_y", "zoom", "window_left", "window_top", "window_width", "window_height", "topmost",
                    "background_color", "window_opacity", "opacity_affects_images", "show_window_frame" },
                "gif_states" => new[] { "speed", "is_playing" },
                _ => Array.Empty<string>()
            };
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                if (table == "drawers" && operation != "UPDATE") continue;
                var row = operation == "DELETE" ? "OLD" : "NEW";
                var id = table == "drawers" ? $"{row}.id" : table == "gif_states"
                    ? $"(SELECT drawer_id FROM items WHERE id={row}.item_id)" : $"{row}.drawer_id";
                var when = operation == "UPDATE" && columns.Length > 0
                    ? string.Join(" OR ", columns.Select(c => $"OLD.{c} IS NOT NEW.{c}")) : "1";
                if (table == "gif_states" && operation == "UPDATE") when += " OR (NEW.is_playing=0 AND OLD.frame_index IS NOT NEW.frame_index)";
                command.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS scene_{table}_{operation.ToLowerInvariant()}
                    AFTER {operation} ON {table} WHEN ({when}) AND EXISTS(SELECT 1 FROM drawers WHERE id={id})
                    BEGIN
                        INSERT INTO scene_revisions(drawer_id,revision) VALUES({id},1)
                        ON CONFLICT(drawer_id) DO UPDATE SET revision=revision+1;
                    END;
                    """;
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }

    // Readers share one transaction. Aliases are fixed by this implementation,
    // never supplied by a scene, and SQLite integers are converted for bool DTOs.
    private static async Task<List<T>> SceneRowsAsync<T>(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, string id, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var rows = new List<T>();
        var properties = typeof(T).GetProperties().ToDictionary(p => p.Name);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var values = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (value is not null && properties.TryGetValue(name, out var property) && property.PropertyType == typeof(bool))
                    value = Convert.ToInt64(value) != 0;
                values[name] = value;
            }
            rows.Add(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(values))!);
        }
        return rows;
    }

    private static async Task ExecuteSceneAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<SceneSnapshot> CaptureSceneAsync(string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var name = connection.CreateCommand();
            name.Transaction = transaction;
            name.CommandText = "SELECT display_name FROM drawers WHERE id=$id";
            name.Parameters.AddWithValue("$id", drawerId);
            var document = new SceneDocument { Name = await name.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidOperationException("抽屉已不存在。") };
            var baseColumns = "id Id, x X,y Y,width Width,height Height,rotation Rotation,z_index ZIndex,created_utc CreatedUtc,layer_name LayerName";
            var groupColumns = "group_id GroupId,group_background_color GroupBackgroundColor,group_border_color GroupBorderColor,group_border_thickness GroupBorderThickness,group_frame_padding GroupFramePadding,group_background_visible GroupBackgroundVisible,group_locked GroupLocked,group_auto_membership GroupAutoMembership";
            document.Images = await SceneRowsAsync<BoardItem>(connection, transaction,
                $"SELECT {baseColumns},asset_id AssetId,web_link WebLink,file_link FileLink,{groupColumns} FROM items WHERE drawer_id=$id ORDER BY z_index,id",
                drawerId, cancellationToken);
            document.Texts = await SceneRowsAsync<BoardTextItem>(connection, transaction,
                $"SELECT {baseColumns},document_data DocumentData,background_color BackgroundColor,web_link WebLink,file_link FileLink,{groupColumns} FROM text_items WHERE drawer_id=$id ORDER BY z_index,id",
                drawerId, cancellationToken);
            document.Drawings = await SceneRowsAsync<BoardDrawingItem>(connection, transaction,
                $"SELECT {baseColumns},kind Kind,points_json PointsJson,stroke_color StrokeColor,fill_color FillColor,stroke_thickness StrokeThickness,stroke_opacity StrokeOpacity,dashed Dashed,{groupColumns} FROM drawing_items WHERE drawer_id=$id ORDER BY z_index,id",
                drawerId, cancellationToken);
            document.Groups = await SceneRowsAsync<BoardGroup>(connection, transaction, """
                SELECT id Id,parent_group_id ParentGroupId,layer_name LayerName,background_color BackgroundColor,
                    border_color BorderColor,border_thickness BorderThickness,frame_padding FramePadding,
                    background_visible BackgroundVisible,locked Locked,auto_membership AutoMembership
                FROM board_groups WHERE drawer_id=$id ORDER BY id
                """, drawerId, cancellationToken);
            document.Viewport = (await SceneRowsAsync<BoardViewport>(connection, transaction, """
                SELECT pan_x PanX,pan_y PanY,zoom Zoom,window_left WindowLeft,window_top WindowTop,
                    window_width WindowWidth,window_height WindowHeight,topmost Topmost,background_color BackgroundColor,
                    window_opacity WindowOpacity,opacity_affects_images OpacityAffectsImages,
                    show_window_frame ShowWindowFrame FROM viewports WHERE drawer_id=$id
                """, drawerId, cancellationToken)).SingleOrDefault() ?? new();
            using var cover = connection.CreateCommand();
            cover.Transaction = transaction;
            cover.CommandText = "SELECT source_asset_id,preview_asset_id,crop_json FROM drawer_covers WHERE drawer_id=$id";
            cover.Parameters.AddWithValue("$id", drawerId);
            await using (var reader = await cover.ExecuteReaderAsync(cancellationToken))
                if (await reader.ReadAsync(cancellationToken))
                    document.Cover = new DrawerCover(reader.GetString(0), reader.GetString(1),
                        JsonSerializer.Deserialize<CoverCropState>(reader.GetString(2)) ?? new());
            document.Gifs = await SceneRowsAsync<GifSceneState>(connection, transaction, GifSelectSql, drawerId, cancellationToken);
            var assets = await SceneRowsAsync<AssetRecord>(connection, transaction, """
                SELECT id Id,hash Hash,extension Extension,file_name FileName,pixel_width PixelWidth,pixel_height PixelHeight,created_utc CreatedUtc
                FROM assets WHERE id IN (SELECT asset_id FROM items WHERE drawer_id=$id)
                    OR id IN (SELECT source_asset_id FROM drawer_covers WHERE drawer_id=$id)
                    OR id IN (SELECT preview_asset_id FROM drawer_covers WHERE drawer_id=$id) ORDER BY hash
                """, drawerId, cancellationToken);
            // Wire documents never carry machine-specific image paths or drawer IDs.
            foreach (var element in document.Images.Cast<BoardElement>().Concat(document.Texts).Concat(document.Drawings)) element.DrawerId = "";
            foreach (var group in document.Groups) group.DrawerId = "";
            document.Viewport.DrawerId = "";
            document.Assets = assets.Select(a => new SceneAsset(a.Id, a.Hash,
                ImageFileFormatService.FromFile(Path.Combine(_assetDirectory, a.FileName)) ?? a.Extension, a.PixelWidth, a.PixelHeight)).ToList();
            var paths = assets.ToDictionary(a => a.Id, a => Path.Combine(_assetDirectory, a.FileName));
            name.CommandText = "SELECT COALESCE((SELECT revision FROM scene_revisions WHERE drawer_id=$id),0)";
            var revision = Convert.ToInt64(await name.ExecuteScalarAsync(cancellationToken));
            transaction.Commit();
            return new SceneSnapshot(document, paths, revision);
        }
        finally { _gate.Release(); }
    }

    private const string GifSelectSql = """
        SELECT g.item_id ItemId,g.speed Speed,g.is_playing IsPlaying,g.frame_index FrameIndex
        FROM gif_states g JOIN items i ON i.id=g.item_id WHERE i.drawer_id=$id ORDER BY g.item_id
        """;

    public async Task<IReadOnlyList<GifSceneState>> GetGifStatesAsync(string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await SceneRowsAsync<GifSceneState>(connection, null, GifSelectSql, drawerId, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveGifStatesAsync(IReadOnlyList<GifSceneState> states, CancellationToken cancellationToken = default)
    {
        if (states.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            foreach (var s in states)
                await ExecuteSceneAsync(connection, transaction, """
                    INSERT INTO gif_states(item_id,speed,is_playing,frame_index)
                    SELECT $id,$speed,$playing,$frame WHERE EXISTS(SELECT 1 FROM items WHERE id=$id)
                    ON CONFLICT(item_id) DO UPDATE SET speed=$speed,is_playing=$playing,frame_index=$frame
                    """, cancellationToken, ("$id", s.ItemId), ("$speed", s.Speed), ("$playing", s.IsPlaying), ("$frame", s.FrameIndex));
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task<SceneBinding?> GetSceneBindingAsync(string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return (await SceneRowsAsync<SceneBinding>(connection, null,
                "SELECT drawer_id DrawerId,file_path FilePath,saved_revision SavedRevision,file_hash FileHash FROM scene_bindings WHERE drawer_id=$id",
                drawerId, cancellationToken)).SingleOrDefault();
        }
        finally { _gate.Release(); }
    }

    public async Task MarkSceneSavedAsync(SceneBinding binding, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await WriteSceneBindingAsync(connection, null, binding, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static Task WriteSceneBindingAsync(SqliteConnection connection, SqliteTransaction? transaction, SceneBinding binding, CancellationToken token)
        => ExecuteSceneAsync(connection, transaction, """
            INSERT INTO scene_bindings(drawer_id,file_path,saved_revision,file_hash) VALUES($id,$path,$revision,$hash)
            ON CONFLICT(drawer_id) DO UPDATE SET file_path=$path,saved_revision=$revision,file_hash=$hash
            """, token, ("$id", binding.DrawerId), ("$path", binding.FilePath), ("$revision", binding.SavedRevision), ("$hash", binding.FileHash));
}
