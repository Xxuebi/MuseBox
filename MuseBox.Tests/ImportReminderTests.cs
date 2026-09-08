using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Application = System.Windows.Application;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
namespace ScreenshotCollector.Tests;
internal static partial class Program
{
    private static void ImageImportPreferenceIsolation() => WithMainDrawerWindow((main,repo)=>
    {
        var root=CreateTempDirectory();
        try
        {
            var dialogs=MaterialField<TestSceneDialogs>(main,"_sceneDialogs");
            var path=LinkedTestImage(root);
            dialogs.RememberImportChoice=true;dialogs.Choices.Enqueue(2);
            AwaitMainTask(main,"ImportFilesAsync","A",new[]{path});
            Equal(new ImageImportPreferences(false,ImageImportMode.Link),repo.GetImageImportPreferencesAsync("A").Result);
            Equal(path,repo.GetItemsAsync("A").Result.Single().AssetPath);
            var count=dialogs.ImportPromptCount;
            AwaitMainTask(main,"ImportFilesAsync","A",new[]{path});
            Equal(count,dialogs.ImportPromptCount);
            True(repo.GetItemsAsync("A").Result.All(i=>i.AssetPath==path),"未使用已记住的链接方式");
            dialogs.Choices.Enqueue(0);
            AwaitMainTask(main,"ImportFilesAsync","B",new[]{path});
            Equal(0,repo.GetItemsAsync("B").Result.Count);
            True(repo.GetImageImportPreferencesAsync("B").Result.AskEveryTime,"取消却记住了选择");
            dialogs.Choices.Enqueue(2);
            AwaitMainTask(main,"ImportFilesAsync","B",new[]{path,System.IO.Path.Combine(root,"missing.png")});
            Equal(0,repo.GetItemsAsync("B").Result.Count);
            True(repo.GetImageImportPreferencesAsync("B").Result.AskEveryTime,"失败却关闭了提醒");
            dialogs.Choices.Enqueue(1);
            AwaitMainTask(main,"ImportFilesAsync","B",new[]{path});
            Equal(new ImageImportPreferences(false,ImageImportMode.Copy),repo.GetImageImportPreferencesAsync("B").Result);
            True(repo.GetItemsAsync("B").Result.Single().AssetPath!=path,"复制方式没有复制图片");
            var dbRoot=System.IO.Path.GetDirectoryName(new SqliteConnectionStringBuilder(MaterialField<string>(repo,"_connectionString")).DataSource)!;
            var reopened=new BoardRepository(new AppDataPaths(dbRoot));reopened.InitializeAsync().GetAwaiter().GetResult();
            Equal(new ImageImportPreferences(false,ImageImportMode.Link),reopened.GetImageImportPreferencesAsync("A").Result);
            reopened.SaveImageImportPreferencesAsync("A",new(true,ImageImportMode.Link)).GetAwaiter().GetResult();
            dialogs.RememberImportChoice=false;dialogs.Choices.Enqueue(1);
            count=dialogs.ImportPromptCount;AwaitMainTask(main,"ImportFilesAsync","A",new[]{path});
            Equal(count+1,dialogs.ImportPromptCount);
            True(repo.GetImageImportPreferencesAsync("A").Result.AskEveryTime,"重新提醒后再次被关闭");
            True(!repo.GetImageImportPreferencesAsync("B").Result.AskEveryTime,"修改A影响了B");
        }
        finally {Directory.Delete(root,true);}
    });
    private static void ImportReminderSettingsUi()
    {
        True(new AppSettings().AutoSaveEnabled && JsonSerializer.Deserialize<AppSettings>("{}")!.AutoSaveEnabled,"自动保存未默认开启");
        True(!JsonSerializer.Deserialize<AppSettings>("{\"AutoSaveEnabled\":false}")!.AutoSaveEnabled,"覆盖了已有关闭设置");
        var settings=new SettingsWindow(new AppSettings()){Opacity=0,ShowActivated=false};
        try
        {
            settings.Show();settings.UpdateLayout();
            var tabs=(TabControl)settings.FindName("SettingsCategories");
            tabs.SelectedItem=settings.FindName("SaveLoadSettingsTab");
            settings.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);settings.UpdateLayout();
            var input=(TextBox)settings.FindName("StoragePathTextBox");
            True(!input.IsKeyboardFocusWithin && input.SelectionLength==0,"进入保存页自动编辑路径");
            True(input.Focusable && !input.IsReadOnly,"路径框不能手动编辑");
            input.Text="test";input.SelectAll();
            tabs.SelectedIndex=0;tabs.SelectedIndex=3;
            settings.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
            Equal(0,input.SelectionLength);
            True(settings.FindName("AskLinkedSaveCheck") is null,"旧外部链接设置仍然显示");
            tabs.SelectedIndex=1;settings.UpdateLayout();
            True(!MainDescendants((DependencyObject)((TabItem)tabs.SelectedItem).Content).OfType<TextBlock>().Any(t=>t.Text=="目前仅提供简体中文"),"应用语言描述仍存在");
            SaveSettingsSnapshot(settings,"settings-language-v135.png");
            tabs.SelectedIndex=3;settings.UpdateLayout();
            SaveSettingsSnapshot(settings,"settings-save-v135.png");
        } finally {settings.Close();}
        var board=new BoardSettingsWindow("#808080",1,askImageImportMode:false){Opacity=0,ShowActivated=false};
        try
        {
            board.Show();board.UpdateLayout();
            var check=(CheckBox)board.FindName("ImageImportReminderCheck");
            True(check.IsChecked==false,"没有加载抽屉提醒状态");
            check.IsChecked=true;check.UpdateLayout();
            True(((TextBlock)check.Template.FindName("Tick",check)).Visibility==Visibility.Visible,"勾选标记未显示");
            SaveSettingsSnapshot(board,"board-import-reminder-v135.png");
            board.Hide();board.Dispatcher.BeginInvoke(new Action(()=>typeof(BoardSettingsWindow).GetMethod("OnApplyClick",PrivateInstance)!.Invoke(board,new object[]{board,new RoutedEventArgs()})));
            True(board.ShowDialog()==true && board.AskImageImportMode,"应用没有返回重新提醒");
        } finally {board.Close();}
        var owner=new Window(){Opacity=0,ShowActivated=false};owner.Show();
        try
        {
            owner.Dispatcher.BeginInvoke(new Action(()=>
            {
                var dialog=Application.Current.Windows.OfType<PromptWindow>().Single(w=>w.Title=="导入图像");
                var check=(CheckBox)dialog.FindName("RememberChoice");
                Equal("保存选择，下次不提醒",check.Content.ToString()!);
                True(check.IsChecked!=true,"默认记住了导入方式");
                var message=((TextBlock)dialog.FindName("PromptMessage")).Text;
                True(message.Contains("复制进画板：将图像复制进画板，不依赖原文件") &&
                     message.Contains("链接原文件：仅链接图片，不复制进画板，移动或删除原文件将使链接失效"),"导入说明不符");
                check.IsChecked=true;check.UpdateLayout();
                True(((TextBlock)check.Template.FindName("Tick",check)).Visibility==Visibility.Visible,"记忆选项勾选标记未显示");
                SaveSettingsSnapshot(dialog,"image-import-remember-v135.png");
                ((Button)dialog.FindName("PromptAlternative")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }),DispatcherPriority.ContextIdle);
            Equal((2,true),new SceneDialogs().ChooseImageImport(owner,"抽屉 A",3));
        } finally {owner.Close();}
    }
    private static void GifStaleStateSaveRecovery() => PumpSceneTask(async()=>
    {
        var root=CreateTempDirectory();
        try
        {
            var (repo,imports)=SceneRepository(System.IO.Path.Combine(root,"library"));
            var gif=WriteTestGif(root);
            var item=(await imports.ImportFilesAsync("A",new[]{gif})).Single();
            await repo.SaveGifStatesAsync(new[]{new GifSceneState(item.Id,2,false,2)});
            var original=await repo.CaptureSceneAsync("A");
            Equal(1,original.Document.Gifs.Count);
            var png=await imports.StageInternalAssetAsync(LinkedTestImage(root));
            item.AssetId=png.Asset.Id;await repo.UpdateItemsAsync(new[]{item});
            var staticSnapshot=await repo.CaptureSceneAsync("A");
            Equal(0,staticSnapshot.Document.Gifs.Count);
            SceneValidation.Validate(staticSnapshot.Document);
            await SceneFileService.WriteAsync(System.IO.Path.Combine(root,"static.mubo"),staticSnapshot);
            // A historical mislabeled internal GIF must keep its real animation state.
            var animated=(await imports.ImportFilesAsync("B",new[]{gif})).Single();
            await repo.SaveGifStatesAsync(new[]{new GifSceneState(animated.Id,.5,false,1)});
            using(var connection=new SqliteConnection(MaterialField<string>(repo,"_connectionString")))
            {
                connection.Open();using var command=connection.CreateCommand();
                command.CommandText="UPDATE assets SET extension='.png' WHERE id=$id";command.Parameters.AddWithValue("$id",animated.AssetId);
                command.ExecuteNonQuery();
            }
            var repaired=await repo.CaptureSceneAsync("B");
            Equal(".gif",repaired.Document.Assets.Single().Extension);Equal(1,repaired.Document.Gifs.Count);
            Equal(.5,repaired.Document.Gifs[0].Speed);
            await SceneFileService.WriteAsync(System.IO.Path.Combine(root,"animated.mubo"),repaired);
            // Replacement between capture and freeze must also drop only obsolete GIF metadata.
            var linked=(await imports.ImportFilesAsync("C",new[]{gif},mode:ImageImportMode.Link)).Single();
            await repo.SaveGifStatesAsync(new[]{new GifSceneState(linked.Id,1,true,2)});
            var before=await repo.CaptureSceneAsync("C");
            File.Copy(LinkedTestImage(root),gif,true);ImageFileFormatService.Invalidate(gif);
            var file=System.IO.Path.Combine(root,"replaced.mubo");
            await LinkedSceneSaveService.SaveAsync(repo,imports,"C",file,before,ExternalImageSaveMode.KeepLinks,null);
            using(var opened=await SceneFileService.ReadAsync(file))
            { Equal(0,opened.Document.Gifs.Count);Equal(".png",opened.Document.Assets.Single().Extension); }
            var copiedFile=System.IO.Path.Combine(root,"replaced-copy.mubo");
            await LinkedSceneSaveService.SaveAsync(repo,imports,"C",copiedFile,before,ExternalImageSaveMode.Copy,null);
            using(var opened=await SceneFileService.ReadAsync(copiedFile))
            {Equal(0,opened.Document.Gifs.Count);True(opened.Document.Assets.All(a=>a.SourceKind==AssetSourceKind.Internal),"复制保存没有内嵌静态替换资源");}
            using(var opened=await SceneFileService.ReadAsync(System.IO.Path.Combine(root,"animated.mubo")))
            {Equal(1,opened.Document.Gifs.Count);Equal(1,opened.Document.Gifs[0].FrameIndex);Equal(.5,opened.Document.Gifs[0].Speed);}
            // Incoming invalid scenes still fail validation; only local snapshots are repaired.
            staticSnapshot.Document.Gifs.Add(new GifSceneState(item.Id,1,true,0));
            try {SceneValidation.Validate(staticSnapshot.Document);throw new Exception("接受了无效GIF场景");}
            catch(InvalidDataException){}
        }
        finally {SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
        return true;
    });
}

