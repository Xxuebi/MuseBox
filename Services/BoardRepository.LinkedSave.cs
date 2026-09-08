using ScreenshotCollector.Models;
namespace ScreenshotCollector.Services;

public sealed partial class BoardRepository
{
    public async Task CommitSceneSaveAsync(string drawerId, string path, string stagedFile, string hash, long revision,
        IReadOnlyDictionary<string, string> converted, string? expectedHash, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        var backup = path + "." + Guid.NewGuid().ToString("N") + ".backup";
        var published = false;
        var committed = false;
        var hadOriginal = File.Exists(path);
        try
        {
            await using var connection = await OpenAsync(token);
            using var transaction = connection.BeginTransaction();
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.Parameters.AddWithValue("$id", drawerId);
            query.CommandText = "SELECT COALESCE((SELECT revision FROM scene_revisions WHERE drawer_id=$id),0)";
            if (Convert.ToInt64(await query.ExecuteScalarAsync(token)) != revision)
                throw new IOException("画板在保存期间发生变化，请重新保存。");
            if (expectedHash is not null && (!File.Exists(path) || await SceneFileService.HashFileAsync(path, token) != expectedHash))
                throw new SceneFileConflictException();
            foreach (var pair in converted)
                await ExecuteSceneAsync(connection, transaction,
                    "UPDATE items SET asset_id=$new WHERE drawer_id=$id AND asset_id=$old",
                    token, ("$id", drawerId), ("$old", pair.Key), ("$new", pair.Value));
            foreach (var pair in converted)
                await ExecuteSceneAsync(connection, transaction,
                    "UPDATE board_materials SET asset_id=$new WHERE drawer_id=$id AND asset_id=$old",
                    token, ("$id", drawerId), ("$old", pair.Key), ("$new", pair.Value));
            var savedRevision = Convert.ToInt64(await query.ExecuteScalarAsync(token));
            await ExecuteSceneAsync(connection, transaction,
                "INSERT INTO scene_bindings(drawer_id,file_path,saved_revision,file_hash) VALUES($id,$path,$revision,$hash) ON CONFLICT(drawer_id) DO UPDATE SET file_path=$path,saved_revision=$revision,file_hash=$hash",
                token, ("$id", drawerId), ("$path", path), ("$revision", savedRevision), ("$hash", hash));
            token.ThrowIfCancellationRequested();
            if (hadOriginal) File.Replace(stagedFile, path, backup);
            else File.Move(stagedFile, path);
            published = true;
            transaction.Commit();
            committed = true;
        }
        catch
        {
            if (published)
            {
                if (hadOriginal) File.Move(backup, path, true);
                else File.Delete(path);
            }
            throw;
        }
        finally
        {
            _gate.Release();
            if (committed) { try { if (File.Exists(backup)) File.Delete(backup); } catch (IOException) { } }
        }
    }
}

