using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector.Tests;
internal static partial class Program
{
    private static string LinkedTestImage(string root, string name = "original.png", int width = 24)
    {
        var path = Path.Combine(root, name);
        using var image = new Bitmap(width, 18);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Coral);
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }
    private static void LinkedImportBatchSafety()
    {
        var root = CreateTempDirectory();
        try
        {
            var library = Path.Combine(root, "library");
            var (repo, imports) = SceneRepository(library);
            var first = LinkedTestImage(root);
            var second = Path.Combine(root, "same.png"); File.Copy(first, second);
            var items = imports.ImportFilesAsync("A", new[] { first, second }, mode: ImageImportMode.Link).GetAwaiter().GetResult();
            True(items[0].AssetId != items[1].AssetId, "不同路径链接被内容去重");
            Equal(first, items[0].AssetPath);
            Equal(0, Directory.GetFiles(new AppDataPaths(library).Assets).Length);
            var again = imports.ImportFilesAsync("A", new[] { first }, mode: ImageImportMode.Link).GetAwaiter().GetResult().Single();
            Equal(items[0].AssetId, again.AssetId);
            var copies = imports.ImportFilesAsync("B", new[] { first, second }).GetAwaiter().GetResult();
            Equal(copies[0].AssetId, copies[1].AssetId);
            True(copies[0].AssetId != items[0].AssetId, "内部与外部身份合并");
            var invalid = Path.Combine(root, "broken.png"); File.WriteAllText(invalid, "not an image");
            var before = repo.GetItemCountAsync("A").GetAwaiter().GetResult();
            try { imports.ImportFilesAsync("A", new[] { first, invalid }, mode: ImageImportMode.Link).GetAwaiter().GetResult(); throw new Exception("未拒绝损坏批次"); }
            catch (IOException) { }
            Equal(before, repo.GetItemCountAsync("A").GetAwaiter().GetResult());
            imports.ImportFilesAsync("C", new[] { first, second }, mode: ImageImportMode.Link).GetAwaiter().GetResult();
            repo.DeleteItemsAsync(items.Select(i => i.Id).Append(again.Id).ToArray()).GetAwaiter().GetResult();
            foreach (var file in repo.DeleteDrawerAsync("C").GetAwaiter().GetResult()) File.Delete(file);
            True(File.Exists(first) && File.Exists(second), "删除抽屉删除了外部原文件");
            var reopened = new BoardRepository(new AppDataPaths(library)); reopened.InitializeAsync().GetAwaiter().GetResult();
            Equal(AssetSourceKind.Internal, reopened.CaptureSceneAsync("B").GetAwaiter().GetResult().Document.Assets.Single().SourceKind);
        }
        finally { Directory.Delete(root, true); }
    }
    private static void LinkedSceneRoundTrip() => PumpSceneTask(async () =>
    {
        var root = CreateTempDirectory();
        try
        {
            var (repo, imports) = SceneRepository(Path.Combine(root, "library"));
            var original = LinkedTestImage(root);
            var image = (await imports.ImportFilesAsync("A", new[] { original }, mode: ImageImportMode.Link)).Single();
            await imports.ImportFilesAsync("B", new[] { original }, mode: ImageImportMode.Link);
            image.X = 153; image.Rotation = 42; await repo.UpdateItemsAsync(new[] { image });
            var path = Path.Combine(root, "linked.mubo");
            await LinkedSceneSaveService.SaveAsync(repo, imports, "A", path, await repo.CaptureSceneAsync("A"), ExternalImageSaveMode.KeepLinks, null);
            using (var zip = ZipFile.OpenRead(path)) Equal(1, zip.Entries.Count);
            var binding = (await repo.GetSceneBindingAsync("A"))!;
            File.Delete(original);
            using (var scene = await SceneFileService.ReadAsync(path))
            {
                Equal(4, scene.Document.Version);
                Equal(AssetSourceKind.External, scene.Document.Assets.Single().SourceKind);
                var (other, _) = SceneRepository(Path.Combine(root, "other"));
                var id = await other.ImportSceneAsync(null, scene, path);
                Equal(original, (await other.GetItemsAsync(id)).Single().AssetPath);
                var snapshot = await other.CaptureSceneAsync(id);
                True(SceneThumbnailRenderer.Render(snapshot).Length > 0, "缺失图片缩略图失败");
                try { await BoardExportService.ExportCompositeAsync(snapshot, null, Path.Combine(root, "bad.png")); throw new Exception("缺失图片被静默漏导出"); }
                catch (IOException) { }
                var bad = JsonSerializer.Deserialize<SceneDocument>(JsonSerializer.Serialize(scene.Document))!;
                bad.Assets[0] = bad.Assets[0] with { ExternalPath = "relative.png" };
                try { SceneValidation.Validate(bad); throw new Exception("接受相对路径"); } catch (InvalidDataException) { }
            }
            await LinkedSceneSaveService.SaveAsync(repo, imports, "A", path, await repo.CaptureSceneAsync("A"), ExternalImageSaveMode.KeepLinks, binding.FileHash);
            binding = (await repo.GetSceneBindingAsync("A"))!;
            var oldBytes = await File.ReadAllBytesAsync(path);
            try { await LinkedSceneSaveService.SaveAsync(repo, imports, "A", path, await repo.CaptureSceneAsync("A"), ExternalImageSaveMode.Copy, binding.FileHash); throw new Exception("缺失链接复制成功"); }
            catch (IOException) { }
            True(oldBytes.SequenceEqual(await File.ReadAllBytesAsync(path)), "失败覆盖了旧场景");
            Equal(image.AssetId, (await repo.GetItemsAsync("A")).Single().AssetId);
            LinkedTestImage(root);
            True(await LinkedSceneSaveService.SaveAsync(repo, imports, "A", path, await repo.CaptureSceneAsync("A"), ExternalImageSaveMode.Copy, binding.FileHash), "未转换");
            var converted = (await repo.GetItemsAsync("A")).Single();
            True(converted.AssetPath != original && File.Exists(converted.AssetPath), "未生成内部副本");
            Equal(153d, converted.X); Equal(42d, converted.Rotation);
            Equal(original, (await repo.GetItemsAsync("B")).Single().AssetPath);
            using var copied = await SceneFileService.ReadAsync(path);
            Equal(AssetSourceKind.Internal, copied.Document.Assets.Single().SourceKind);
            True(!(await repo.GetDrawersAsync()).Single(d => d.Id == "A").HasUnsavedScene, "保存后仍脏");
        }
        finally { Directory.Delete(root, true); }
        return true;
    });
    private static void LinkedSaveRollback() => PumpSceneTask(async () =>
    {
        var root = CreateTempDirectory();
        try
        {
            var library = Path.Combine(root, "library");
            var (repo, imports) = SceneRepository(library);
            var original = LinkedTestImage(root);
            await imports.ImportFilesAsync("A", new[] { original }, mode: ImageImportMode.Link);
            var path = Path.Combine(root, "rollback.mubo");
            var snapshot = await repo.CaptureSceneAsync("A");
            await LinkedSceneSaveService.SaveAsync(repo, imports, "A", path, snapshot, ExternalImageSaveMode.KeepLinks, null);
            var binding = (await repo.GetSceneBindingAsync("A"))!;
            var bytes = await File.ReadAllBytesAsync(path);
            using var connection = new SqliteConnection("Data Source=" + new AppDataPaths(library).Database);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE save_failure(id TEXT REFERENCES drawers(id) DEFERRABLE INITIALLY DEFERRED); CREATE TRIGGER fail_scene_commit AFTER UPDATE ON scene_bindings BEGIN INSERT INTO save_failure VALUES('does-not-exist'); END;";
            await command.ExecuteNonQueryAsync();
            try { await LinkedSceneSaveService.SaveAsync(repo, imports, "A", path, snapshot, ExternalImageSaveMode.Copy, binding.FileHash); throw new Exception("未触发提交失败"); }
            catch (SqliteException) { }
            Equal(original, (await repo.GetItemsAsync("A")).Single().AssetPath);
            Equal(binding, (await repo.GetSceneBindingAsync("A"))!);
            True(bytes.SequenceEqual(await File.ReadAllBytesAsync(path)), "提交失败未恢复场景");
            Equal(snapshot.Revision, (await repo.CaptureSceneAsync("A")).Revision);
            True(File.Exists(original), "失败破坏原图");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        return true;
    });
    private static void LinkedFileRefresh() => PumpSceneTask(async () =>
    {
        var root = CreateTempDirectory();
        try
        {
            var (repo, imports) = SceneRepository(Path.Combine(root, "library"));
            var original = LinkedTestImage(root);
            var item = (await imports.ImportFilesAsync("A", new[] { original }, mode: ImageImportMode.Link)).Single();
            item.X = 30; item.Y = -80; item.Width = 140; item.Rotation = 73;
            await repo.UpdateItemsAsync(new[] { item });
            using var monitor = new ExternalImageMonitor(repo);
            await monitor.PollAsync(); await Task.Delay(650); await monitor.PollAsync();
            async Task Observe()
            {
                var changed = false;
                for (var n = 0; n < 12 && !changed; n++)
                { await Task.Delay(250); changed = (await monitor.PollAsync()).Any(c => c.Path == original); }
                True(changed, "未收到原图更新");
                var after = (await repo.GetItemsAsync("A")).Single();
                Equal(item.X, after.X); Equal(item.Y, after.Y); Equal(item.Width, after.Width); Equal(item.Rotation, after.Rotation);
            }
            File.Delete(original); LinkedTestImage(root, width: 38); await Observe();
            Equal(38, (await repo.GetLinkedAssetsAsync()).Single().PixelWidth);
            File.Delete(original); await Observe();
            LinkedTestImage(root); await Observe();
            Equal(24, (await repo.GetLinkedAssetsAsync()).Single().PixelWidth);
        }
        finally { Directory.Delete(root, true); }
        return true;
    });
    private static void LinkedImportConfirmation() => WithMainDrawerWindow((window, repository) =>
    {
        var root = CreateTempDirectory();
        try
        {
            var path = LinkedTestImage(root);
            var dialogs = (TestSceneDialogs)typeof(MainWindow).GetField("_sceneDialogs", PrivateInstance)!.GetValue(window)!;
            dialogs.Choices.Enqueue(0);
            AwaitMainTask(window, "ImportFilesAsync", "A", new[] { path });
            Equal(0, repository.GetItemCountAsync("A").GetAwaiter().GetResult());
            dialogs.Choices.Enqueue(2);
            AwaitMainTask(window, "ImportFilesAsync", "A", new[] { path });
            Equal(path, repository.GetItemsAsync("A").GetAwaiter().GetResult().Single().AssetPath);
            AwaitMainTask(window, "HandleDroppedFilesAsync", new[] { path }, null!);
            Equal(1, repository.GetItemCountAsync("A").GetAwaiter().GetResult());
        }
        finally { Directory.Delete(root, true); }
    });
    private static void LinkedConversionUndo() => WithDrawingBoard((board, repository) =>
    {
        var root = CreateTempDirectory();
        try
        {
            var imports = (BoardImportService)typeof(BoardWindow).GetField("_importService", PrivateInstance)!.GetValue(board)!;
            var original = LinkedTestImage(root);
            PumpSceneTask(async () =>
            {
                await imports.ImportFilesAsync("A", new[] { original }, mode: ImageImportMode.Link);
                await board.ReloadAsync();
                var complete = board.PrepareLinkedConversionUndo();
                await LinkedSceneSaveService.SaveAsync(repository, imports, "A", Path.Combine(root, "undo.mubo"),
                    await repository.CaptureSceneAsync("A"), ExternalImageSaveMode.Copy, null);
                await complete();
                return true;
            });
            AwaitDrawing(board, "UndoAsync");
            Equal(original, repository.GetItemsAsync("A").GetAwaiter().GetResult().Single().AssetPath);
            True(repository.GetDrawersAsync().GetAwaiter().GetResult().Single(d => d.Id == "A").HasUnsavedScene, "撤回没有标记未保存");
            AwaitDrawing(board, "RedoAsync");
            True(repository.GetItemsAsync("A").GetAwaiter().GetResult().Single().AssetPath != original, "重做未恢复内部副本");
            True(File.Exists(original), "转换修改了原图");
        }
        finally { Directory.Delete(root, true); }
    });
}

