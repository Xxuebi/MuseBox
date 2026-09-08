using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Application = System.Windows.Application;
using DataFormats = System.Windows.DataFormats;
using TabControl = System.Windows.Controls.TabControl;
namespace ScreenshotCollector.Tests;
internal static partial class Program
{
    private static void BoardGifClipboardPreservesAnimation()
    {
        var root=CreateTempDirectory();
        try
        {
            var original=WriteTestGif(root);
            var bytes=File.ReadAllBytes(original);
            var data=BoardClipboardService.CreateDataObject(original,Path.Combine(root,"cache"));
            True(!data.GetDataPresent(DataFormats.Bitmap,false) && !data.GetDataPresent("PNG",false),"GIF 仍提供会被优先选用的静态 PNG");
            foreach(var format in new[]{"GIF","image/gif"})
                True(((MemoryStream)data.GetData(format,false)).ToArray().SequenceEqual(bytes),"GIF 原始数据改变");
            var file=data.GetFileDropList()[0]!;
            True(file!=original && Path.GetExtension(file)==".gif","没有独立的原格式文件");
            True(File.ReadAllBytes(file).SequenceEqual(bytes),"动图帧或延时发生改变");
            var html=(string)data.GetData(DataFormats.Html);
            var htmlBytes=Encoding.UTF8.GetBytes(html);
            int Offset(string name)=>int.Parse(html.Split("\r\n").Single(s=>s.StartsWith(name+":")).Split(':')[1]);
            var fragment=Encoding.UTF8.GetString(htmlBytes[Offset("StartFragment")..Offset("EndFragment")]);
            True(fragment.StartsWith("<img ") && fragment.Contains(".gif"),"HTML 剪贴板片段无效");
            Equal(htmlBytes.Length,Offset("EndHTML"));
            // Exercise the native data representation without replacing the user's clipboard.
            var com=(System.Runtime.InteropServices.ComTypes.IDataObject)data;
            var formatEtc=new FORMATETC{cfFormat=(short)DataFormats.GetDataFormat("GIF").Id,dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1,tymed=TYMED.TYMED_HGLOBAL};
            com.GetData(ref formatEtc,out var medium);
            try
            {
                var pointer=ClipboardTestGlobalLock(medium.unionmember);
                try
                {
                    var signature=new byte[6];Marshal.Copy(pointer,signature,0,6);
                    Equal("GIF89a",Encoding.ASCII.GetString(signature));
                }
                finally{ClipboardTestGlobalUnlock(medium.unionmember);}
            }
            finally{ClipboardTestReleaseMedium(ref medium);}
            File.Delete(original);
            True(File.Exists(file),"复制依赖可能被删除的原文件");
            var png=LinkedTestImage(root);
            var still=BoardClipboardService.CreateDataObject(png,Path.Combine(root,"cache"));
            True(still.ContainsImage() && !still.GetDataPresent("GIF",false),"普通图片复制行为改变");
        }
        finally{Directory.Delete(root,true);}
    }
    [DllImport("kernel32.dll",EntryPoint="GlobalLock")] private static extern IntPtr ClipboardTestGlobalLock(IntPtr handle);
    [DllImport("kernel32.dll",EntryPoint="GlobalUnlock")] private static extern bool ClipboardTestGlobalUnlock(IntPtr handle);
    [DllImport("ole32.dll",EntryPoint="ReleaseStgMedium")] private static extern void ClipboardTestReleaseMedium(ref STGMEDIUM medium);

    private static void BoardMaterialSettingsAndGeneralHints()
    {
        var old=ThemeService.CurrentMode;
        try
        {
            foreach(var mode in new[]{AppAppearanceMode.Light,AppAppearanceMode.Dark})
            {
                ThemeService.Apply(Application.Current,mode);
                var main=new SettingsWindow(new AppSettings()){Opacity=0,ShowActivated=false};
                try
                {
                    main.Show();main.UpdateLayout();
                    True(main.FindName("MaterialAreaToggle") is null,"素材开关仍在小窗常规页");
                    var content=(DependencyObject)((TabItem)((TabControl)main.FindName("SettingsCategories")).Items[0]).Content;
                    var texts=MainDescendants(content).OfType<TextBlock>().ToArray();
                    True(texts.Where(t=>t.FontSize==12 && t.Text.Length>4).All(t=>t.Visibility==Visibility.Collapsed),"常规说明仍占用页面空间");
                    var hints=MainDescendants(content).OfType<Grid>().Where(g=>g.ToolTip is string).ToArray();
                    True(hints.Length==3 && hints.Any(g=>g.ToolTip.ToString()!.Contains("截图")),"功能缺少悬停说明");
                    True(main.FindName("MainTopmostToggle") is null,"默认置顶仍在设置");
                    True(!MainDescendants(content).Contains(main.FindName("AutoSaveToggle")),"自动保存仍在常规页");
                    True(hints.All(g=>g.ActualHeight<=46),"常规功能行不够紧凑");
                    SaveSettingsSnapshot(main,$"general-hints-{mode}.png");
                    var tabs=(TabControl)main.FindName("SettingsCategories");
                    tabs.SelectedItem=main.FindName("SaveLoadSettingsTab");main.UpdateLayout();
                    var saveContent=(DependencyObject)((TabItem)tabs.SelectedItem).Content;
                    True(MainDescendants(saveContent).Contains(main.FindName("AutoSaveToggle")) &&
                         MainDescendants(saveContent).Contains(main.FindName("UndoStepLimitInput")),"保存页缺少自动保存或撤回");
                    var path=(FrameworkElement)main.FindName("StoragePathTextBox");
                    var auto=(FrameworkElement)main.FindName("AutoSaveToggle");
                    True(path.TranslatePoint(new Point(),main).Y<auto.TranslatePoint(new Point(),main).Y,"画板路径没有放最上");
                    SaveSettingsSnapshot(main,$"save-load-compact-{mode}.png");
                }
                finally{main.Close();}
                var board=new BoardSettingsWindow("#808080",1,materialAreaEnabled:false){Opacity=0,ShowActivated=false};
                try
                {
                    board.Show();board.UpdateLayout();
                    var toggle=(ToggleButton)board.FindName("MaterialAreaToggle");
                    Equal(false,toggle.IsChecked==true);
                    True(((Grid)board.FindName("MaterialAreaRow")).ToolTip.ToString()!.Contains("仅当前画板"),"设置范围未说明");
                    toggle.IsChecked=true;
                    SaveSettingsSnapshot(board,$"board-material-setting-{mode}.png");
                    board.Hide();
                    board.Dispatcher.BeginInvoke(new Action(()=>typeof(BoardSettingsWindow).GetMethod("OnApplyClick",PrivateInstance)!.Invoke(board,new object[]{board,new RoutedEventArgs()})));
                    True(board.ShowDialog()==true,"画板设置未应用");
                    True(board.MaterialAreaEnabled,"画板设置未返回素材开关值");
                }
                finally{board.Close();}
            }
        }
        finally{ThemeService.Apply(Application.Current,old);}
    }
}

