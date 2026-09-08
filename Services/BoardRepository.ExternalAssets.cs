using ScreenshotCollector.Models;
namespace ScreenshotCollector.Services;
public sealed partial class BoardRepository
{
    public async Task<IReadOnlyList<AssetRecord>> GetLinkedAssetsAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            return await SceneRowsAsync<AssetRecord>(connection, null,
                "SELECT id Id,content_hash Hash,extension Extension,file_name FileName,pixel_width PixelWidth,pixel_height PixelHeight,created_utc CreatedUtc,source_kind SourceKind FROM assets WHERE source_kind=1 AND id IN (SELECT asset_id FROM items UNION SELECT asset_id FROM board_materials)", "", token);
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<string>> RefreshLinkedAssetAsync(AssetRecord asset, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var transaction = connection.BeginTransaction();
            var drawers = new List<string>();
            using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT drawer_id FROM items WHERE asset_id=$id UNION SELECT drawer_id FROM board_materials WHERE asset_id=$id";
                query.Parameters.AddWithValue("$id", asset.Id);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) drawers.Add(reader.GetString(0));
            }
            await ExecuteSceneAsync(connection, transaction,
                "UPDATE assets SET content_hash=$hash,extension=$ext,pixel_width=$w,pixel_height=$h WHERE id=$id AND source_kind=1",
                token, ("$hash", asset.Hash), ("$ext", asset.Extension), ("$w", asset.PixelWidth), ("$h", asset.PixelHeight), ("$id", asset.Id));
            foreach (var drawer in drawers)
                await ExecuteSceneAsync(connection, transaction,
                    "INSERT INTO scene_revisions(drawer_id,revision) VALUES($id,1) ON CONFLICT(drawer_id) DO UPDATE SET revision=revision+1", token, ("$id", drawer));
            transaction.Commit();
            return drawers;
        }
        finally { _gate.Release(); }
    }
}

