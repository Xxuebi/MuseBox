using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
namespace ScreenshotCollector.Tests;
internal static partial class Program
{
    private static void MaterialBoardFlagRoundTrip() => PumpSceneTask(async () =>
    {
        var root=CreateTempDirectory();
        try
        {
            True(JsonSerializer.Deserialize<BoardViewport>("{}")!.MaterialAreaEnabled,"旧场景没有默认开启素材区");
            var paths=new AppDataPaths(Path.Combine(root,"library"));
            var repo=new BoardRepository(paths);await repo.InitializeAsync();
            var before=await repo.GetViewportAsync("A");before.PanX=124;before.PanY=-64;before.Zoom=2;
            await repo.SaveViewportAsync(before);
            await repo.SetMaterialAreaEnabledAsync("A",false);
            True((await repo.GetViewportAsync("B")).MaterialAreaEnabled,"影响了其他画板");
            var reopened=new BoardRepository(paths);await reopened.InitializeAsync();
            var view=await reopened.GetViewportAsync("A");
            True(!view.MaterialAreaEnabled,"重启后没有保留独立开关");
            Equal(124d,view.PanX);Equal(-64d,view.PanY);Equal(2d,view.Zoom);
            view.PanX=240;await reopened.SaveViewportAsync(view);
            True(!(await reopened.GetViewportAsync("A")).MaterialAreaEnabled,"视口保存覆盖了开关");
            var file=Path.Combine(root,"flag.mubo");
            var snapshot=await reopened.CaptureSceneAsync("A");
            True(!snapshot.Document.Viewport.MaterialAreaEnabled,"场景快照丢失独立开关");
            await SceneFileService.WriteAsync(file,snapshot);
            var (target,_)=SceneRepository(Path.Combine(root,"target"));
            using(var scene=await SceneFileService.ReadAsync(file))
            {
                True(!scene.Document.Viewport.MaterialAreaEnabled,"文件丢失独立开关");
                var id=await target.ImportSceneAsync(null,scene,file);
                True(!(await target.GetViewportAsync(id)).MaterialAreaEnabled,"导入丢失独立开关");
            }
            var revision=(await reopened.CaptureSceneAsync("A")).Revision;
            await reopened.SetMaterialAreaEnabledAsync("A",true);
            True((await reopened.CaptureSceneAsync("A")).Revision>revision,"修改开关未标记场景变化");
            // Simulate a pre-feature viewport schema and verify the compatibility migration.
            using(var connection=new SqliteConnection(MaterialField<string>(target,"_connectionString")))
            {
                connection.Open();using var command=connection.CreateCommand();
                command.CommandText="DROP TRIGGER scene_viewports_update; ALTER TABLE viewports DROP COLUMN material_area_enabled;";
                command.ExecuteNonQuery();
            }
            await target.InitializeAsync();
            True((await target.GetViewportAsync("A")).MaterialAreaEnabled,"旧库迁移默认值错误");
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
        return true;
    });
}

