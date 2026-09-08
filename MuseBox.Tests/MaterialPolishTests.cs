using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ScreenshotCollector.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
namespace ScreenshotCollector.Tests;
internal static partial class Program
{
    private static void MaterialChromeAndBlankSelection()
    {
        var root=CreateTempDirectory();
        try
        {
            var panel=new MaterialAreaPanel(new object(),"A"){Width=300,Height=320};
            var items=Enumerable.Range(0,3).Select(i=>new BoardMaterialItem{Id="tile"+i,LayerName="不显示的文件名"+i,AssetPath=LinkedTestImage(root,$"image{i}.png")}).ToArray();
            panel.SetItems(items);
            var host=new Window{Content=panel,SizeToContent=SizeToContent.WidthAndHeight,WindowStyle=WindowStyle.None,Opacity=0,ShowActivated=false};
            try
            {
                host.Show();host.UpdateLayout();
                PumpSceneTask(async()=>{await Task.Delay(180);return true;});
                True(!MainDescendants(panel).OfType<TextBlock>().Any(t=>t.IsVisible && (t.Text.Contains("不显示的文件名") || t.Text.StartsWith("素材"))),"素材标题或文件名仍然可见");
                var image=MainDescendants(panel).OfType<System.Windows.Controls.Image>().First(i=>i.Source is not null);
                True(image.Effect is DropShadowEffect{Opacity: >0, BlurRadius: >0},"图片没有淡阴影");
                var tile=image;
                DependencyObject parent=tile;
                while(parent is not Border{DataContext: { }} b || b.DataContext.GetType().Name!="MaterialTile")
                    parent=VisualTreeHelper.GetParent(parent);
                var hit=typeof(MaterialAreaPanel).GetMethod("IsImageHit",BindingFlags.NonPublic|BindingFlags.Static)!;
                True(!(bool)hit.Invoke(null,new object[]{parent,new Point(0,0)})!,"图片外留白仍然可选");
                panel.Select(items[0].Id,ModifierKeys.None);
                panel.Rows.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseDownEvent});
                Equal(0,panel.SelectedIds.Count);
                ((UIElement)parent).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Right){RoutedEvent=UIElement.PreviewMouseDownEvent});
                True(panel.SelectedIds.SetEquals(new[]{items[0].Id}),"右键没有选中指向的素材");
                panel.Select(items[1].Id,ModifierKeys.Control);
                panel.SelectForContextMenu(items[0].Id);Equal(2,panel.SelectedIds.Count);
                panel.SelectForContextMenu(items[2].Id);
                True(panel.SelectedIds.SetEquals(new[]{items[2].Id}),"右键新素材未切换选区");
                panel.ClearSelection();
                IReadOnlySet<string>? moved=null;
                panel.ActionRequested+=(ids,delete)=>{if(!delete)moved=ids;};
                var moveAll=panel.ContextMenu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"全部移入画板"));
                moveAll.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                True(moved is not null && moved.SetEquals(items.Select(i=>i.Id)),"全部移入依赖选中范围");
                SaveSettingsSnapshot(host,"material-v132-clean.png");
            }
            finally{host.Close();}
        }
        finally{Directory.Delete(root,true);}
    }
    private static void MaterialSideHandleAndDragPreview() => WithDrawingBoard((board,repo)=>
    {
        board.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), board,
            typeof(BoardWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        board.ShowActivated=false;board.Opacity=0;board.Show();board.UpdateLayout();
        var panel=MaterialField<MaterialAreaPanel>(board,"_materialPanel");
        var tab=MaterialField<System.Windows.Controls.Button>(board,"_materialTab");
        var zoom=MaterialField<double>(board,"_viewZoom");var x=MaterialField<double>(board,"_viewPanX");var y=MaterialField<double>(board,"_viewPanY");
        True(tab.Content is System.Windows.Shapes.Path,"收纳按钮不是小三角");
        var toolbar=(System.Windows.Controls.Primitives.ToggleButton)board.FindName("MaterialsButton");
        toolbar.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        PumpSceneTask(async()=>{await Task.Delay(240);return true;});
        Equal(Visibility.Collapsed,panel.Visibility);Equal(Visibility.Visible,tab.Visibility);
        Equal(0d,tab.TranslatePoint(new Point(),(UIElement)board.FindName("BoardSurface")).X,.01);
        SaveSettingsSnapshot(board,"material-v132-collapsed.png");
        var originalWidth=board.Width;board.Width=540;board.UpdateLayout();
        Equal(0d,tab.TranslatePoint(new Point(),(UIElement)board.FindName("BoardSurface")).X,.01);
        board.Width=originalWidth;board.UpdateLayout();
        CallDrawing(board,"ToggleMaterialArea");
        PumpSceneTask(async()=>{await Task.Delay(240);return true;});
        Equal(Visibility.Visible,panel.Visibility);
        var surface=(UIElement)board.FindName("BoardSurface");
        Equal(panel.TranslatePoint(new Point(panel.ActualWidth,0),surface).X-2,tab.TranslatePoint(new Point(),surface).X,.01);
        True(ReferenceEquals(panel.RenderTransform,tab.RenderTransform),"主体和按钮仍使用独立动画");
        Equal(zoom,MaterialField<double>(board,"_viewZoom"));Equal(x,MaterialField<double>(board,"_viewPanX"));Equal(y,MaterialField<double>(board,"_viewPanY"));
        var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,
            new byte[]{0,0,255,255,0,255,0,255,255,0,0,255,255,255,255,255},8);
        bitmap.Freeze();
        CallDrawing(board,"BeginMaterialDragPreview",new ImageSource?[]{bitmap,bitmap},2);
        CallDrawing(board,"PositionMaterialDragPreview",new Point(450,220));
        var preview=MaterialField<Canvas>(board,"_materialDragPreview");
        True(!preview.IsHitTestVisible,"拖动预览阻挡鼠标");
        Equal(464d,((TranslateTransform)preview.RenderTransform).X);
        Equal(236d,((TranslateTransform)preview.RenderTransform).Y);
        True(VisualChildren<System.Windows.Controls.Image>(preview).Count()==2,"多选没有叠放预览");
        board.UpdateLayout();SaveSettingsSnapshot(board,"material-v132-drag-preview.png");
        CallDrawing(board,"EndMaterialDragPreview");
        True(typeof(BoardWindow).GetField("_materialDragPreview",PrivateInstance)!.GetValue(board) is null,"取消后残留预览");
        Equal(0,repo.GetItemsAsync("A").GetAwaiter().GetResult().Count);
    });
}

