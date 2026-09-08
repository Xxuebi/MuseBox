using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Application = System.Windows.Application;
namespace ScreenshotCollector.Tests;
internal static partial class Program
{
    private static T MaterialField<T>(object instance,string name) =>
        (T)instance.GetType().GetField(name,PrivateInstance)!.GetValue(instance)!;
    private static void MaterialRoutingAndPersistence() => PumpSceneTask(async()=>{
        var root=CreateTempDirectory();
        try {
            var (repo,imports)=SceneRepository(Path.Combine(root,"library"));
            var a=LinkedTestImage(root,"a.png"); var b=LinkedTestImage(root,"b.png",60);
            True(JsonSerializer.Deserialize<AppSettings>("{}")!.MaterialAreaEnabled,"旧设置未默认开启");
            True(new AppSettings().Copy().MaterialAreaEnabled,"复制设置丢失默认值");
            var batch=await imports.ImportFilesAsync("A",new[]{a,b},destination:ImageImportDestination.Materials);
            Equal(ImageImportDestination.Materials,((ImageImportBatch)batch).Destination);
            Equal(0,(await repo.GetItemsAsync("A")).Count);
            var first=await repo.GetMaterialsAsync("A");
            await repo.MarkSceneSavedAsync(new SceneBinding("A",Path.Combine(root,"bound.mubo"),0,""));
            True(first.Select(x=>x.LayerName).SequenceEqual(new[]{"a","b"}),"同批次顺序改变");
            using(var bitmap=CreateBitmap()) await imports.ImportBitmapAsync("A",bitmap,destination:ImageImportDestination.Materials);
            var inbox=await repo.GetMaterialsAsync("A");
            Equal(3,inbox.Count);Equal(first[0].Id,inbox[1].Id);
            var board=(await imports.ImportFilesAsync("A",new[]{a})).Single();
            Equal(1,(await repo.GetItemsAsync("A")).Count);
            var reopen=new BoardRepository(new AppDataPaths(Path.Combine(root,"library")));await reopen.InitializeAsync();
            Equal(3,(await reopen.GetMaterialsAsync("A")).Count);Equal(4,await reopen.GetItemCountAsync("A"));
            True(await reopen.GetLatestAssetPathAsync("A") is not null,"素材预览未纳入");
            var scene=await reopen.CaptureSceneAsync("A");Equal(3,scene.Document.Materials.Count);
            Equal(board.Id,scene.Document.Images.Single().Id);
            True((await reopen.GetDrawersAsync()).Single(d=>d.Id=="A").HasUnsavedScene,"素材未标记未保存");
        } finally {SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        return true;
    });
    private static void MaterialMoveTransactions() => PumpSceneTask(async()=>{
        var root=CreateTempDirectory();
        try {
            var (repo,imports)=SceneRepository(Path.Combine(root,"library"));
            var path=LinkedTestImage(root);
            var original=(await imports.ImportFilesAsync("A",new[]{path})).Single();
            original.X=300;original.Y=-100;original.Rotation=30;await repo.UpdateItemsAsync(new[]{original});
            await imports.ImportFilesAsync("A",new[]{path},destination:ImageImportDestination.Materials);
            using(var bitmap=CreateBitmap())await imports.ImportBitmapAsync("A",bitmap,destination:ImageImportDestination.Materials);
            var before=await repo.CaptureSceneAsync("A");
            var move=await MaterialAreaService.PlanMoveAsync(repo,"A",center:new Point(500,250));
            var images=move.AfterImages;
            Equal(500d,(images.Min(x=>x.X)+images.Max(x=>x.X+x.Width))/2,.01);
            Equal(250d,(images.Min(x=>x.Y)+images.Max(x=>x.Y+x.Height))/2,.01);
            True(images.All(i=>i.GroupId=="" && i.ZIndex>original.ZIndex),"素材没有放在根级最前层");
            await repo.ApplyMaterialChangesAsync(new[]{move});
            await imports.ImportFilesAsync("A",new[]{path},destination:ImageImportDestination.Materials);
            var arrival=(await repo.GetMaterialsAsync("A")).Single();
            await repo.ApplyMaterialChangesAsync(new[]{move.Inverse()});
            Equal(3,(await repo.GetMaterialsAsync("A")).Count);
            Equal(arrival.Id,(await repo.GetMaterialsAsync("A"))[0].Id);
            await repo.ApplyMaterialChangesAsync(new[]{move});
            Equal(arrival.Id,(await repo.GetMaterialsAsync("A")).Single().Id);
            var retained=(await repo.GetItemsAsync("A")).Single(i=>i.Id==original.Id);
            Equal(original.X,retained.X);Equal(original.Rotation,retained.Rotation);
            var right=await MaterialAreaService.PlanMoveAsync(repo,"A");
            var bounds=SceneThumbnailRenderer.GetBoardBounds(await repo.CaptureSceneAsync("A"));
            Equal(bounds.Right+32,right.AfterImages.Min(i=>i.X),.01);
            await imports.ImportFilesAsync("B",new[]{path},destination:ImageImportDestination.Materials);
            var other=await MaterialAreaService.PlanMoveAsync(repo,"B");
            var snapshot=await repo.CaptureSceneAsync("A");
            try { await repo.ApplyMaterialChangesAsync(new[]{right,other},()=>throw new IOException("settings failure"));throw new Exception("未回滚"); }catch(IOException){}
            Equal(snapshot.Revision,(await repo.CaptureSceneAsync("A")).Revision);
            Equal(1,(await repo.GetMaterialsAsync("A")).Count);Equal(1,(await repo.GetMaterialsAsync("B")).Count);
            Equal(0,(await repo.GetItemsAsync("B")).Count);
        }finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        return true;
    });
    private static void MaterialSceneRoundTrip() => PumpSceneTask(async()=>{
        var root=CreateTempDirectory();
        try{
            var (repo,imports)=SceneRepository(Path.Combine(root,"library"));
            var path=LinkedTestImage(root);
            await imports.ImportFilesAsync("A",new[]{path},mode:ImageImportMode.Link,destination:ImageImportDestination.Materials);
            var snapshot=await repo.CaptureSceneAsync("A");
            Equal(4,snapshot.Document.Version);
            True(SceneThumbnailRenderer.Render(snapshot).Length>0,"纯素材场景没有预览");
            var file=Path.Combine(root,"material.mubo");
            await LinkedSceneSaveService.SaveAsync(repo,imports,"A",file,snapshot,ExternalImageSaveMode.KeepLinks,null);
            File.Delete(path);
            using(var scene=await SceneFileService.ReadAsync(file)){
                var (other,_)=SceneRepository(Path.Combine(root,"other"));
                var id=await other.ImportSceneAsync(null,scene,file);
                Equal(path,(await other.GetMaterialsAsync(id)).Single().AssetPath);
                Equal(0,(await other.GetItemsAsync(id)).Count);
                var missing=await other.CaptureSceneAsync(id);
                True(SceneThumbnailRenderer.Render(missing).Length>0,"缺失素材预览失败");
                var move=await MaterialAreaService.PlanMoveAsync(other,id);
                await other.ApplyMaterialChangesAsync(new[]{move});
                Equal(path,(await other.GetItemsAsync(id)).Single().AssetPath);
                var bad=JsonSerializer.Deserialize<SceneDocument>(JsonSerializer.Serialize(snapshot.Document))!;
                bad.Materials.Add(bad.Materials[0].Clone());
                try{SceneValidation.Validate(bad);throw new Exception("接受重复素材");}catch(InvalidDataException){}
                bad.Materials.RemoveAt(1);bad.Materials[0].AssetId="missing";
                try{SceneValidation.Validate(bad);throw new Exception("接受缺失资源引用");}catch(InvalidDataException){}
            }
            using(var scene=await SceneFileService.ReadAsync(file)){
                scene.MoveMaterialsToBoard=true;
                var id=await repo.ImportSceneAsync("B",scene,file);
                Equal(0,(await repo.GetMaterialsAsync(id)).Count);Equal(1,(await repo.GetItemsAsync(id)).Count);
                True((await repo.GetDrawersAsync()).Single(d=>d.Id==id).HasUnsavedScene,"关闭开关载入素材未标脏");
            }
            LinkedTestImage(root);
            await LinkedSceneSaveService.SaveAsync(repo,imports,"A",file,await repo.CaptureSceneAsync("A"),ExternalImageSaveMode.Copy,null);
            True((await repo.GetMaterialsAsync("A")).Single().AssetPath!=path,"素材链接没有转内部");
            Equal(path,(await repo.GetItemsAsync("B")).Single().AssetPath);
            using var copied=await SceneFileService.ReadAsync(file);
            Equal(1,copied.Document.Materials.Count);Equal(AssetSourceKind.Internal,copied.Document.Assets.Single().SourceKind);
            var legacy=new SceneDocument{Version=3};SceneMigration.UpgradeToCurrent(legacy);Equal(0,legacy.Materials.Count);Equal(4,legacy.Version);
        }finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        return true;
    });
    private static void MaterialExportScope() => PumpSceneTask(async()=>{
        var root=CreateTempDirectory();
        try{
            var (repo,imports)=SceneRepository(Path.Combine(root,"library"));
            var path=LinkedTestImage(root);
            var first=(await imports.ImportFilesAsync("A",new[]{path})).Single();
            var second=(await imports.ImportFilesAsync("A",new[]{path})).Single();
            await imports.ImportFilesAsync("A",new[]{path},destination:ImageImportDestination.Materials);
            var snapshot=await repo.CaptureSceneAsync("A");
            var opts=new ImageExportOptions(root,ImageExportFormat.Original,"%02i - %n");
            var plan=ImageExportTemplateService.CreatePlan(new ImageExportRequest(snapshot,null,"图片"),opts);
            Equal(3,plan.Entries.Count);Equal(second.Id,plan.Entries[0].Item.Id);Equal(first.Id,plan.Entries[1].Item.Id);
            Equal(snapshot.Document.Materials[0].Id,plan.Entries[2].Item.Id);
            Equal(1,ImageExportTemplateService.CreatePlan(new ImageExportRequest(snapshot,new HashSet<string>{first.Id},"图片"),opts).Entries.Count);
            await repo.DeleteItemsAsync(new[]{first.Id,second.Id});
            var only=await repo.CaptureSceneAsync("A");
            Equal(1,ImageExportTemplateService.CreatePlan(new ImageExportRequest(only,null,"图片"),opts).Entries.Count);
            Equal(0,only.Document.Images.Count);
        }finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        return true;
    });
    private static void MaterialHistoryAndSelection() => WithDrawingBoard((board,repo)=>{
        var root=CreateTempDirectory();
        try{
            var imports=MaterialField<BoardImportService>(board,"_importService");
            var path=LinkedTestImage(root);
            PumpSceneTask(async()=>{
                await imports.ImportFilesAsync("A",new[]{path},destination:ImageImportDestination.Materials);
                await board.ReloadAsync();return true;
            });
            var id=repo.GetMaterialsAsync("A").GetAwaiter().GetResult().Single().Id;
            var panel=MaterialField<MaterialAreaPanel>(board,"_materialPanel");
            panel.Select(id,ModifierKeys.None);
            Equal(0,MaterialField<HashSet<string>>(board,"_selected").Count);
            AwaitDrawing(board,"ApplyMaterialActionAsync",new HashSet<string>{id},false,new Point(400,300));
            Equal(id,repo.GetItemsAsync("A").GetAwaiter().GetResult().Single().Id);
            AwaitDrawing(board,"UndoAsync");Equal(0,repo.GetItemsAsync("A").GetAwaiter().GetResult().Count);
            AwaitDrawing(board,"RedoAsync");Equal(0,repo.GetMaterialsAsync("A").GetAwaiter().GetResult().Count);
            AwaitDrawing(board,"UndoAsync");
            AwaitDrawing(board,"ApplyMaterialActionAsync",new HashSet<string>{id},true,null!);
            Equal(0,repo.GetMaterialsAsync("A").GetAwaiter().GetResult().Count);
            PumpSceneTask(async()=>{await imports.ImportFilesAsync("A",new[]{path},destination:ImageImportDestination.Materials);await board.ReloadAsync();return true;});
            AwaitDrawing(board,"UndoAsync");
            Equal(2,repo.GetMaterialsAsync("A").GetAwaiter().GetResult().Count);
            True(File.Exists(path),"删除素材删除了外部文件");
            SetDrawingField(board,"_materialsExpanded",false);CallDrawing(board,"UpdateMaterialAreaVisibility");
            PumpSceneTask(async()=>{await imports.ImportFilesAsync("A",new[]{path},destination:ImageImportDestination.Materials);await board.ReloadAsync();return true;});
            Equal(Visibility.Collapsed,panel.Visibility);
            True(MaterialField<System.Windows.Controls.Button>(board,"_materialTab").ToolTip.ToString()!.Contains("3"),"数量未刷新");
        }finally{Directory.Delete(root,true);}
    });
    private sealed class MaterialMemorySettings : ISettingsService
    {
        public bool Fail { get; set; }
        public AppSettings Value=new();
        public Task<AppSettings> LoadAsync(CancellationToken token=default)=>Task.FromResult(Value.Copy());
        public Task SaveAsync(AppSettings settings,CancellationToken token=default)
        {if(Fail)throw new IOException("模拟设置保存失败");Value=settings.Copy();return Task.CompletedTask;}
    }
    private static void MaterialGlobalDisable() => WithMainDrawerWindow((main,repo)=>{
        var root=CreateTempDirectory();
        try{
            repo.SetMaterialAreaEnabledAsync("A",true).GetAwaiter().GetResult();
            repo.SetMaterialAreaEnabledAsync("B",true).GetAwaiter().GetResult();
            var imports=MaterialField<BoardImportService>(main,"_importService");
            var path=LinkedTestImage(root);
            AwaitMainTask(main,"ImportFilesAsync","A",new[]{path});
            AwaitMainTask(main,"ImportFilesAsync","B",new[]{path});
            Equal(0,repo.GetItemsAsync("A").GetAwaiter().GetResult().Count);
            var dialogs=MaterialField<TestSceneDialogs>(main,"_sceneDialogs");
            dialogs.Choices.Enqueue(0);AwaitMainTask(main,"SetMaterialAreaEnabledAsync","A",false);
            True(repo.GetViewportAsync("A").Result.MaterialAreaEnabled,"取消修改了当前画板开关");
            Equal(1,repo.GetMaterialsAsync("A").Result.Count);
            AwaitMainTask(main,"SetMaterialAreaEnabledAsync","A",false);
            Equal(0,repo.GetMaterialsAsync("A").Result.Count);Equal(1,repo.GetItemsAsync("A").Result.Count);
            Equal(1,repo.GetMaterialsAsync("B").Result.Count);Equal(0,repo.GetItemsAsync("B").Result.Count);
            True(!repo.GetViewportAsync("A").Result.MaterialAreaEnabled && repo.GetViewportAsync("B").Result.MaterialAreaEnabled,"开关未按画板隔离");
            AwaitMainTask(main,"ImportFilesAsync","A",new[]{path});
            AwaitMainTask(main,"ImportFilesAsync","B",new[]{path});
            Equal(2,repo.GetItemsAsync("A").Result.Count);Equal(2,repo.GetMaterialsAsync("B").Result.Count);
            var history=MaterialAreaSession.For(repo).Take("A");Equal(1,history.Count);
            PumpSceneTask(async()=>{await repo.ApplyMaterialChangesAsync(new[]{history[0].Inverse()});return true;});
            Equal(1,repo.GetMaterialsAsync("A").Result.Count);
            True(!repo.GetViewportAsync("A").Result.MaterialAreaEnabled,"撤回改变了开关");
            Equal(0,MaterialAreaSession.For(repo).Take("B").Count);
            AwaitMainTask(main,"SetMaterialAreaEnabledAsync","A",true);
            True(repo.GetViewportAsync("A").Result.MaterialAreaEnabled,"重新开启失败");
        }finally{Directory.Delete(root,true);}
    });
    private static void MaterialLinkedRefreshAndUndo() => WithDrawingBoard((board,repo)=>{
        var root=CreateTempDirectory();
        try{
            PumpSceneTask(async()=>{
                var imports=MaterialField<BoardImportService>(board,"_importService");
                var path=WriteTestGif(root);
                await imports.ImportFilesAsync("A",new[]{path},mode:ImageImportMode.Link,destination:ImageImportDestination.Materials);
                await board.ReloadAsync();
                using var monitor=new ExternalImageMonitor(repo);
                await monitor.PollAsync();await Task.Delay(650);await monitor.PollAsync();
                var initial=(await repo.CaptureSceneAsync("A")).Revision;
                File.Delete(path);
                var changed=false;
                for(var n=0;n<12 && !changed;n++){await Task.Delay(250);changed=(await monitor.PollAsync()).Any(c=>c.DrawerId=="A");}
                True(changed,"纯素材外链删除没有通知");
                True((await repo.CaptureSceneAsync("A")).Revision>initial,"外链变化未标记素材场景");
                File.Copy(LinkedTestImage(root),path);ImageFileFormatService.Invalidate(path);
                changed=false;
                for(var n=0;n<12 && !changed;n++){await Task.Delay(250);changed=(await monitor.PollAsync()).Any(c=>c.DrawerId=="A");}
                True(changed,"纯素材外链恢复没有通知");
                await board.ReloadAsync();
                var complete=board.PrepareLinkedConversionUndo();
                await LinkedSceneSaveService.SaveAsync(repo,imports,"A",Path.Combine(root,"undo.mubo"),
                    await repo.CaptureSceneAsync("A"),ExternalImageSaveMode.Copy,null);
                await complete();
                True((await repo.GetMaterialsAsync("A")).Single().AssetPath!=path,"素材转换失败");
                return true;
            });
            AwaitDrawing(board,"UndoAsync");
            var path=repo.GetMaterialsAsync("A").GetAwaiter().GetResult().Single().AssetPath;
            True(Path.GetDirectoryName(path)==root,"素材转换撤回没有恢复链接");
            AwaitDrawing(board,"RedoAsync");True(repo.GetMaterialsAsync("A").GetAwaiter().GetResult().Single().AssetPath!=path,"素材转换重做失败");
            True(File.Exists(path),"转换或撤回修改了原图");
        }finally{Directory.Delete(root,true);}
    });
    private static void MaterialCommitFailureRestoresSettings() => WithMainDrawerWindow((main,repo)=>{
        var root=CreateTempDirectory();
        try{
            repo.SetMaterialAreaEnabledAsync("A",true).GetAwaiter().GetResult();
            var path=LinkedTestImage(root);
            AwaitMainTask(main,"ImportFilesAsync","A",new[]{path});
            using var connection=new SqliteConnection(MaterialField<string>(repo,"_connectionString"));connection.Open();
            using var command=connection.CreateCommand();
            command.CommandText="CREATE TABLE material_failure(id TEXT REFERENCES drawers(id) DEFERRABLE INITIALLY DEFERRED); CREATE TRIGGER fail_material_commit AFTER INSERT ON items BEGIN INSERT INTO material_failure VALUES('not-present'); END;";
            command.ExecuteNonQuery();
            var revision=repo.CaptureSceneAsync("A").Result.Revision;
            AwaitMainTask(main,"SetMaterialAreaEnabledAsync","A",false);
            Equal(1,repo.GetMaterialsAsync("A").Result.Count);Equal(0,repo.GetItemsAsync("A").Result.Count);
            True(repo.GetViewportAsync("A").Result.MaterialAreaEnabled,"数据库失败后开关未恢复");
            Equal(revision,repo.CaptureSceneAsync("A").Result.Revision);
            Equal(0,MaterialAreaSession.For(repo).Take("A").Count);
        }finally{Directory.Delete(root,true);}
    });
    private static void MaterialPendingHistoryAndDeleteSafety() => WithDrawingBoard((board,repo)=>{
        var root=CreateTempDirectory();
        try{
            PumpSceneTask(async()=>{
                var imports=MaterialField<BoardImportService>(board,"_importService");
                var path=LinkedTestImage(root);
                await imports.ImportFilesAsync("A",new[]{path},mode:ImageImportMode.Link,destination:ImageImportDestination.Materials);
                await imports.ImportFilesAsync("B",new[]{path},mode:ImageImportMode.Link,destination:ImageImportDestination.Materials);
                var change=await MaterialAreaService.PlanMoveAsync(repo,"A");await repo.ApplyMaterialChangesAsync(new[]{change});
                MaterialAreaSession.For(repo).Queue(change);await repo.SetMaterialAreaEnabledAsync("A",false);
                await board.ReloadAsync();
                var files=await repo.DeleteDrawerAsync("B");True(files.Count==0 && File.Exists(path),"删除抽屉删除外部原图");
                return true;
            });
            AwaitDrawing(board,"UndoAsync");
            Equal(1,repo.GetMaterialsAsync("A").GetAwaiter().GetResult().Count);
            True(!repo.GetViewportAsync("A").Result.MaterialAreaEnabled,"接入历史撤回开启了素材区");
            True(((System.Windows.Controls.TextBlock)board.FindName("BoardStatus")).Text.Contains("重新开启"),"撤回缺少查看素材的提示");
            AwaitDrawing(board,"RedoAsync");Equal(1,repo.GetItemsAsync("A").GetAwaiter().GetResult().Count);
        }finally{Directory.Delete(root,true);}
    });
    private static void MaterialPanelLayout()
    {
        var root=CreateTempDirectory();
        var original=ThemeService.CurrentMode;
        try{
            var path=LinkedTestImage(root);
            foreach(var theme in new[]{AppAppearanceMode.Light,AppAppearanceMode.Dark}){
                ThemeService.Apply(Application.Current,theme);
                var panel=new MaterialAreaPanel(new object(),"A"){Width=300,Height=480};
                var items=Enumerable.Range(0,1000).Select(i=>new BoardMaterialItem{Id="m"+i,LayerName="素材 "+i,AssetPath=path}).ToArray();
                panel.SetItems(items);
                var host=new Window{Content=panel,SizeToContent=SizeToContent.WidthAndHeight,WindowStyle=WindowStyle.None,Opacity=0,ShowActivated=false};
                try{
                    host.Show();host.UpdateLayout();
                    PumpSceneTask(async()=>{await Task.Delay(100);return true;});
                    panel.Select("m0",ModifierKeys.None);panel.Select("m2",ModifierKeys.Shift);Equal(3,panel.SelectedIds.Count);
                    panel.Select("m1",ModifierKeys.Control);Equal(2,panel.SelectedIds.Count);
                    True(MainDescendants(panel).OfType<ListBoxItem>().Count()<20,"素材列表未虚拟化");
                    var scroll=MainDescendants(panel.Rows).OfType<ScrollViewer>().First();
                    True(scroll.ScrollableHeight>0,"素材没有滚动");
                    var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=UIElement.PreviewMouseWheelEvent};
                    panel.RaiseEvent(wheel);host.UpdateLayout();
                    True(wheel.Handled && scroll.VerticalOffset>0,"素材滚轮没有隔离");
                    PumpSceneTask(async()=>{await Task.Delay(150);return true;});
                    SaveSettingsSnapshot(host,$"materials-{theme}.png");
                    panel.Width=220;panel.Height=240;panel.Reflow();host.UpdateLayout();
                    SaveSettingsSnapshot(host,$"materials-narrow-{theme}.png");
                    True(panel.Rows.ActualWidth<220,"窄面板溢出");
                    Equal(3,MainDescendants(panel).OfType<Thumb>().Count(t=>t.Cursor is not null));
                }finally{host.Close();}
            }
        }finally{ThemeService.Apply(Application.Current,original);Directory.Delete(root,true);}
    }
}

