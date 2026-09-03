using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed partial class BoardRepository : IBoardRepository
{
    private readonly string _connectionString;
    private readonly string _assetDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BoardRepository(AppDataPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _assetDirectory = paths.Assets;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS drawers(
                    id TEXT PRIMARY KEY, sort_order INTEGER NOT NULL, created_utc TEXT NOT NULL,
                    display_name TEXT NOT NULL DEFAULT '未命名'
                );
                CREATE TABLE IF NOT EXISTS assets(
                    id TEXT PRIMARY KEY, hash TEXT NOT NULL UNIQUE, extension TEXT NOT NULL,
                    file_name TEXT NOT NULL, pixel_width INTEGER NOT NULL, pixel_height INTEGER NOT NULL,
                    created_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS items(
                    id TEXT PRIMARY KEY,
                    drawer_id TEXT NOT NULL REFERENCES drawers(id) ON DELETE CASCADE,
                    asset_id TEXT NOT NULL REFERENCES assets(id),
                    x REAL NOT NULL, y REAL NOT NULL, width REAL NOT NULL, height REAL NOT NULL,
                    rotation REAL NOT NULL DEFAULT 0,
                    z_index INTEGER NOT NULL, created_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_items_drawer ON items(drawer_id, z_index);
                CREATE TABLE IF NOT EXISTS drawer_covers(
                    drawer_id TEXT PRIMARY KEY REFERENCES drawers(id) ON DELETE CASCADE,
                    source_asset_id TEXT NOT NULL REFERENCES assets(id),
                    preview_asset_id TEXT NOT NULL REFERENCES assets(id),
                    crop_json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS text_items(
                    id TEXT PRIMARY KEY,
                    drawer_id TEXT NOT NULL REFERENCES drawers(id) ON DELETE CASCADE,
                    x REAL NOT NULL, y REAL NOT NULL, width REAL NOT NULL, height REAL NOT NULL,
                    rotation REAL NOT NULL DEFAULT 0, z_index INTEGER NOT NULL,
                    document_data TEXT NOT NULL, background_color TEXT NOT NULL DEFAULT '#00FFFFFF',
                    created_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_text_items_drawer ON text_items(drawer_id, z_index);
                CREATE TABLE IF NOT EXISTS drawing_items(
                    id TEXT PRIMARY KEY,
                    drawer_id TEXT NOT NULL REFERENCES drawers(id) ON DELETE CASCADE,
                    x REAL NOT NULL, y REAL NOT NULL, width REAL NOT NULL, height REAL NOT NULL,
                    rotation REAL NOT NULL DEFAULT 0, z_index INTEGER NOT NULL,
                    kind INTEGER NOT NULL, points_json TEXT NOT NULL,
                    stroke_color TEXT NOT NULL, fill_color TEXT NOT NULL,
                    stroke_thickness REAL NOT NULL, stroke_opacity REAL NOT NULL,
                    dashed INTEGER NOT NULL DEFAULT 0, created_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_drawing_items_drawer ON drawing_items(drawer_id, z_index);
                CREATE TABLE IF NOT EXISTS board_groups(
                    id TEXT PRIMARY KEY,
                    drawer_id TEXT NOT NULL REFERENCES drawers(id) ON DELETE CASCADE,
                    parent_group_id TEXT NOT NULL DEFAULT '',
                    layer_name TEXT NOT NULL DEFAULT '',
                    background_color TEXT NOT NULL DEFAULT '#52FFFFFF',
                    border_color TEXT NOT NULL DEFAULT '#807A7A7A',
                    border_thickness REAL NOT NULL DEFAULT 1.2,
                    frame_padding REAL NOT NULL DEFAULT 14,
                    background_visible INTEGER NOT NULL DEFAULT 1,
                    locked INTEGER NOT NULL DEFAULT 1,
                    auto_membership INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_board_groups_drawer ON board_groups(drawer_id);
                CREATE TABLE IF NOT EXISTS app_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS viewports(
                    drawer_id TEXT PRIMARY KEY REFERENCES drawers(id) ON DELETE CASCADE,
                    pan_x REAL NOT NULL DEFAULT 0, pan_y REAL NOT NULL DEFAULT 0,
                    zoom REAL NOT NULL DEFAULT 1, window_left REAL NULL, window_top REAL NULL,
                    window_width REAL NOT NULL DEFAULT 1100, window_height REAL NOT NULL DEFAULT 760,
                    topmost INTEGER NOT NULL DEFAULT 0,
                    background_color TEXT NOT NULL DEFAULT '#7A7A7A',
                    window_opacity REAL NOT NULL DEFAULT 1,
                    opacity_affects_images INTEGER NOT NULL DEFAULT 0,
                    show_window_frame INTEGER NOT NULL DEFAULT 1
                );
                INSERT OR IGNORE INTO drawers(id, sort_order, created_utc)
                VALUES
                    ('A', 0, strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('B', 1, strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('C', 2, strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('D', 3, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                INSERT OR IGNORE INTO viewports(drawer_id) VALUES('A');
                INSERT OR IGNORE INTO viewports(drawer_id) VALUES('B');
                INSERT OR IGNORE INTO viewports(drawer_id) VALUES('C');
                INSERT OR IGNORE INTO viewports(drawer_id) VALUES('D');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureColumnAsync(connection, "drawers", "display_name",
                "ALTER TABLE drawers ADD COLUMN display_name TEXT NOT NULL DEFAULT '未命名'", cancellationToken);
            await EnsureColumnAsync(connection, "items", "group_id",
                "ALTER TABLE items ADD COLUMN group_id TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnAsync(connection, "items", "group_background_color",
                "ALTER TABLE items ADD COLUMN group_background_color TEXT NOT NULL DEFAULT '#52FFFFFF'", cancellationToken);
            await EnsureColumnAsync(connection, "items", "group_border_color",
                "ALTER TABLE items ADD COLUMN group_border_color TEXT NOT NULL DEFAULT '#807A7A7A'", cancellationToken);
            await EnsureColumnAsync(connection, "items", "group_border_thickness",
                "ALTER TABLE items ADD COLUMN group_border_thickness REAL NOT NULL DEFAULT 1.2", cancellationToken);
            await EnsureColumnAsync(connection, "items", "group_frame_padding",
                "ALTER TABLE items ADD COLUMN group_frame_padding REAL NOT NULL DEFAULT 14", cancellationToken);
            await EnsureColumnAsync(connection, "items", "group_background_visible",
                "ALTER TABLE items ADD COLUMN group_background_visible INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "items", "group_locked",
                "ALTER TABLE items ADD COLUMN group_locked INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "items", "group_auto_membership",
                "ALTER TABLE items ADD COLUMN group_auto_membership INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "items", "web_link",
                "ALTER TABLE items ADD COLUMN web_link TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnAsync(connection, "items", "file_link",
                "ALTER TABLE items ADD COLUMN file_link TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnAsync(connection, "text_items", "web_link",
                "ALTER TABLE text_items ADD COLUMN web_link TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnAsync(connection, "text_items", "file_link",
                "ALTER TABLE text_items ADD COLUMN file_link TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureGroupColumnsAsync(connection, "text_items", cancellationToken);
            await EnsureGroupColumnsAsync(connection, "drawing_items", cancellationToken);
            foreach (var table in new[] { "items", "text_items", "drawing_items" })
                await EnsureColumnAsync(connection, table, "layer_name",
                    $"ALTER TABLE {table} ADD COLUMN layer_name TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnAsync(connection, "viewports", "background_color",
                "ALTER TABLE viewports ADD COLUMN background_color TEXT NOT NULL DEFAULT '#7A7A7A'", cancellationToken);
            await EnsureColumnAsync(connection, "viewports", "window_opacity",
                "ALTER TABLE viewports ADD COLUMN window_opacity REAL NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "viewports", "opacity_affects_images",
                "ALTER TABLE viewports ADD COLUMN opacity_affects_images INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "viewports", "show_window_frame",
                "ALTER TABLE viewports ADD COLUMN show_window_frame INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "items", "rotation",
                "ALTER TABLE items ADD COLUMN rotation REAL NOT NULL DEFAULT 0", cancellationToken);
            await InitializeLayerTreeAsync(connection, cancellationToken);
            await InitializeScenesAsync(connection, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static async Task InitializeLayerTreeAsync(SqliteConnection connection, CancellationToken token)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT value FROM app_metadata WHERE key='layer_tree_v1'";
        if (await exists.ExecuteScalarAsync(token) is not null) return;
        using var transaction = connection.BeginTransaction();
        using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = """
            INSERT OR IGNORE INTO board_groups(id,drawer_id,parent_group_id,layer_name,background_color,border_color,
                border_thickness,frame_padding,background_visible,locked,auto_membership)
            SELECT group_id,drawer_id,'','组合',group_background_color,group_border_color,
                group_border_thickness,group_frame_padding,group_background_visible,group_locked,group_auto_membership
            FROM (
                SELECT group_id,drawer_id,z_index,group_background_color,group_border_color,group_border_thickness,
                    group_frame_padding,group_background_visible,group_locked,group_auto_membership FROM items
                UNION ALL
                SELECT group_id,drawer_id,z_index,group_background_color,group_border_color,group_border_thickness,
                    group_frame_padding,group_background_visible,group_locked,group_auto_membership FROM text_items
                UNION ALL
                SELECT group_id,drawer_id,z_index,group_background_color,group_border_color,group_border_thickness,
                    group_frame_padding,group_background_visible,group_locked,group_auto_membership FROM drawing_items)
            WHERE group_id<>'' GROUP BY group_id;
            INSERT INTO app_metadata(key,value) VALUES('layer_tree_v1','1');
            """;
        await migrate.ExecuteNonQueryAsync(token);
        transaction.Commit();
    }

    private static async Task EnsureGroupColumnsAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, table, "group_id",
            $"ALTER TABLE {table} ADD COLUMN group_id TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, table, "group_background_color",
            $"ALTER TABLE {table} ADD COLUMN group_background_color TEXT NOT NULL DEFAULT '#52FFFFFF'", cancellationToken);
        await EnsureColumnAsync(connection, table, "group_border_color",
            $"ALTER TABLE {table} ADD COLUMN group_border_color TEXT NOT NULL DEFAULT '#807A7A7A'", cancellationToken);
        await EnsureColumnAsync(connection, table, "group_border_thickness",
            $"ALTER TABLE {table} ADD COLUMN group_border_thickness REAL NOT NULL DEFAULT 1.2", cancellationToken);
        await EnsureColumnAsync(connection, table, "group_frame_padding",
            $"ALTER TABLE {table} ADD COLUMN group_frame_padding REAL NOT NULL DEFAULT 14", cancellationToken);
        await EnsureColumnAsync(connection, table, "group_background_visible",
            $"ALTER TABLE {table} ADD COLUMN group_background_visible INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, table, "group_locked",
            $"ALTER TABLE {table} ADD COLUMN group_locked INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, table, "group_auto_membership",
            $"ALTER TABLE {table} ADD COLUMN group_auto_membership INTEGER NOT NULL DEFAULT 0", cancellationToken);
    }

    public async Task<IReadOnlyList<Drawer>> GetDrawersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.id, d.sort_order, d.created_utc, d.display_name,
                    c.source_asset_id, c.preview_asset_id, c.crop_json, s.file_name, p.file_name,
                    b.file_path, COALESCE(r.revision,0) > COALESCE(b.saved_revision,0)
                FROM drawers d LEFT JOIN drawer_covers c ON c.drawer_id=d.id
                LEFT JOIN assets s ON s.id=c.source_asset_id LEFT JOIN assets p ON p.id=c.preview_asset_id
                LEFT JOIN scene_bindings b ON b.drawer_id=d.id
                LEFT JOIN scene_revisions r ON r.drawer_id=d.id
                ORDER BY d.sort_order
                """;
            var result = new List<Drawer>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new Drawer(
                    reader.GetString(0), reader.GetInt32(1), ParseDate(reader.GetString(2)), reader.GetString(3))
                    { Cover = ReadDrawerCover(reader), ScenePath = reader.IsDBNull(9) ? null : reader.GetString(9),
                        HasUnsavedScene = !reader.IsDBNull(9) && reader.GetBoolean(10) });
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<Drawer> AddNextDrawerAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = "SELECT id, sort_order FROM drawers";
            var nextOrder = 0;
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    existing.Add(reader.GetString(0));
                    nextOrder = Math.Max(nextOrder, checked(reader.GetInt32(1) + 1));
                }
            }
            var id = DrawerIdFromIndex(nextOrder);
            while (existing.Contains(id)) id = DrawerIdFromIndex(checked(++nextOrder));
            var now = DateTime.UtcNow;
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO drawers(id, sort_order, created_utc) VALUES($id, $sort, $created);
                INSERT INTO viewports(drawer_id) VALUES($id);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$sort", nextOrder);
            insert.Parameters.AddWithValue("$created", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new Drawer(id, nextOrder, now, "未命名");
        }
        finally { _gate.Release(); }
    }

    internal static string DrawerIdFromIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var result = string.Empty;
        for (long value = (long)index + 1; value > 0; value = (value - 1) / 26)
            result = (char)('A' + (value - 1) % 26) + result;
        return result;
    }

    public async Task UpdateDrawerOrderAsync(IReadOnlyList<string> drawerIds, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var existing = new HashSet<string>(StringComparer.Ordinal);
            var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT id FROM drawers";
            await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(0));
            if (drawerIds.Count != existing.Count || drawerIds.Distinct(StringComparer.Ordinal).Count() != existing.Count ||
                !existing.SetEquals(drawerIds))
                throw new ArgumentException("抽屉顺序必须包含所有现有抽屉，且不能重复。", nameof(drawerIds));
            for (var i = 0; i < drawerIds.Count; i++)
            {
                var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE drawers SET sort_order=$order WHERE id=$id";
                update.Parameters.AddWithValue("$order", i);
                update.Parameters.AddWithValue("$id", drawerIds[i]);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateDrawerNameAsync(
        string drawerId, string displayName, CancellationToken cancellationToken = default)
    {
        displayName = string.IsNullOrWhiteSpace(displayName) ? "未命名" : displayName.Trim();
        if (displayName.Length > 30) displayName = displayName[..30];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE drawers SET display_name=$name WHERE id=$id";
            command.Parameters.AddWithValue("$name", displayName);
            command.Parameters.AddWithValue("$id", drawerId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> GetItemCountAsync(string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM items WHERE drawer_id=$id) +
                    (SELECT COUNT(*) FROM text_items WHERE drawer_id=$id) +
                    (SELECT COUNT(*) FROM drawing_items WHERE drawer_id=$id)
                """;
            command.Parameters.AddWithValue("$id", drawerId);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> DeleteDrawerAsync(string drawerId, CancellationToken cancellationToken = default)
    {
        if (drawerId == "A") throw new InvalidOperationException("抽屉 A 不能删除。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var candidates = new List<(string Id, string FileName)>();
            var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = """
                SELECT DISTINCT a.id, a.file_name FROM assets a
                WHERE a.id IN (SELECT asset_id FROM items WHERE drawer_id=$drawer)
                    OR a.id IN (SELECT source_asset_id FROM drawer_covers WHERE drawer_id=$drawer)
                    OR a.id IN (SELECT preview_asset_id FROM drawer_covers WHERE drawer_id=$drawer)
                """;
            find.Parameters.AddWithValue("$drawer", drawerId);
            await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    candidates.Add((reader.GetString(0), reader.GetString(1)));
            }
            var deleteDrawer = connection.CreateCommand();
            deleteDrawer.Transaction = transaction;
            deleteDrawer.CommandText = "DELETE FROM drawers WHERE id=$id";
            deleteDrawer.Parameters.AddWithValue("$id", drawerId);
            await deleteDrawer.ExecuteNonQueryAsync(cancellationToken);

            var orphanFiles = new List<string>();
            foreach (var candidate in candidates)
            {
                var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = """
                    SELECT (SELECT COUNT(*) FROM items WHERE asset_id=$id) +
                    (SELECT COUNT(*) FROM drawer_covers WHERE source_asset_id=$id OR preview_asset_id=$id)
                    """;
                count.Parameters.AddWithValue("$id", candidate.Id);
                if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) != 0) continue;
                var deleteAsset = connection.CreateCommand();
                deleteAsset.Transaction = transaction;
                deleteAsset.CommandText = "DELETE FROM assets WHERE id=$id";
                deleteAsset.Parameters.AddWithValue("$id", candidate.Id);
                await deleteAsset.ExecuteNonQueryAsync(cancellationToken);
                orphanFiles.Add(Path.Combine(_assetDirectory, candidate.FileName));
            }
            await transaction.CommitAsync(cancellationToken);
            return orphanFiles;
        }
        finally { _gate.Release(); }
    }

    public async Task<AssetRecord?> FindAssetByHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "SELECT id,hash,extension,file_name,pixel_width,pixel_height,created_utc FROM assets WHERE hash=$hash";
            command.Parameters.AddWithValue("$hash", hash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadAsset(reader) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertAssetAsync(AssetRecord asset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO assets(id,hash,extension,file_name,pixel_width,pixel_height,created_utc)
                VALUES($id,$hash,$ext,$file,$width,$height,$created)
                """;
            command.Parameters.AddWithValue("$id", asset.Id);
            command.Parameters.AddWithValue("$hash", asset.Hash);
            command.Parameters.AddWithValue("$ext", asset.Extension);
            command.Parameters.AddWithValue("$file", asset.FileName);
            command.Parameters.AddWithValue("$width", asset.PixelWidth);
            command.Parameters.AddWithValue("$height", asset.PixelHeight);
            command.Parameters.AddWithValue("$created", asset.CreatedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BoardItem>> GetItemsAsync(string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT i.id,i.drawer_id,i.asset_id,a.file_name,i.x,i.y,i.width,i.height,
                       i.rotation,i.z_index,i.created_utc,i.group_id,i.web_link,i.file_link,
                       i.group_background_color,i.group_border_color,i.group_border_thickness,i.group_frame_padding,
                       i.group_background_visible,i.group_locked,i.group_auto_membership,i.layer_name
                FROM items i JOIN assets a ON a.id=i.asset_id
                WHERE i.drawer_id=$drawer ORDER BY i.z_index
                """;
            command.Parameters.AddWithValue("$drawer", drawerId);
            var result = new List<BoardItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new BoardItem
                {
                    Id = reader.GetString(0), DrawerId = reader.GetString(1), AssetId = reader.GetString(2),
                    AssetPath = Path.Combine(_assetDirectory, reader.GetString(3)),
                    X = reader.GetDouble(4), Y = reader.GetDouble(5), Width = reader.GetDouble(6),
                    Height = reader.GetDouble(7), Rotation = reader.GetDouble(8),
                    ZIndex = reader.GetInt32(9), CreatedUtc = ParseDate(reader.GetString(10)),
                    GroupId = reader.GetString(11), WebLink = reader.GetString(12), FileLink = reader.GetString(13),
                    GroupBackgroundColor = reader.GetString(14), GroupBorderColor = reader.GetString(15),
                    GroupBorderThickness = reader.GetDouble(16), GroupFramePadding = reader.GetDouble(17),
                    GroupBackgroundVisible = reader.GetBoolean(18),
                    GroupLocked = reader.GetBoolean(19), GroupAutoMembership = reader.GetBoolean(20),
                    LayerName = reader.GetString(21)
                });
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public Task AddItemsAsync(IReadOnlyList<BoardItem> items, CancellationToken cancellationToken = default) =>
        WriteItemsAsync(items, insert: true, cancellationToken);

    public Task UpdateItemsAsync(IReadOnlyList<BoardItem> items, CancellationToken cancellationToken = default) =>
        WriteItemsAsync(items, insert: false, cancellationToken);

    private async Task WriteItemsAsync(IReadOnlyList<BoardItem> items, bool insert, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var item in items)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = insert
                    ? "INSERT INTO items(id,drawer_id,asset_id,x,y,width,height,rotation,z_index,created_utc,group_id,web_link,file_link,group_background_color,group_border_color,group_border_thickness,group_frame_padding,group_background_visible,group_locked,group_auto_membership,layer_name) VALUES($id,$drawer,$asset,$x,$y,$w,$h,$rotation,$z,$created,$group,$web,$file,$groupBackground,$groupBorder,$groupThickness,$groupPadding,$groupVisible,$groupLocked,$groupAuto,$layerName)"
                    : "UPDATE items SET asset_id=$asset,x=$x,y=$y,width=$w,height=$h,rotation=$rotation,z_index=$z,group_id=$group,web_link=$web,file_link=$file,group_background_color=$groupBackground,group_border_color=$groupBorder,group_border_thickness=$groupThickness,group_frame_padding=$groupPadding,group_background_visible=$groupVisible,group_locked=$groupLocked,group_auto_membership=$groupAuto,layer_name=$layerName WHERE id=$id";
                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$drawer", item.DrawerId);
                command.Parameters.AddWithValue("$asset", item.AssetId);
                command.Parameters.AddWithValue("$group", item.GroupId);
                command.Parameters.AddWithValue("$web", item.WebLink);
                command.Parameters.AddWithValue("$file", item.FileLink);
                command.Parameters.AddWithValue("$groupBackground", item.GroupBackgroundColor);
                command.Parameters.AddWithValue("$groupBorder", item.GroupBorderColor);
                command.Parameters.AddWithValue("$groupThickness", item.GroupBorderThickness);
                command.Parameters.AddWithValue("$groupPadding", item.GroupFramePadding);
                command.Parameters.AddWithValue("$groupVisible", item.GroupBackgroundVisible);
                command.Parameters.AddWithValue("$groupLocked", item.GroupLocked);
                command.Parameters.AddWithValue("$groupAuto", item.GroupAutoMembership);
                command.Parameters.AddWithValue("$layerName", item.LayerName);
                command.Parameters.AddWithValue("$x", item.X);
                command.Parameters.AddWithValue("$y", item.Y);
                command.Parameters.AddWithValue("$w", item.Width);
                command.Parameters.AddWithValue("$h", item.Height);
                command.Parameters.AddWithValue("$rotation", item.Rotation);
                command.Parameters.AddWithValue("$z", item.ZIndex);
                command.Parameters.AddWithValue("$created", item.CreatedUtc.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteItemsAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var id in itemIds)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM items WHERE id=$id";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BoardTextItem>> GetTextItemsAsync(
        string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "SELECT id,drawer_id,x,y,width,height,rotation,z_index,document_data,background_color,created_utc,web_link,file_link,group_id,group_background_color,group_border_color,group_border_thickness,group_frame_padding,group_background_visible,group_locked,group_auto_membership,layer_name FROM text_items WHERE drawer_id=$drawer ORDER BY z_index";
            command.Parameters.AddWithValue("$drawer", drawerId);
            var result = new List<BoardTextItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new BoardTextItem
                {
                    Id = reader.GetString(0), DrawerId = reader.GetString(1),
                    X = reader.GetDouble(2), Y = reader.GetDouble(3),
                    Width = reader.GetDouble(4), Height = reader.GetDouble(5),
                    Rotation = reader.GetDouble(6), ZIndex = reader.GetInt32(7),
                    DocumentData = reader.GetString(8), BackgroundColor = reader.GetString(9),
                    CreatedUtc = ParseDate(reader.GetString(10)),
                    WebLink = reader.GetString(11), FileLink = reader.GetString(12),
                    GroupId = reader.GetString(13), GroupBackgroundColor = reader.GetString(14),
                    GroupBorderColor = reader.GetString(15), GroupBorderThickness = reader.GetDouble(16),
                    GroupFramePadding = reader.GetDouble(17), GroupBackgroundVisible = reader.GetBoolean(18),
                    GroupLocked = reader.GetBoolean(19), GroupAutoMembership = reader.GetBoolean(20),
                    LayerName = reader.GetString(21)
                });
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public Task AddTextItemsAsync(IReadOnlyList<BoardTextItem> items, CancellationToken cancellationToken = default) =>
        WriteTextItemsAsync(items, true, cancellationToken);

    public Task UpdateTextItemsAsync(IReadOnlyList<BoardTextItem> items, CancellationToken cancellationToken = default) =>
        WriteTextItemsAsync(items, false, cancellationToken);

    private async Task WriteTextItemsAsync(
        IReadOnlyList<BoardTextItem> items, bool insert, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var item in items)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = insert
                    ? "INSERT INTO text_items(id,drawer_id,x,y,width,height,rotation,z_index,document_data,background_color,created_utc,web_link,file_link,group_id,group_background_color,group_border_color,group_border_thickness,group_frame_padding,group_background_visible,group_locked,group_auto_membership,layer_name) VALUES($id,$drawer,$x,$y,$w,$h,$rotation,$z,$document,$background,$created,$web,$file,$group,$groupBackground,$groupBorder,$groupThickness,$groupPadding,$groupVisible,$groupLocked,$groupAuto,$layerName)"
                    : "UPDATE text_items SET x=$x,y=$y,width=$w,height=$h,rotation=$rotation,z_index=$z,document_data=$document,background_color=$background,web_link=$web,file_link=$file,group_id=$group,group_background_color=$groupBackground,group_border_color=$groupBorder,group_border_thickness=$groupThickness,group_frame_padding=$groupPadding,group_background_visible=$groupVisible,group_locked=$groupLocked,group_auto_membership=$groupAuto,layer_name=$layerName WHERE id=$id";
                AddElementParameters(command, item);
                AddGroupParameters(command, item);
                command.Parameters.AddWithValue("$document", item.DocumentData);
                command.Parameters.AddWithValue("$background", item.BackgroundColor);
                command.Parameters.AddWithValue("$web", item.WebLink);
                command.Parameters.AddWithValue("$file", item.FileLink);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public Task DeleteTextItemsAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken = default) =>
        DeleteRowsAsync("text_items", itemIds, cancellationToken);

    public async Task<IReadOnlyList<BoardDrawingItem>> GetDrawingItemsAsync(
        string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "SELECT id,drawer_id,x,y,width,height,rotation,z_index,kind,points_json,stroke_color,fill_color,stroke_thickness,stroke_opacity,dashed,created_utc,group_id,group_background_color,group_border_color,group_border_thickness,group_frame_padding,group_background_visible,group_locked,group_auto_membership,layer_name FROM drawing_items WHERE drawer_id=$drawer ORDER BY z_index";
            command.Parameters.AddWithValue("$drawer", drawerId);
            var result = new List<BoardDrawingItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new BoardDrawingItem
                {
                    Id = reader.GetString(0), DrawerId = reader.GetString(1),
                    X = reader.GetDouble(2), Y = reader.GetDouble(3),
                    Width = reader.GetDouble(4), Height = reader.GetDouble(5),
                    Rotation = reader.GetDouble(6), ZIndex = reader.GetInt32(7),
                    Kind = (BoardDrawingKind)reader.GetInt32(8), PointsJson = reader.GetString(9),
                    StrokeColor = reader.GetString(10), FillColor = reader.GetString(11),
                    StrokeThickness = reader.GetDouble(12), StrokeOpacity = reader.GetDouble(13),
                    Dashed = reader.GetBoolean(14), CreatedUtc = ParseDate(reader.GetString(15)),
                    GroupId = reader.GetString(16), GroupBackgroundColor = reader.GetString(17),
                    GroupBorderColor = reader.GetString(18), GroupBorderThickness = reader.GetDouble(19),
                    GroupFramePadding = reader.GetDouble(20), GroupBackgroundVisible = reader.GetBoolean(21),
                    GroupLocked = reader.GetBoolean(22), GroupAutoMembership = reader.GetBoolean(23),
                    LayerName = reader.GetString(24)
                });
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public Task AddDrawingItemsAsync(IReadOnlyList<BoardDrawingItem> items, CancellationToken cancellationToken = default) =>
        WriteDrawingItemsAsync(items, true, cancellationToken);

    public Task UpdateDrawingItemsAsync(IReadOnlyList<BoardDrawingItem> items, CancellationToken cancellationToken = default) =>
        WriteDrawingItemsAsync(items, false, cancellationToken);

    private async Task WriteDrawingItemsAsync(
        IReadOnlyList<BoardDrawingItem> items, bool insert, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var item in items)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = insert
                    ? "INSERT INTO drawing_items(id,drawer_id,x,y,width,height,rotation,z_index,kind,points_json,stroke_color,fill_color,stroke_thickness,stroke_opacity,dashed,created_utc,group_id,group_background_color,group_border_color,group_border_thickness,group_frame_padding,group_background_visible,group_locked,group_auto_membership,layer_name) VALUES($id,$drawer,$x,$y,$w,$h,$rotation,$z,$kind,$points,$stroke,$fill,$thickness,$opacity,$dashed,$created,$group,$groupBackground,$groupBorder,$groupThickness,$groupPadding,$groupVisible,$groupLocked,$groupAuto,$layerName)"
                    : "UPDATE drawing_items SET x=$x,y=$y,width=$w,height=$h,rotation=$rotation,z_index=$z,kind=$kind,points_json=$points,stroke_color=$stroke,fill_color=$fill,stroke_thickness=$thickness,stroke_opacity=$opacity,dashed=$dashed,group_id=$group,group_background_color=$groupBackground,group_border_color=$groupBorder,group_border_thickness=$groupThickness,group_frame_padding=$groupPadding,group_background_visible=$groupVisible,group_locked=$groupLocked,group_auto_membership=$groupAuto,layer_name=$layerName WHERE id=$id";
                AddElementParameters(command, item);
                AddGroupParameters(command, item);
                command.Parameters.AddWithValue("$kind", (int)item.Kind);
                command.Parameters.AddWithValue("$points", item.PointsJson);
                command.Parameters.AddWithValue("$stroke", item.StrokeColor);
                command.Parameters.AddWithValue("$fill", item.FillColor);
                command.Parameters.AddWithValue("$thickness", item.StrokeThickness);
                command.Parameters.AddWithValue("$opacity", item.StrokeOpacity);
                command.Parameters.AddWithValue("$dashed", item.Dashed);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public Task DeleteDrawingItemsAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken = default) =>
        DeleteRowsAsync("drawing_items", itemIds, cancellationToken);

    private async Task DeleteRowsAsync(
        string table, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return;
        if (table is not ("text_items" or "drawing_items"))
            throw new ArgumentOutOfRangeException(nameof(table));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var id in ids)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table} WHERE id=$id";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static void AddElementParameters(SqliteCommand command, BoardElement item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$drawer", item.DrawerId);
        command.Parameters.AddWithValue("$x", item.X);
        command.Parameters.AddWithValue("$y", item.Y);
        command.Parameters.AddWithValue("$w", item.Width);
        command.Parameters.AddWithValue("$h", item.Height);
        command.Parameters.AddWithValue("$rotation", item.Rotation);
        command.Parameters.AddWithValue("$z", item.ZIndex);
        command.Parameters.AddWithValue("$created", item.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$layerName", item.LayerName);
    }

    private static void AddGroupParameters(SqliteCommand command, BoardElement item, string? groupId = null)
    {
        command.Parameters.AddWithValue("$group", groupId ?? item.GroupId);
        command.Parameters.AddWithValue("$groupBackground", item.GroupBackgroundColor);
        command.Parameters.AddWithValue("$groupBorder", item.GroupBorderColor);
        command.Parameters.AddWithValue("$groupThickness", item.GroupBorderThickness);
        command.Parameters.AddWithValue("$groupPadding", item.GroupFramePadding);
        command.Parameters.AddWithValue("$groupVisible", item.GroupBackgroundVisible);
        command.Parameters.AddWithValue("$groupLocked", item.GroupLocked);
        command.Parameters.AddWithValue("$groupAuto", item.GroupAutoMembership);
    }

    public async Task<IReadOnlyList<BoardGroup>> GetGroupsAsync(
        string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id,drawer_id,parent_group_id,layer_name,background_color,border_color,
                    border_thickness,frame_padding,background_visible,locked,auto_membership
                FROM board_groups WHERE drawer_id=$drawer ORDER BY id
                """;
            command.Parameters.AddWithValue("$drawer", drawerId);
            var result = new List<BoardGroup>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new BoardGroup
                {
                    Id = reader.GetString(0), DrawerId = reader.GetString(1), ParentGroupId = reader.GetString(2),
                    LayerName = reader.GetString(3), BackgroundColor = reader.GetString(4), BorderColor = reader.GetString(5),
                    BorderThickness = reader.GetDouble(6), FramePadding = reader.GetDouble(7),
                    BackgroundVisible = reader.GetBoolean(8), Locked = reader.GetBoolean(9),
                    AutoMembership = reader.GetBoolean(10)
                });
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyLayerTreeAsync(string drawerId, IReadOnlyList<BoardGroup> groups,
        IReadOnlyList<BoardElement> elements, CancellationToken cancellationToken = default)
    {
        if (groups.Any(group => group.DrawerId != drawerId) || elements.Any(element => element.DrawerId != drawerId))
            throw new InvalidOperationException("图层数据不属于当前画板。");
        BoardLayerTreeService.Validate(groups, elements);
        BoardLayerTreeService.SyncLegacyPresentation(groups, elements);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM board_groups WHERE drawer_id=$drawer";
                delete.Parameters.AddWithValue("$drawer", drawerId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var group in groups)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO board_groups(id,drawer_id,parent_group_id,layer_name,background_color,border_color,
                        border_thickness,frame_padding,background_visible,locked,auto_membership)
                    VALUES($id,$drawer,$parent,$name,$background,$border,$thickness,$padding,$visible,$locked,$auto)
                    """;
                command.Parameters.AddWithValue("$id", group.Id);
                command.Parameters.AddWithValue("$drawer", drawerId);
                command.Parameters.AddWithValue("$parent", group.ParentGroupId);
                command.Parameters.AddWithValue("$name", group.LayerName);
                command.Parameters.AddWithValue("$background", group.BackgroundColor);
                command.Parameters.AddWithValue("$border", group.BorderColor);
                command.Parameters.AddWithValue("$thickness", group.BorderThickness);
                command.Parameters.AddWithValue("$padding", group.FramePadding);
                command.Parameters.AddWithValue("$visible", group.BackgroundVisible);
                command.Parameters.AddWithValue("$locked", group.Locked);
                command.Parameters.AddWithValue("$auto", group.AutoMembership);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var element in elements)
            {
                var table = element switch
                {
                    BoardItem => "items",
                    BoardTextItem => "text_items",
                    BoardDrawingItem => "drawing_items",
                    _ => throw new InvalidOperationException("不支持的图层类型。")
                };
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    UPDATE {table} SET z_index=$z,group_id=$group,layer_name=$name,
                        group_background_color=$groupBackground,group_border_color=$groupBorder,
                        group_border_thickness=$groupThickness,group_frame_padding=$groupPadding,
                        group_background_visible=$groupVisible,group_locked=$groupLocked,
                        group_auto_membership=$groupAuto
                    WHERE id=$id AND drawer_id=$drawer
                    """;
                command.Parameters.AddWithValue("$z", element.ZIndex);
                command.Parameters.AddWithValue("$group", element.GroupId);
                command.Parameters.AddWithValue("$name", element.LayerName);
                command.Parameters.AddWithValue("$groupBackground", element.GroupBackgroundColor);
                command.Parameters.AddWithValue("$groupBorder", element.GroupBorderColor);
                command.Parameters.AddWithValue("$groupThickness", element.GroupBorderThickness);
                command.Parameters.AddWithValue("$groupPadding", element.GroupFramePadding);
                command.Parameters.AddWithValue("$groupVisible", element.GroupBackgroundVisible);
                command.Parameters.AddWithValue("$groupLocked", element.GroupLocked);
                command.Parameters.AddWithValue("$groupAuto", element.GroupAutoMembership);
                command.Parameters.AddWithValue("$id", element.Id);
                command.Parameters.AddWithValue("$drawer", drawerId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("图层元素已不存在，未修改层级。");
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task<BoardViewport> GetViewportAsync(string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "SELECT pan_x,pan_y,zoom,window_left,window_top,window_width,window_height,topmost,background_color,window_opacity,opacity_affects_images,show_window_frame FROM viewports WHERE drawer_id=$id";
            command.Parameters.AddWithValue("$id", drawerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return new BoardViewport { DrawerId = drawerId };
            return new BoardViewport
            {
                DrawerId = drawerId, PanX = reader.GetDouble(0), PanY = reader.GetDouble(1),
                Zoom = reader.GetDouble(2), WindowLeft = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                WindowTop = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                WindowWidth = reader.GetDouble(5), WindowHeight = reader.GetDouble(6),
                Topmost = reader.GetBoolean(7),
                BackgroundColor = reader.GetString(8),
                WindowOpacity = reader.GetDouble(9),
                OpacityAffectsImages = reader.GetBoolean(10),
                ShowWindowFrame = reader.GetBoolean(11)
            };
        }
        finally { _gate.Release(); }
    }

    public async Task SaveViewportAsync(BoardViewport viewport, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO viewports(drawer_id,pan_x,pan_y,zoom,window_left,window_top,window_width,window_height,topmost,background_color,window_opacity,opacity_affects_images,show_window_frame)
                VALUES($id,$x,$y,$zoom,$left,$top,$width,$height,$pin,$background,$opacity,$affectImages,$showFrame)
                ON CONFLICT(drawer_id) DO UPDATE SET pan_x=$x,pan_y=$y,zoom=$zoom,window_left=$left,
                    window_top=$top,window_width=$width,window_height=$height,topmost=$pin,
                    background_color=$background,window_opacity=$opacity,opacity_affects_images=$affectImages,show_window_frame=$showFrame
                """;
            command.Parameters.AddWithValue("$id", viewport.DrawerId);
            command.Parameters.AddWithValue("$x", viewport.PanX);
            command.Parameters.AddWithValue("$y", viewport.PanY);
            command.Parameters.AddWithValue("$zoom", viewport.Zoom);
            command.Parameters.AddWithValue("$left", (object?)viewport.WindowLeft ?? DBNull.Value);
            command.Parameters.AddWithValue("$top", (object?)viewport.WindowTop ?? DBNull.Value);
            command.Parameters.AddWithValue("$width", viewport.WindowWidth);
            command.Parameters.AddWithValue("$height", viewport.WindowHeight);
            command.Parameters.AddWithValue("$pin", viewport.Topmost);
            command.Parameters.AddWithValue("$background", viewport.BackgroundColor);
            command.Parameters.AddWithValue("$opacity", viewport.WindowOpacity);
            command.Parameters.AddWithValue("$affectImages", viewport.OpacityAffectsImages);
            command.Parameters.AddWithValue("$showFrame", viewport.ShowWindowFrame);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetLatestAssetPathAsync(string drawerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT a.file_name FROM items i JOIN assets a ON a.id=i.asset_id
                WHERE i.drawer_id=$id ORDER BY i.created_utc DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("$id", drawerId);
            return await command.ExecuteScalarAsync(cancellationToken) is string file
                ? Path.Combine(_assetDirectory, file) : null;
        }
        finally { _gate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static AssetRecord ReadAsset(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetInt32(4), reader.GetInt32(5), ParseDate(reader.GetString(6)));

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string alterSql,
        CancellationToken cancellationToken)
    {
        var exists = false;
        var query = connection.CreateCommand();
        query.CommandText = $"PRAGMA table_info({table})";
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists) return;
        var alter = connection.CreateCommand();
        alter.CommandText = alterSql;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
