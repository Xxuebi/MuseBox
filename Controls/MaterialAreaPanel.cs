using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using ScreenshotCollector.Models;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Point = System.Windows.Point;

namespace ScreenshotCollector.Controls;

public sealed record MaterialDragPayload(object Owner, string DrawerId, string[] Ids);
public sealed class MaterialAreaPanel : Border
{
    public const string DragFormat = "MuseBox.Private.MaterialMove.v1";
    private readonly object _owner;
    private readonly string _drawer;
    private readonly ListBox _list = new();
    private readonly TextBlock _empty = new() { Text = "新收集的图片会显示在这里\n拖进画板即可开始整理", TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Dictionary<string, MaterialTile> _tiles = new();
    private readonly HashSet<string> _selection = new();
    private string[] _order = Array.Empty<string>();
    private string[] _renderedOrder = Array.Empty<string>();
    private int _columns;
    private string? _anchor, _pressedId;
    private Point _press;
    private bool _deferSingle;
    public event Action<IReadOnlyList<ImageSource?>, int>? DragStarted;
    public event Action? DragEnded;
    public event Action<IReadOnlySet<string>, bool>? ActionRequested;
    public event Action<double, double>? ResizeRequested;
    public IReadOnlySet<string> SelectedIds => _selection;
    public ListBox Rows => _list;
    public MaterialAreaPanel(object owner, string drawer)
    {
        _owner = owner; _drawer = drawer;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MuseBox;component/Controls/MaterialAreaStyles.xaml", UriKind.Relative) });
        CornerRadius = new CornerRadius(13); BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "CardBrush"); SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition());
        _list.BorderThickness = new Thickness(0); _list.Background = Brushes.Transparent;
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _list.SetValue(ScrollViewer.CanContentScrollProperty, true);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        _list.ItemContainerStyle = new Style(typeof(ListBoxItem));
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _list.ItemContainerStyle.Setters.Add(new Setter(FocusableProperty, false));
        var rowPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        rowPresenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
        rowPresenter.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("ContentTemplate") { RelativeSource = RelativeSource.TemplatedParent });
        _list.ItemContainerStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = rowPresenter }));
        _list.FocusVisualStyle = null;
        var row = new FrameworkElementFactory(typeof(ItemsControl));
        row.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("."));
        var horizontal = new FrameworkElementFactory(typeof(StackPanel)); horizontal.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        row.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(horizontal));
        row.SetValue(ItemsControl.ItemTemplateProperty, TileTemplate());
        _list.ItemTemplate = new DataTemplate { VisualTree = row };
        root.Children.Add(_list);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        root.Children.Add(_empty);
        Child = root;
        var context = new ContextMenu(); context.SetResourceReference(StyleProperty, "RoundedContextMenu");
        var move = new MenuItem { Header = "移入画板" }; var delete = new MenuItem { Header = "删除素材" };
        move.Click += (_, _) => ActionRequested?.Invoke(_selection.ToHashSet(), false);
        delete.Click += (_, _) => ActionRequested?.Invoke(_selection.ToHashSet(), true);
        var moveAll = new MenuItem { Header = "全部移入画板" };
        moveAll.Click += (_, _) => ActionRequested?.Invoke(_order.ToHashSet(), false);
        context.Items.Add(move); context.Items.Add(delete); context.Items.Add(new Separator()); context.Items.Add(moveAll);
        ContextMenu = context;
        ContextMenuOpening += (_, _) => { move.IsEnabled = delete.IsEnabled = _selection.Count > 0; moveAll.IsEnabled = _order.Length > 0; };
        PreviewMouseDown += (_, e) =>
        {
            var source=e.OriginalSource as DependencyObject;
            while(source is not null && !ReferenceEquals(source,this))
            {
                if(source is Thumb or ScrollBar or RepeatButton)return;
                if(source is FrameworkElement {DataContext:MaterialTile tile})
                {
                    if(e.ChangedButton==MouseButton.Right) SelectForContextMenu(tile.Id);
                    return;
                }
                source=source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            ClearSelection();_list.Focus();e.Handled=true;
        };
        MouseDown += (_, e) => { _list.Focus(); e.Handled = true; };
        PreviewMouseWheel += (_, e) =>
        {
            var scroll = FindScroll(_list);
            if (scroll is not null) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - Math.Sign(e.Delta) * 3);
            e.Handled = true;
        };
        DragOver += (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => e.Handled = true;
        AddResize(root, HorizontalAlignment.Right, VerticalAlignment.Stretch, Cursors.SizeWE, true, false);
        AddResize(root, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Cursors.SizeNS, false, true);
        AddResize(root, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE, true, true);
    }
    private void AddResize(Grid root, HorizontalAlignment h, VerticalAlignment v, Cursor cursor, bool x, bool y)
    {
        var thumb = new Thumb { HorizontalAlignment=h, VerticalAlignment=v, Cursor=cursor,
            Width=x ? 8 : double.NaN, Height=y ? 8 : double.NaN, Margin=new Thickness(-10), Background=Brushes.Transparent };
        var template = new FrameworkElementFactory(typeof(Border)); template.SetValue(BackgroundProperty, Brushes.Transparent);
        thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree=template };
        Grid.SetRowSpan(thumb,2); thumb.DragDelta += (_, e) => ResizeRequested?.Invoke(x ? e.HorizontalChange : 0, y ? e.VerticalChange : 0);
        root.Children.Add(thumb);
    }
    private DataTemplate TileTemplate()
    {
        var style = new Style(typeof(Border));
        var selected = new DataTrigger { Binding=new Binding(nameof(MaterialTile.Selected)), Value=true };
        selected.Setters.Add(new Setter(BackgroundProperty,new DynamicResourceExtension("AccentSubtleBrush"))); style.Triggers.Add(selected);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(BorderThicknessProperty,new Thickness(1));
        border.SetValue(StyleProperty, style); border.SetBinding(WidthProperty,new Binding(nameof(MaterialTile.Width)));
        border.SetValue(HeightProperty,112d); border.SetValue(MarginProperty,new Thickness(2));
        border.SetValue(CornerRadiusProperty,new CornerRadius(9)); border.SetValue(PaddingProperty,new Thickness(5));
        border.SetBinding(ToolTipProperty,new Binding(nameof(MaterialTile.Tooltip)));
        style.Setters.Add(new Setter(BackgroundProperty,Brushes.Transparent));
        border.AddHandler(MouseLeftButtonDownEvent,new MouseButtonEventHandler(OnTileDown));
        border.AddHandler(MouseMoveEvent,new MouseEventHandler(OnTileMove));
        border.AddHandler(MouseLeftButtonUpEvent,new MouseButtonEventHandler((sender,e) =>
        { if (_deferSingle && sender is FrameworkElement { DataContext: MaterialTile tile }) Select(tile.Id, ModifierKeys.None); _deferSingle=false; e.Handled=true; }));
        border.AddHandler(MouseRightButtonDownEvent,new MouseButtonEventHandler((sender,e) =>
        { if (sender is FrameworkElement { DataContext: MaterialTile tile } element) {
            SelectForContextMenu(tile.Id);
          } _list.Focus(); e.Handled=true; }));
        border.AddHandler(LoadedEvent,new RoutedEventHandler((sender,_) => { if (sender is FrameworkElement {DataContext:MaterialTile tile}) tile.Load(); }));
        border.AddHandler(UnloadedEvent,new RoutedEventHandler((sender,_) => { if (sender is FrameworkElement {DataContext:MaterialTile tile}) tile.Unload(); }));
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var imageGrid = new FrameworkElementFactory(typeof(Grid)); imageGrid.SetValue(HeightProperty,98d);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(Image.StretchProperty,Stretch.Uniform);
        image.SetValue(EffectProperty, new DropShadowEffect { BlurRadius=8, ShadowDepth=1, Opacity=.24, Color=Colors.Black });
        image.SetBinding(Image.SourceProperty,new Binding(nameof(MaterialTile.Thumbnail))); imageGrid.AppendChild(image);
        var missing = new FrameworkElementFactory(typeof(TextBlock)); missing.SetBinding(TextBlock.TextProperty,new Binding(nameof(MaterialTile.Status)));
        missing.SetResourceReference(TextBlock.ForegroundProperty,"MutedTextBrush");
        missing.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap); missing.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);
        imageGrid.AppendChild(missing); stack.AppendChild(imageGrid);
        var gif = new FrameworkElementFactory(typeof(TextBlock));
        gif.SetBinding(TextBlock.TextProperty, new Binding(nameof(MaterialTile.GifBadge)));
        gif.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        gif.SetResourceReference(TextBlock.BackgroundProperty, "CardBrush");
        gif.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
        gif.SetValue(VerticalAlignmentProperty, VerticalAlignment.Bottom);
        gif.SetValue(TextBlock.FontSizeProperty, 10d);
        imageGrid.AppendChild(gif);
        border.AppendChild(stack);
        return new DataTemplate { VisualTree=border };
    }
    public void SetItems(IReadOnlyList<BoardMaterialItem> materials)
    {
        _order = materials.Select(m=>m.Id).ToArray();
        _selection.IntersectWith(_order);
        foreach (var stale in _tiles.Keys.Except(_order).ToArray()) { _tiles[stale].Unload(); _tiles.Remove(stale); }
        foreach (var item in materials)
        {
            if (!_tiles.TryGetValue(item.Id,out var tile)) _tiles[item.Id]=tile=new MaterialTile(item);
            else tile.Update(item);
            tile.Selected=_selection.Contains(item.Id);
        }
        _empty.Visibility=materials.Count==0 ? Visibility.Visible : Visibility.Collapsed;
        Reflow();
    }
    public void Reflow()
    {
        var available=Math.Max(140,Width-42);
        var columns=Math.Max(1,(int)(available/120));
        var width=available/columns-4;
        foreach(var tile in _tiles.Values) tile.Width=width;
        if (_columns == columns && _renderedOrder.SequenceEqual(_order)) return;
        _columns=columns; _renderedOrder=_order.ToArray();
        _list.ItemsSource=_order.Select(id=>_tiles[id]).Chunk(columns).ToArray();
    }
    public void Invalidate(IReadOnlySet<string> paths)
    { foreach (var tile in _tiles.Values.Where(t=>paths.Contains(t.Item.AssetPath))) tile.Refresh(); }
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsKeyboardFocusWithin) return false;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Z or Key.Y) return false;
        if (e.Key==Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _selection.UnionWith(_order); UpdateSelection(); }
        else if (e.Key==Key.Delete) ActionRequested?.Invoke(_selection.ToHashSet(),true);
        else if (e.Key==Key.Escape) { _selection.Clear(); UpdateSelection(); }
        else return true;
        e.Handled=true; return true;
    }
    public void Select(string id, ModifierKeys modifiers)
    {
        var index=Array.IndexOf(_order,id); if(index<0)return;
        if(modifiers.HasFlag(ModifierKeys.Shift) && _anchor is not null && Array.IndexOf(_order,_anchor) is var start && start>=0)
        {
            if(!modifiers.HasFlag(ModifierKeys.Control)) _selection.Clear();
            _selection.UnionWith(_order.Skip(Math.Min(start,index)).Take(Math.Abs(start-index)+1));
        }
        else if(modifiers.HasFlag(ModifierKeys.Control)) { if(!_selection.Add(id))_selection.Remove(id); _anchor=id; }
        else { _selection.Clear(); _selection.Add(id); _anchor=id; }
        UpdateSelection();
    }
    public void SelectForContextMenu(string id)
    {
        if (!_selection.Contains(id)) Select(id,ModifierKeys.None);
        _deferSingle=false; _pressedId=null;
    }
    public void ClearSelection()
    {
        _selection.Clear(); _anchor=null; _pressedId=null; _deferSingle=false;
        _list.SelectedIndex=-1; UpdateSelection();
    }
    private void UpdateSelection() { foreach(var tile in _tiles.Values)tile.Selected=_selection.Contains(tile.Id); }
    private static bool IsImageHit(FrameworkElement tile, Point point)
    {
        var image=FindImage(tile);
        if (image?.Source is not { } source) return true; // Missing-image placeholder remains selectable.
        var scale=Math.Min(image.ActualWidth/source.Width,image.ActualHeight/source.Height);
        var origin=image.TranslatePoint(new Point((image.ActualWidth-source.Width*scale)/2,
            (image.ActualHeight-source.Height*scale)/2),tile);
        return new Rect(origin,new Size(source.Width*scale,source.Height*scale)).Contains(point);
    }
    private static Image? FindImage(DependencyObject root)
    {
        if(root is Image image)return image;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            if(FindImage(VisualTreeHelper.GetChild(root,i)) is { } found)return found;
        return null;
    }
    private void OnTileDown(object sender,MouseButtonEventArgs e)
    {
        if(sender is not FrameworkElement {DataContext:MaterialTile tile} element)return;
        if(!IsImageHit(element,e.GetPosition(element))){ClearSelection();e.Handled=true;return;}
        _list.Focus(); _press=e.GetPosition(this); _pressedId=tile.Id;
        _deferSingle=_selection.Contains(tile.Id) && Keyboard.Modifiers==ModifierKeys.None;
        if(!_deferSingle)Select(tile.Id,Keyboard.Modifiers); e.Handled=true;
    }
    private void OnTileMove(object sender,MouseEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed || _pressedId is null)return;
        var delta=e.GetPosition(this)-_press;
        if(Math.Abs(delta.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        if(_selection.Count==0)return;
        _deferSingle=false; _pressedId=null; e.Handled=true;
        var ids=_order.Where(_selection.Contains).ToArray();
        try
        {
            DragStarted?.Invoke(ids.Take(3).Select(id=>_tiles[id].Thumbnail).ToArray(),ids.Length);
            DragDrop.DoDragDrop(this,new DataObject(DragFormat,new MaterialDragPayload(_owner,_drawer,ids)),DragDropEffects.Move);
        }
        finally { DragEnded?.Invoke(); }
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if(root is ScrollViewer found)return found;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {var result=FindScroll(VisualTreeHelper.GetChild(root,i));if(result is not null)return result;}
        return null;
    }
    private sealed class MaterialTile : INotifyPropertyChanged
    {
        public BoardMaterialItem Item {get;private set;}
        public string Id=>Item.Id;
        public string GifBadge=>Services.GifAnimationService.IsGif(Item.AssetPath) ? "GIF" : "";
        public string Tooltip=>Item.LayerName+"\n"+Item.AssetPath;
        private double _width; public double Width {get=>_width;set{_width=value;Notify(nameof(Width));}}
        private bool _selected; public bool Selected{get=>_selected;set{_selected=value;Notify(nameof(Selected));}}
        public ImageSource? Thumbnail{get;private set;}
        public string Status{get;private set;}="";
        private int _generation; private bool _visible;
        public MaterialTile(BoardMaterialItem item)=>Item=item;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string name)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
        public void Update(BoardMaterialItem item){var changed=Item.AssetPath!=item.AssetPath;Item=item;Notify(nameof(GifBadge));Notify(nameof(Tooltip));if(changed)Refresh();}
        public void Unload(){_visible=false;_generation++;Thumbnail=null;Notify(nameof(Thumbnail));}
        public void Refresh(){Thumbnail=null;Notify(nameof(Thumbnail));if(_visible)Load();}
        public async void Load()
        {
            _visible=true;var generation=++_generation;var path=Item.AssetPath;
            var source=await Task.Run<ImageSource?>(()=>
            {
                try { using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                    var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=240;
                    image.StreamSource=stream;image.EndInit();image.Freeze();return image; } catch{return null;}
            });
            if(generation!=_generation || !_visible)return;
            Thumbnail=source;Status=source is null ? "图片缺失或无法读取" : "";
            Notify(nameof(Thumbnail));Notify(nameof(Status));Notify(nameof(GifBadge));
        }
    }
}

