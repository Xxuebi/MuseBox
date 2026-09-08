using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ScreenshotCollector.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
namespace ScreenshotCollector;
public partial class BoardWindow
{
    private readonly List<BoardMaterialItem> _materials = new();
    private MaterialAreaPanel _materialPanel = null!;
    private Button _materialTab = null!;
    private bool _materialsExpanded = true, _materialTransferBusy;
    private double _materialWidth=300, _materialHeight=480;
    private const double MaterialHandleOverlap = 2;
    private readonly TranslateTransform _materialSlide = new();
    private readonly System.Windows.Shapes.Path _materialArrow = new() { Width=5, Height=8, Stretch=Stretch.Fill };
    private int _materialAnimationEpoch;
    private Canvas? _materialDragPreview;
    private void InitializeMaterialArea()
    {
        _materialPanel=new MaterialAreaPanel(this,_drawerId) { HorizontalAlignment=HorizontalAlignment.Left,
            VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(12,62,0,0),Width=300,Height=480 };
        _materialPanel.RenderTransform=_materialSlide;
        _materialArrow.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"MutedTextBrush");
        _materialTab=new Button {Content=_materialArrow,HorizontalAlignment=HorizontalAlignment.Left,
            VerticalAlignment=VerticalAlignment.Top,RenderTransform=_materialSlide};
        _materialTab.Style=(Style)_materialPanel.FindResource("MaterialHandleStyle");
        _materialTab.Click+=(_,_)=>ToggleMaterialArea();
        _materialPanel.DragStarted+=BeginMaterialDragPreview;
        _materialPanel.DragEnded+=EndMaterialDragPreview;
        AddHandler(DragDrop.PreviewDragOverEvent,new DragEventHandler(OnMaterialPreviewDragOver),true);
        AddHandler(DragDrop.PreviewDragLeaveEvent,new DragEventHandler(OnMaterialPreviewDragLeave),true);
        _materialPanel.ResizeRequested+=(x,y)=>{_materialWidth=_materialPanel.Width+x;_materialHeight=_materialPanel.Height+y;ClampMaterialPanel();};
        _materialPanel.ActionRequested+=async(ids,delete)=>await ApplyMaterialActionAsync(ids,delete,null);
        Panel.SetZIndex(_materialPanel,124);Panel.SetZIndex(_materialTab,124);
        BoardSurface.Children.Add(_materialPanel);BoardSurface.Children.Add(_materialTab);
        SizeChanged+=(_,_)=>ClampMaterialPanel();
        Closed+=(_,_)=>{EndMaterialDragPreview();};
        UpdateMaterialAreaVisibility();
    }
    private void ClampMaterialPanel()
    {
        var width=Math.Max(180,BoardSurface.ActualWidth);
        var height=Math.Max(180,BoardSurface.ActualHeight-76);
        _materialPanel.Width=Math.Clamp(_materialWidth,Math.Min(220,width-24),Math.Max(Math.Min(220,width-24),Math.Min(560,width*.45)));
        _materialPanel.Height=Math.Clamp(_materialHeight,Math.Min(180,height),height);
        _materialPanel.Reflow();
        // One transform and an overlapping seam keep the handle attached at every DPI and animation frame.
        _materialTab.Margin=new Thickness(12+_materialPanel.Width-MaterialHandleOverlap,62+_materialPanel.Height/2-17,0,0);
        if (!_materialsExpanded && !_materialSlide.HasAnimatedProperties)
            _materialSlide.X=-_materialPanel.Width-12+MaterialHandleOverlap;
    }
    private void OnMaterialsToolbarClick(object sender,RoutedEventArgs e)
    {
        if (!_viewport.MaterialAreaEnabled)
        {
            MaterialsButton.IsChecked=false;
            OnBoardSettingsClick(sender,e);
            return;
        }
        ToggleMaterialArea();
    }
    private void ToggleMaterialArea()
    {
        var from=_materialSlide.X;
        _materialsExpanded=!_materialsExpanded;
        UpdateMaterialAreaVisibility();
        if(!IsLoaded)return;
        var epoch=++_materialAnimationEpoch;
        _materialPanel.Visibility=Visibility.Visible;
        _materialPanel.IsHitTestVisible=_materialsExpanded;
        var duration=TimeSpan.FromMilliseconds(180);
        var panelAnimation=new DoubleAnimation(from,_materialsExpanded ? 0 : -_materialPanel.Width-12+MaterialHandleOverlap,duration)
            { EasingFunction=new CubicEase {EasingMode=EasingMode.EaseInOut} };
        panelAnimation.Completed+=(_,_)=>{
            if(epoch!=_materialAnimationEpoch)return;
            _materialSlide.BeginAnimation(TranslateTransform.XProperty,null);
            UpdateMaterialAreaVisibility();
        };
        _materialSlide.BeginAnimation(TranslateTransform.XProperty,panelAnimation);
    }
    private void UpdateMaterialAreaVisibility()
    {
        var visible=_viewport.MaterialAreaEnabled &&
            _presentationMode is not (BoardPresentationMode.IgnoreMouse or BoardPresentationMode.Transparent);
        _materialAnimationEpoch++;
        _materialSlide.BeginAnimation(TranslateTransform.XProperty,null);
        _materialPanel.Visibility=visible && _materialsExpanded ? Visibility.Visible : Visibility.Collapsed;
        _materialPanel.IsHitTestVisible=visible && _materialsExpanded;
        _materialTab.Visibility=visible ? Visibility.Visible : Visibility.Collapsed;
        _materialArrow.Data=Geometry.Parse(_materialsExpanded ? "M 5,0 L 0,4 L 5,8 Z" : "M 0,0 L 5,4 L 0,8 Z");
        _materialTab.ToolTip=(_materialsExpanded ? "收起素材区" : "展开素材区")+$" · {_materials.Count} 张";
        ClampMaterialPanel();
        _materialSlide.X=_materialsExpanded ? 0 : -_materialPanel.Width-12+MaterialHandleOverlap;
        MaterialsButton.IsChecked=visible && _materialsExpanded;
        MaterialsButton.ToolTip=_viewport.MaterialAreaEnabled ? "素材区" : "素材区已关闭，点击打开画板设置";
    }
    private async Task ReloadMaterialsAsync()
    {
        _viewport.MaterialAreaEnabled=(await _repository.GetViewportAsync(_drawerId)).MaterialAreaEnabled;
        _materials.Clear();_materials.AddRange(await _repository.GetMaterialsAsync(_drawerId));
        _materialPanel.SetItems(_materials);
        foreach(var change in MaterialAreaSession.For(_repository).Take(_drawerId))
            PushUndoSnapshot(Snapshot() with {MaterialDelta=change.Inverse()});
        UpdateMaterialAreaVisibility();
    }
    private void BeginMaterialDragPreview(IReadOnlyList<ImageSource?> sources,int count)
    {
        EndMaterialDragPreview();
        var canvas=new Canvas{Width=146,Height=126,Opacity=.82,IsHitTestVisible=false,
            HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        for(var index=sources.Count-1;index>=0;index--)
        {
            var card=new Border{Width=124,Height=100,CornerRadius=new CornerRadius(5),Padding=new Thickness(4),
                Effect=new DropShadowEffect{BlurRadius=10,ShadowDepth=2,Opacity=.25}};
            card.SetResourceReference(BackgroundProperty,"CardBrush");
            card.SetResourceReference(Border.BorderBrushProperty,"ControlBorderBrush");card.BorderThickness=new Thickness(1);
            if(sources[index] is { } source)card.Child=new System.Windows.Controls.Image{Source=source,Stretch=Stretch.Uniform};
            else card.Child=new TextBlock{Text="图片",HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            Canvas.SetLeft(card,index*7);Canvas.SetTop(card,index*7);canvas.Children.Add(card);
        }
        if(count>1)
        {
            var badge=new Border{CornerRadius=new CornerRadius(9),Padding=new Thickness(6,2,6,2)};
            badge.SetResourceReference(BackgroundProperty,"AccentBrush");
            badge.Child=new TextBlock{Text=count.ToString(),Foreground=Brushes.White};
            Canvas.SetLeft(badge,112);Canvas.SetTop(badge,92);canvas.Children.Add(badge);
        }
        _materialDragPreview=canvas;Panel.SetZIndex(canvas,1000);BoardSurface.Children.Add(canvas);
        PositionMaterialDragPreview(Mouse.GetPosition(BoardSurface));
    }
    private void PositionMaterialDragPreview(Point point)
    {
        if(_materialDragPreview is not { } preview)return;
        preview.Visibility=Visibility.Visible;
        preview.RenderTransform=new TranslateTransform(point.X+14,point.Y+16);
    }
    private void OnMaterialPreviewDragOver(object sender,DragEventArgs e)
    {
        if(e.Data.GetData(MaterialAreaPanel.DragFormat) is MaterialDragPayload payload && ReferenceEquals(payload.Owner,this))
            PositionMaterialDragPreview(e.GetPosition(BoardSurface));
    }
    private void OnMaterialPreviewDragLeave(object sender,DragEventArgs e)
    {
        var point=e.GetPosition(BoardSurface);
        if(_materialDragPreview is { } preview && !new Rect(BoardSurface.RenderSize).Contains(point))
            preview.Visibility=Visibility.Collapsed;
    }
    private void EndMaterialDragPreview()
    {
        if(_materialDragPreview is { } preview)BoardSurface.Children.Remove(preview);
        _materialDragPreview=null;
    }
    private bool IsMaterialDropTarget(DependencyObject? source) =>
        !IsMaterialSource(source) && !IsPointerInsideLayersPanel(source) && !IsToolPaletteSource(source) &&
        !IsDescendantOf(source, Toolbar) && !IsDescendantOf(source, ToolbarToggleButton);
    private static bool IsDescendantOf(DependencyObject? source, DependencyObject target)
    {
        while (source is not null) { if (ReferenceEquals(source,target)) return true; source=GetTreeParent(source); }
        return false;
    }
    private bool IsMaterialSource(DependencyObject? source)
    {
        while(source is not null)
        {
            if(ReferenceEquals(source,_materialPanel)||ReferenceEquals(source,_materialTab))return true;
            source=source is Visual || source is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private async Task ApplyMaterialActionAsync(IReadOnlySet<string> ids,bool delete,Point? center)
    {
        if(ids.Count==0||_materialTransferBusy||_sceneOperation||_historyBusy||_imageEditBusy)return;
        _materialTransferBusy=true;
        BoardSurface.IsHitTestVisible=false;
        try
        {
            await FlushPendingDrawingAsync();await CommitTextEditingAsync();
            var change=delete ? new MaterialChange(_drawerId,_materials.Where(m=>ids.Contains(m.Id)).Select(m=>m.Clone()).ToArray(),
                Array.Empty<BoardMaterialItem>(),Array.Empty<BoardItem>(),Array.Empty<BoardItem>())
                : await MaterialAreaService.PlanMoveAsync(_repository,_drawerId,ids,center ?? ScreenToWorld(new Point(BoardSurface.ActualWidth/2,BoardSurface.ActualHeight/2)));
            if(change.BeforeMaterials.Count==0)return;
            await _repository.ApplyMaterialChangesAsync(new[]{change});
            await ReloadAsync();
            PushUndoSnapshot(Snapshot() with {MaterialDelta=change.Inverse()});
            if(!delete){_selected.Clear();_selected.UnionWith(change.AfterImages.Select(i=>i.Id));UpdateSelectionVisuals();}
            BoardStatus.Text=delete ? $"已删除 {change.BeforeMaterials.Count} 项素材" : $"已移入 {change.AfterImages.Count} 张图片";
        }
        catch(Exception error){BoardStatus.Text="素材操作失败："+error.Message;}
        finally{_materialTransferBusy=false;BoardSurface.IsHitTestVisible=true;}
    }
}

