using DrawingBitmap = System.Drawing.Bitmap;
using DrawingPointF = System.Drawing.PointF;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using ScreenshotCollector.Controls;

namespace ScreenshotCollector;

public partial class BoardWindow : Window
{
    private readonly string _drawerId;
    private readonly IBoardRepository _repository;
    private readonly BoardImportService _importService;
    private readonly IClipboardImageService _clipboard = new ClipboardImageService();
    private readonly ISettingsService _settingsService = new JsonSettingsService();
    private readonly List<BoardItem> _items = new();
    private readonly List<BoardTextItem> _textItems = new();
    private readonly List<BoardDrawingItem> _drawingItems = new();
    private readonly List<BoardGroup> _groups = new();
    private readonly Dictionary<string, ItemVisual> _visuals = new();
    private readonly HashSet<string> _selected = new();
    private readonly Stack<BoardSnapshot> _undo = new();
    private readonly Stack<BoardSnapshot> _redo = new();
    private int _undoStepLimit = 100;
    private bool _historyBusy;
    private bool _boardShortcutsEnabled = true;
    private List<BoardElement>? _rotationSnapshots;
    private readonly Dictionary<BoardResizeDirection, Thumb> _resizeHandles = new();
    private readonly Dictionary<BoardRotationCorner, Thumb> _rotationHandles = new();
    private readonly Dictionary<string, BoardKeyGesture> _shortcutGestures =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _shortcutValues = BoardShortcutCatalog.CreateDefaults();
    private readonly DispatcherTimer _viewportTimer;
    private readonly DispatcherTimer _renderQualityTimer;
    private readonly DropShadowEffect _windowFrameShadow = new()
        { BlurRadius = 14, ShadowDepth = 1, Opacity = .38 };
    private double _viewZoom = 1;
    private double _viewPanX;
    private double _viewPanY;
    private bool _viewRenderPending;
    private bool _viewSavePending;
    private bool _boardClosed;
    private bool _compatibleRendering = true;
    private BoardViewport _viewport = new();
    private System.Windows.Point _mouseStart;
    private System.Windows.Point _lastMouse;
    private bool _panning;
    private bool _boxSelecting;
    private bool _draggingItems;
    private bool _spaceDown;
    private BoardSnapshot? _gestureSnapshot;
    private BoardElement? _resizeItem;
    private BoardElement? _resizeSnapshot;
    private List<BoardElement>? _multiResizeSnapshots;
    private Rect _multiResizeBounds;
    private System.Windows.Point _resizeStartMouse;
    private BoardElement? _rotateItem;
    private BoardElement? _rotateSnapshot;
    private System.Windows.Point _rotateCenterScreen;
    private double _rotateStartAngle;
    private bool _rightWindowDragCandidate;
    private bool _rightWindowDragMoved;
    private bool _suppressNextContextMenu;
    private bool _toolbarVisible = true;
    private System.Windows.Point _rightDragStartScreen;
    private double _rightDragStartLeft;
    private double _rightDragStartTop;
    private BoardToolMode _toolMode = BoardToolMode.Select;
    private RichTextBox? _activeTextEditor;
    private BoardTextItem? _activeTextItem;
    private BoardSnapshot? _textEditSnapshot;
    private readonly List<BoardStrokePoint> _drawingPoints = new();
    private BoardDrawingItem? _previewDrawing;
    private Point _drawingStartWorld;
    private string _drawingStrokeColor = "#FF55A6C9";
    private string _drawingFillColor = "#00000000";
    private bool _drawingDashed;
    private bool _drawingArrow;

    private IEnumerable<BoardElement> AllElements =>
        _items.Cast<BoardElement>().Concat(_textItems).Concat(_drawingItems);

    public BoardWindow(string drawerId, IBoardRepository repository, BoardImportService importService)
    {
        _drawerId = drawerId;
        _repository = repository;
        _importService = importService;
        InitializeComponent();
        Opacity = 0;
        InitializeTextToolbar();
        UpdateDrawingToolbarState();
        InitializeResizeHandles();
        InitializeToolPopups();
        InitializeGifPlayback();
        BoardSurface.AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler(OnBoardQueryCursor), true);
        BoardSurface.SizeChanged += (_, _) => { PositionTextPalette(); UpdateImageToolbar(); UpdateTextLinks(); UpdateLayersPanelWidth(); };
        TextPalette.SizeChanged += (_, _) => PositionTextPalette();
        Deactivated += (_, _) => HideEraserCursor();
        UpdateUndoButtons();
        Title = $"画板 {drawerId}";
        BoardTitle.Text = $"画板 {drawerId}";
        _viewportTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _viewportTimer.Tick += async (_, _) =>
        {
            _viewportTimer.Stop();
            await SaveViewportAsync();
        };
        _renderQualityTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _renderQualityTimer.Tick += (_, _) =>
        {
            _renderQualityTimer.Stop();
            RenderOptions.SetBitmapScalingMode(WorldCanvas, BitmapScalingMode.Fant);
        };
        RenderOptions.SetBitmapScalingMode(WorldCanvas, BitmapScalingMode.Fant);
        Closed += (_, _) =>
        {
            _boardClosed = true;
            _viewportTimer.Stop();
            _renderQualityTimer.Stop();
            ResetEraserPreview();
            CloseToolPopups();
        };
        Loaded += OnLoaded;
        Closing += async (sender, args) =>
        {
            CloseToolPopups();
            if (_imageEditBusy) { args.Cancel = true; BoardStatus.Text = "图片正在保存，请稍候"; return; }
            if (_closeAfterDrawingSave) return;
            args.Cancel = true;
            try
            {
                await FlushPendingDrawingAsync();
                await CommitTextEditingAsync();
                await _gifStateSave;
                await PersistGifStatesAsync();
                await SaveViewportAsync();
                _closeAfterDrawingSave = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            catch (Exception error) { BoardStatus.Text = $"尚未保存，窗口保持打开：{error.Message}"; }
        };
    }

    public async Task ReloadAsync()
    {
        if (_boardClosed) return;
        await FlushPendingDrawingAsync();
        await RefreshBoardTitleAsync();
        var selection = _selected.ToHashSet();
        _items.Clear();
        _items.AddRange(await _repository.GetItemsAsync(_drawerId));
        _textItems.Clear();
        _textItems.AddRange(await _repository.GetTextItemsAsync(_drawerId));
        _drawingItems.Clear();
        _drawingItems.AddRange(await _repository.GetDrawingItemsAsync(_drawerId));
        _groups.Clear();
        _groups.AddRange(await _repository.GetGroupsAsync(_drawerId));
        var layerState = AllElements.Select(element => (element.Id, element.ZIndex, element.LayerName)).ToArray();
        var groupNames = _groups.Select(group => (group.Id, group.LayerName)).ToArray();
        BoardLayerNameService.EnsureNames(AllElements, _groups);
        BoardLayerTreeService.NormalizeZIndices(_groups, AllElements);
        if (!layerState.SequenceEqual(AllElements.Select(element => (element.Id, element.ZIndex, element.LayerName))) ||
            !groupNames.SequenceEqual(_groups.Select(group => (group.Id, group.LayerName))))
            await _repository.ApplyLayerTreeAsync(_drawerId, _groups, AllElements.ToArray());
        if (_focusedImageId is not null && AllElements.All(x => x.Id != _focusedImageId) &&
            _groups.All(group => group.Id != _focusedImageId)) _focusedImageId = null;
        _savedGifStates.Clear();
        foreach (var state in await _repository.GetGifStatesAsync(_drawerId)) _savedGifStates[state.ItemId] = state;
        if (_boardClosed) return;
        RenderItems();
        foreach (var id in selection.Where(id => AllElements.Any(x => x.Id == id))) _selected.Add(id);
        UpdateSelectionVisuals();
    }

    private async Task RefreshBoardTitleAsync()
    {
        var drawer = (await _repository.GetDrawersAsync()).FirstOrDefault(x => x.Id == _drawerId);
        var displayName = string.IsNullOrWhiteSpace(drawer?.DisplayName) ? "未命名" : drawer.DisplayName;
        var title = $"画板 {displayName} {_drawerId}";
        Title = title;
        BoardTitle.Text = title;
        if (drawer is not null) UpdateSceneTitle(drawer);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
        _viewport = await _repository.GetViewportAsync(_drawerId);
        await LoadBoardShortcutsAsync();
        if (_viewport.WindowLeft is double left && _viewport.WindowTop is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        Width = Math.Max(MinWidth, _viewport.WindowWidth);
        Height = Math.Max(MinHeight, _viewport.WindowHeight);
        if (await _repository.GetSceneBindingAsync(_drawerId) is not null) ConstrainSceneWindowToScreen();
        Topmost = _viewport.Topmost;
        ApplyBackground(_viewport.BackgroundColor, _viewport.WindowOpacity);
        ApplyWindowFrame(_viewport.ShowWindowFrame);
        _viewZoom = Math.Clamp(_viewport.Zoom, .05, 8);
        _viewPanX = _viewport.PanX;
        _viewPanY = _viewport.PanY;
        ApplyViewportTransform();
        UpdatePinText();
        await ReloadAsync();
        BoardSurface.Focus();
        _boardInitialization.TrySetResult();
        }
        catch (Exception error) { _boardInitialization.TrySetException(error); BoardStatus.Text = $"画板载入失败：{error.Message}"; }
        finally
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!_boardClosed) Opacity = 1;
        }
    }

    private void RenderItems()
    {
        PruneGifBindings();
        var images = _visuals.ToDictionary(pair => pair.Key, pair =>
            (pair.Value.Border.Child as Grid)?.Children.OfType<Image>().FirstOrDefault());
        WorldCanvas.Children.Clear();
        _visuals.Clear();
        _selected.RemoveWhere(id => AllElements.All(x => x.Id != id));
        RenderGroupBackgrounds();
        foreach (var item in AllElements.OrderBy(x => x.ZIndex))
        {
            switch (item)
            {
                case BoardItem image:
                    var cached = images.GetValueOrDefault(image.Id);
                    AddItemVisual(image, Equals(cached?.Tag, image.AssetPath) ? cached?.Source : null);
                    break;
                case BoardTextItem text: AddTextVisual(text); break;
                case BoardDrawingItem drawing: AddDrawingVisual(drawing); break;
            }
        }
        RefreshLayersPanel();
    }

    private void AddItemVisual(BoardItem item, ImageSource? cachedSource = null)
    {
        var border = new Border
        {
            Width = item.Width,
            Height = item.Height,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.Transparent,
            Tag = item.Id,
            Cursor = Cursors.SizeAll,
            ClipToBounds = true,
            RenderTransformOrigin = new System.Windows.Point(.5, .5),
            RenderTransform = new RotateTransform(item.Rotation),
            Opacity = _viewport.OpacityAffectsImages ? Math.Clamp(_viewport.WindowOpacity, .1, 1) : 1
        };
        var grid = new Grid();
        border.Child = grid;
        if (File.Exists(item.AssetPath))
        {
            var image = new Image
            {
                Tag = item.AssetPath,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };
            grid.Children.Add(image);
            if (GifAnimationService.IsGif(item.AssetPath)) _ = AttachGifAsync(item, image);
            else if (cachedSource is not null) image.Source = cachedSource;
            else _ = LoadImageAsync(item.AssetPath, image);
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = "图片文件缺失", Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        WireElementEvents(border, item);
        Canvas.SetLeft(border, item.X);
        Canvas.SetTop(border, item.Y);
        Panel.SetZIndex(border, item.ZIndex);
        WorldCanvas.Children.Add(border);
        _visuals[item.Id] = new ItemVisual(border, null, null);
    }

    private static async Task LoadImageAsync(string path, System.Windows.Controls.Image target)
    {
        var source = await Task.Run(() =>
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1400;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        });
        if (source is not null) target.Source = source;
    }

    private void OnItemMouseDown(BoardElement item, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        if (item is BoardTextItem && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            TryOpenHyperlink(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }
        if (_activeTextItem?.Id == item.Id) return;
        if (_activeTextEditor is not null)
        {
            if (e.ClickCount == 2 && item is BoardTextItem nextText)
            {
                _selected.Clear();
                _selected.Add(item.Id);
                UpdateSelectionVisuals();
                BeginTextEditing(nextText);
            }
            else
            {
                _ = CommitTextEditingAsync();
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && !_spaceDown &&
            !IsDrawingTool(_toolMode) && NextNestedGroupFor(item) is { } nestedGroup &&
            _individualGroupSelectionId != item.Id)
        {
            SelectNestedGroupDirectly(nestedGroup.Id);
            BoardStatus.Text = $"已选择内层组合“{nestedGroup.LayerName}”，再次双击可临时选择元素";
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && !_spaceDown &&
            item.GroupId.Length > 0 &&
            _individualGroupSelectionId != item.Id && !IsDrawingTool(_toolMode))
        {
            _individualGroupSelectionId = item.Id;
            _explicitSelectedGroupId = null;
            _selected.Clear();
            _selected.Add(item.Id);
            UpdateSelectionVisuals();
            BoardStatus.Text = item is BoardItem
                ? "已临时选择组合内单个元素，再次双击可聚焦"
                : "已临时选择组合内单个元素，再次双击可编辑";
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && !_spaceDown &&
            item is BoardDrawingItem drawing && !IsDrawingTool(_toolMode))
        {
            _draggingItems = false;
            _gestureSnapshot = null;
            Mouse.Capture(null);
            BeginDrawingSession(drawing);
            e.Handled = true;
            return;
        }
        if (_toolMode == BoardToolMode.Text)
            SetToolMode(BoardToolMode.Select);
        if (_toolMode != BoardToolMode.Select) return;
        if (e.ChangedButton != MouseButton.Left || _spaceDown) return;
        if (e.ClickCount == 2 && item is BoardItem { GroupId.Length: > 0 } groupedImage)
        {
            _individualGroupSelectionId = groupedImage.Id;
            ToggleImageFocus(groupedImage);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && item is BoardItem image)
        {
            ToggleImageFocus(image);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && item is BoardTextItem text)
        {
            _selected.Clear();
            _selected.Add(item.Id);
            UpdateSelectionVisuals();
            BeginTextEditing(text);
            e.Handled = true;
            return;
        }
        var directGroup = DirectlySelectedGroupContaining(item);
        if (directGroup is null && _individualGroupSelectionId != item.Id)
            _layerDirectSelectionActive = false;
        if (_individualGroupSelectionId != item.Id) _individualGroupSelectionId = null;
        var unit = ImageSelectionUnit(item).ToArray();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var ids = unit.Select(x => x.Id).ToArray();
            if (_selected.Contains(item.Id)) _selected.ExceptWith(ids);
            else _selected.UnionWith(ids);
        }
        else if (!_selected.Contains(item.Id))
        {
            _selected.Clear();
            _selected.UnionWith(unit.Select(element => element.Id));
        }
        _explicitSelectedGroupId = directGroup?.Id ?? (unit.Length > 1
            ? BoardLayerTreeService.OutermostLockedAncestor(item, _groups) : null);
        UpdateSelectionVisuals();
        PrepareGroupMembershipDrag();
        _gestureSnapshot = Snapshot();
        _mouseStart = _lastMouse = e.GetPosition(BoardSurface);
        _draggingItems = true;
        _itemsDragMoved = false;
        BeginContinuousInteraction();
        Mouse.Capture(BoardSurface);
        e.Handled = true;
    }

    private void WireElementEvents(Border border, BoardElement item)
    {
        border.PreviewMouseLeftButtonDown += (_, args) => OnItemMouseDown(item, args);
        border.PreviewMouseRightButtonDown += (_, _) =>
        {
            if (_selected.Contains(item.Id)) return;
            _selected.Clear();
            _selected.UnionWith(ImageSelectionUnit(item).Select(x => x.Id));
            UpdateSelectionVisuals();
        };
    }

    private void AddTextVisual(BoardTextItem item)
    {
        var editor = new RichTextBox
        {
            Document = RichTextDocumentService.Load(item.DocumentData),
            IsReadOnly = true,
            IsUndoEnabled = true,
            UndoLimit = _undoStepLimit,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Focusable = false,
            IsHitTestVisible = true,
            IsDocumentEnabled = true
        };
        var border = CreateElementBorder(item);
        border.Background = ParseBrush(item.BackgroundColor, Brushes.Transparent);
        border.Child = editor;
        WireElementEvents(border, item);
        AddElementBorder(border, item);
        _visuals[item.Id] = new ItemVisual(border, editor, null);
    }

    private void AddDrawingVisual(BoardDrawingItem item)
    {
        var drawing = new BoardDrawingVisual
        {
            Item = item, Width = item.Width, Height = item.Height, Margin = new Thickness(-1.2)
        };
        var border = CreateElementBorder(item);
        border.Child = drawing;
        WireElementEvents(border, item);
        AddElementBorder(border, item);
        _visuals[item.Id] = new ItemVisual(border, null, drawing);
    }

    private Border CreateElementBorder(BoardElement item) => new()
    {
        Width = item.Width,
        Height = item.Height,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(1.2),
        CornerRadius = new CornerRadius(3),
        BorderBrush = Brushes.Transparent,
        Tag = item.Id,
        Cursor = Cursors.SizeAll,
        ClipToBounds = false,
        RenderTransformOrigin = new Point(.5, .5),
        RenderTransform = new RotateTransform(item.Rotation),
        Opacity = _viewport.OpacityAffectsImages ? Math.Clamp(_viewport.WindowOpacity, .1, 1) : 1
    };

    private void AddElementBorder(Border border, BoardElement item)
    {
        Canvas.SetLeft(border, item.X);
        Canvas.SetTop(border, item.Y);
        Panel.SetZIndex(border, item.ZIndex);
        WorldCanvas.Children.Add(border);
    }

    private static Brush ParseBrush(string value, Brush fallback)
    {
        try
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)
                System.Windows.Media.ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }
        catch { return fallback; }
    }

    private void InitializeResizeHandles()
    {
        foreach (var direction in Enum.GetValues<BoardResizeDirection>())
        {
            var horizontalEdge = direction is BoardResizeDirection.North or BoardResizeDirection.South;
            var verticalEdge = direction is BoardResizeDirection.East or BoardResizeDirection.West;
            var handle = new Thumb
            {
                Style = (Style)FindResource("ResizeHandle"),
                Cursor = horizontalEdge ? Cursors.SizeNS
                    : verticalEdge ? Cursors.SizeWE
                    : direction is BoardResizeDirection.NorthWest or BoardResizeDirection.SouthEast
                        ? Cursors.SizeNWSE : Cursors.SizeNESW,
                Visibility = Visibility.Collapsed,
                Tag = direction
            };
            Panel.SetZIndex(handle, 50);
            handle.DragStarted += OnResizeStarted;
            handle.DragDelta += OnResizeDelta;
            handle.DragCompleted += OnResizeCompleted;
            OverlayCanvas.Children.Add(handle);
            _resizeHandles[direction] = handle;
        }

        foreach (var corner in Enum.GetValues<BoardRotationCorner>())
        {
            var handle = new Thumb
            {
                Style = (Style)FindResource("RotationHandle"),
                Cursor = BoardRotationCursor.Value,
                Visibility = Visibility.Collapsed,
                Tag = corner
            };
            // Near-corner rotation targets sit behind resize grips; resize wins
            // in the small overlap without moving rotation far from the image.
            Panel.SetZIndex(handle, 49);
            handle.DragStarted += OnRotateStarted;
            handle.MouseEnter += (_, _) => handle.Cursor = BoardRotationCursor.ForDpi(VisualTreeHelper.GetDpi(handle).DpiScaleX);
            handle.DragDelta += OnRotateDelta;
            handle.DragCompleted += OnRotateCompleted;
            OverlayCanvas.Children.Add(handle);
            _rotationHandles[corner] = handle;
        }
    }

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (_selected.Count == 0) return;
        BeginContinuousInteraction();
        if (_selected.Count > 1)
        {
            PushUndoSnapshot();
            _multiResizeSnapshots = AllElements.Where(x => _selected.Contains(x.Id))
                .Select(x => x.CloneElement()).ToList();
            _multiResizeBounds = GetBounds(_multiResizeSnapshots);
            _resizeStartMouse = Mouse.GetPosition(BoardSurface);
            BoardStatus.Text = "缩放多选内容 · Shift 自由拉伸 · Alt 从中心缩放";
            return;
        }
        _resizeItem = AllElements.FirstOrDefault(x => _selected.Contains(x.Id));
        if (_resizeItem is null) return;
        PushUndoSnapshot();
        _resizeSnapshot = _resizeItem.CloneElement();
        _resizeStartMouse = Mouse.GetPosition(BoardSurface);
        BoardStatus.Text = "默认等比例缩放 · Shift 自由拉伸 · Alt 从中心缩放";
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: BoardResizeDirection direction }) return;
        ResizeSelectionFromPointer(direction, Mouse.GetPosition(BoardSurface), Keyboard.Modifiers);
    }

    private void ResizeSelectionFromPointer(BoardResizeDirection direction, Point current, ModifierKeys modifiers)
    {
        var worldDx = (current.X - _resizeStartMouse.X) / _viewZoom;
        var worldDy = (current.Y - _resizeStartMouse.Y) / _viewZoom;
        if (_multiResizeSnapshots is not null)
        {
            var groupSnapshot = new BoardItem
            {
                X = _multiResizeBounds.X,
                Y = _multiResizeBounds.Y,
                Width = _multiResizeBounds.Width,
                Height = _multiResizeBounds.Height
            };
            var groupTransformed = BoardMath.ResizeFromSnapshot(
                groupSnapshot,
                direction,
                worldDx,
                worldDy,
                preserveAspect: !modifiers.HasFlag(ModifierKeys.Shift),
                fromCenter: modifiers.HasFlag(ModifierKeys.Alt));
            var targetBounds = new Rect(
                groupTransformed.X, groupTransformed.Y,
                groupTransformed.Width, groupTransformed.Height);
            var scaleX = targetBounds.Width / Math.Max(1, _multiResizeBounds.Width);
            var scaleY = targetBounds.Height / Math.Max(1, _multiResizeBounds.Height);
            foreach (var snapshot in _multiResizeSnapshots)
            {
                var live = AllElements.First(x => x.Id == snapshot.Id);
                live.X = targetBounds.X + (snapshot.X - _multiResizeBounds.X) * scaleX;
                live.Y = targetBounds.Y + (snapshot.Y - _multiResizeBounds.Y) * scaleY;
                live.Width = Math.Max(1, snapshot.Width * scaleX);
                live.Height = Math.Max(1, snapshot.Height * scaleY);
                UpdateItemVisual(live);
            }
            UpdateResizeHandles();
            return;
        }
        if (_resizeItem is null || _resizeSnapshot is null) return;
        var transformed = BoardMath.ResizeRotatedFromSnapshot(
            ToBoundsItem(_resizeSnapshot),
            direction,
            worldDx,
            worldDy,
            preserveAspect: _resizeSnapshot is not BoardTextItem &&
                            !modifiers.HasFlag(ModifierKeys.Shift),
            fromCenter: modifiers.HasFlag(ModifierKeys.Alt));
        _resizeItem.X = transformed.X;
        _resizeItem.Y = transformed.Y;
        _resizeItem.Width = transformed.Width;
        _resizeItem.Height = transformed.Height;
        UpdateItemVisual(_resizeItem);
        UpdateResizeHandles();
    }

    private async void OnResizeCompleted(object sender, DragCompletedEventArgs e)
        => await CompleteResizeAsync();

    private async Task CompleteResizeAsync()
    {
        if (_multiResizeSnapshots is not null)
            await PersistElementsAsync(AllElements.Where(x => _selected.Contains(x.Id)));
        else if (_resizeItem is not null)
            await PersistElementsAsync(new[] { _resizeItem });
        _resizeItem = null;
        _resizeSnapshot = null;
        _multiResizeSnapshots = null;
        EndContinuousInteraction();
        UpdateSelectionVisuals();
    }

    private void OnRotateStarted(object sender, DragStartedEventArgs e)
    {
        if (_selected.Count == 0) return;
        BeginContinuousInteraction();
        var selected = AllElements.Where(x => _selected.Contains(x.Id)).ToArray();
        _rotationSnapshots = selected.Length > 1 ? selected.Select(x => x.CloneElement()).ToList() : null;
        _rotateItem = selected.Length > 1 ? CreateSelectionBoundsItem(selected) : selected.FirstOrDefault();
        if (_rotateItem is null) return;
        PushUndoSnapshot();
        _rotateSnapshot = _rotateItem.CloneElement();
        _rotateCenterScreen = ItemCenterScreen(_rotateSnapshot);
        _rotateStartAngle = PointerAngle(Mouse.GetPosition(BoardSurface), _rotateCenterScreen);
        BoardStatus.Text = "拖动角点旋转 · 松开后自动保存";
    }

    private void OnRotateDelta(object sender, DragDeltaEventArgs e)
    {
        if (_rotateItem is null || _rotateSnapshot is null) return;
        var currentAngle = PointerAngle(Mouse.GetPosition(BoardSurface), _rotateCenterScreen);
        var delta = BoardMath.NormalizeAngleDelta(currentAngle - _rotateStartAngle);
        ApplyRotationDelta(delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        UpdateResizeHandles();
        BoardStatus.Text = $"旋转 {_rotateItem.Rotation:0}°";
    }

    private async void OnRotateCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_rotationSnapshots is not null) await PersistElementsAsync(AllElements.Where(x => _selected.Contains(x.Id)));
        else if (_rotateItem is not null) await PersistElementsAsync(new[] { _rotateItem });
        _rotationSnapshots = null;
        _rotateItem = null;
        _rotateSnapshot = null;
        EndContinuousInteraction();
        UpdateSelectionVisuals();
    }

    private static double PointerAngle(System.Windows.Point point, System.Windows.Point center) =>
        Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;

    private void UpdateItemVisual(BoardElement item)
    {
        if (!_visuals.TryGetValue(item.Id, out var visual)) return;
        visual.Border.Width = item.Width;
        visual.Border.Height = item.Height;
        Canvas.SetLeft(visual.Border, item.X);
        Canvas.SetTop(visual.Border, item.Y);
        if (visual.Border.RenderTransform is RotateTransform rotation)
            rotation.Angle = item.Rotation;
        if (visual.Drawing is not null)
        {
            visual.Drawing.Width = item.Width;
            visual.Drawing.Height = item.Height;
            visual.Drawing.Item = (BoardDrawingItem)item;
        }
        UpdateAncestorGroupVisuals(item.GroupId);
    }

    private void UpdateResizeHandles()
    {
        UpdateImageToolbar();
        UpdateTextLinks();
        UpdateGroupToolbar();
        if (IsDrawingTool(_toolMode))
        {
            GroupSelectionRectangle.Visibility = Visibility.Collapsed;
            foreach (var handle in _resizeHandles.Values) handle.Visibility = Visibility.Collapsed;
            foreach (var handle in _rotationHandles.Values) handle.Visibility = Visibility.Collapsed;
            return;
        }
        if (_selected.Count == 0)
        {
            if (GroupSelectionRectangle.Visibility != Visibility.Collapsed)
                GroupSelectionRectangle.Visibility = Visibility.Collapsed;
            foreach (var handle in _resizeHandles.Values)
                if (handle.Visibility != Visibility.Collapsed) handle.Visibility = Visibility.Collapsed;
            foreach (var handle in _rotationHandles.Values)
                if (handle.Visibility != Visibility.Collapsed) handle.Visibility = Visibility.Collapsed;
            PositionTextPalette();
            return;
        }
        var selectedItems = AllElements.Where(x => _selected.Contains(x.Id)).ToList();
        var groupSelection = selectedItems.Count > 1;
        var canRotate = !groupSelection || SelectedGroup() is not null;
        BoardElement? item = selectedItems.Count switch
        {
            0 => null,
            1 => selectedItems[0],
            _ => CreateSelectionBoundsItem(selectedItems)
        };
        foreach (var handle in _resizeHandles.Values)
            handle.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var handle in _rotationHandles.Values)
            handle.Visibility = item is not null && canRotate
                ? Visibility.Visible : Visibility.Collapsed;
        GroupSelectionRectangle.Visibility = groupSelection
            ? Visibility.Visible : Visibility.Collapsed;
        if (item is null) return;

        var left = item.X * _viewZoom + _viewPanX;
        var top = item.Y * _viewZoom + _viewPanY;
        var right = (item.X + item.Width) * _viewZoom + _viewPanX;
        var bottom = (item.Y + item.Height) * _viewZoom + _viewPanY;
        var centerX = (left + right) / 2;
        var centerY = (top + bottom) / 2;
        var center = new System.Windows.Point(centerX, centerY);
        if (groupSelection)
        {
            Canvas.SetLeft(GroupSelectionRectangle, left);
            Canvas.SetTop(GroupSelectionRectangle, top);
            GroupSelectionRectangle.Width = Math.Max(1, right - left);
            GroupSelectionRectangle.Height = Math.Max(1, bottom - top);
            GroupSelectionRectangle.RenderTransformOrigin = new System.Windows.Point(.5, .5);
            if (GroupSelectionRectangle.RenderTransform is RotateTransform groupRotation)
                groupRotation.Angle = item.Rotation;
            else
                GroupSelectionRectangle.RenderTransform = new RotateTransform(item.Rotation);
        }
        var points = new Dictionary<BoardResizeDirection, System.Windows.Point>
        {
            [BoardResizeDirection.NorthWest] = new(left, top),
            [BoardResizeDirection.North] = new(centerX, top),
            [BoardResizeDirection.NorthEast] = new(right, top),
            [BoardResizeDirection.East] = new(right, centerY),
            [BoardResizeDirection.SouthEast] = new(right, bottom),
            [BoardResizeDirection.South] = new(centerX, bottom),
            [BoardResizeDirection.SouthWest] = new(left, bottom),
            [BoardResizeDirection.West] = new(left, centerY)
        };
        foreach (var (direction, point) in points)
        {
            var rotated = BoardMath.RotatePoint(point, center, item.Rotation);
            PositionHandle(direction, rotated.X, rotated.Y);
            _resizeHandles[direction].RenderTransformOrigin = new System.Windows.Point(.5, .5);
            if (_resizeHandles[direction].RenderTransform is RotateTransform rotation)
                rotation.Angle = item.Rotation;
            else
                _resizeHandles[direction].RenderTransform = new RotateTransform(item.Rotation);
        }

        PositionTextPalette();
        if (!canRotate) return;
        PositionRotationHandle(BoardRotationCorner.NorthWest, points[BoardResizeDirection.NorthWest], center, item.Rotation);
        PositionRotationHandle(BoardRotationCorner.NorthEast, points[BoardResizeDirection.NorthEast], center, item.Rotation);
        PositionRotationHandle(BoardRotationCorner.SouthEast, points[BoardResizeDirection.SouthEast], center, item.Rotation);
        PositionRotationHandle(BoardRotationCorner.SouthWest, points[BoardResizeDirection.SouthWest], center, item.Rotation);
    }

    private static BoardItem CreateBoundsItem(Rect bounds) => new()
    {
        X = bounds.X,
        Y = bounds.Y,
        Width = bounds.Width,
        Height = bounds.Height
    };

    private void PositionHandle(BoardResizeDirection direction, double x, double y)
    {
        var handle = _resizeHandles[direction];
        Canvas.SetLeft(handle, x - handle.Width / 2);
        Canvas.SetTop(handle, y - handle.Height / 2);
    }

    private void PositionRotationHandle(
        BoardRotationCorner corner,
        System.Windows.Point point,
        System.Windows.Point center,
        double rotation)
    {
        var rotated = BoardMath.RotatePoint(point, center, rotation);
        var dx = rotated.X - center.X;
        var dy = rotated.Y - center.Y;
        var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        const double offset = 18;
        var handle = _rotationHandles[corner];
        Canvas.SetLeft(handle, rotated.X + dx / length * offset - handle.Width / 2);
        Canvas.SetTop(handle, rotated.Y + dy / length * offset - handle.Height / 2);
    }

    private System.Windows.Point ItemCenterScreen(BoardElement item) => new(
        (item.X + item.Width / 2) * _viewZoom + _viewPanX,
        (item.Y + item.Height / 2) * _viewZoom + _viewPanY);

    private static BoardItem ToBoundsItem(BoardElement item) => new()
    {
        Id = item.Id, DrawerId = item.DrawerId,
        X = item.X, Y = item.Y, Width = item.Width, Height = item.Height,
        Rotation = item.Rotation, ZIndex = item.ZIndex, CreatedUtc = item.CreatedUtc
    };

    private static Rect GetBounds(IEnumerable<BoardElement> elements)
    {
        var items = elements.ToArray();
        if (items.Length == 0) return Rect.Empty;
        return items.Select(RotatedImageBounds).Aggregate(Rect.Empty, (bounds, next) =>
        { bounds.Union(next); return bounds; });
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsToolPaletteSource(e.OriginalSource as DependencyObject)) return;
        if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null ||
            FindVisualAncestor<ComboBox>(e.OriginalSource as DependencyObject) is not null ||
            FindVisualAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (_activeTextEditor is not null &&
            FindVisualAncestor<RichTextBox>(e.OriginalSource as DependencyObject) != _activeTextEditor)
        {
            _ = CommitTextEditingAsync();
            e.Handled = true;
            return;
        }
        BoardSurface.Focus();
        _mouseStart = _lastMouse = e.GetPosition(BoardSurface);
        if (e.ChangedButton == MouseButton.Middle || (_spaceDown && e.ChangedButton == MouseButton.Left))
        {
            BeginContinuousInteraction();
            _panning = true;
            HideEraserCursor();
            BoardSurface.Cursor = Cursors.Hand;
            Mouse.Capture(BoardSurface);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _toolMode == BoardToolMode.Text)
        {
            _ = CreateTextAtAsync(ScreenToWorld(_mouseStart));
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && IsDrawingTool(_toolMode))
        {
            StartDrawing(ScreenToWorld(_mouseStart));
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource == BoardSurface)
        {
            _layerDirectSelectionActive = false;
            _explicitSelectedGroupId = null;
            _individualGroupSelectionId = null;
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) _selected.Clear();
            UpdateSelectionVisuals();
            _boxSelecting = true;
            SelectionRectangle.Visibility = Visibility.Visible;
            UpdateSelectionRectangle(_mouseStart, _mouseStart);
            Mouse.Capture(BoardSurface);
        }
    }

    private void OnSurfaceMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var current = e.GetPosition(BoardSurface);
        if (_panning)
        {
            _viewPanX += current.X - _lastMouse.X;
            _viewPanY += current.Y - _lastMouse.Y;
            _lastMouse = current;
            RequestViewportRender(save: false);
        }
        else if (_draggingItems)
        {
            var dx = (current.X - _lastMouse.X) / _viewZoom;
            var dy = (current.Y - _lastMouse.Y) / _viewZoom;
            _itemsDragMoved |= Math.Abs(dx) + Math.Abs(dy) > .001;
            foreach (var item in AllElements.Where(x => _selected.Contains(x.Id)))
            {
                item.X += dx;
                item.Y += dy;
                var visual = _visuals[item.Id].Border;
                Canvas.SetLeft(visual, item.X);
                Canvas.SetTop(visual, item.Y);
            }
            UpdateGroupVisuals();
            EvaluateGroupMembershipDrop();
            _lastMouse = current;
            UpdateResizeHandles();
        }
        else if (_boxSelecting)
        {
            UpdateSelectionRectangle(_mouseStart, current);
        }
        else if ((_previewDrawing is not null || _erasing) && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateDrawing(ScreenToWorld(current));
        }
    }

    private async void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            BoardSurface.Cursor = _toolMode == BoardToolMode.Select || _toolMode == BoardToolMode.Eraser
                ? Cursors.Arrow : _toolMode == BoardToolMode.Text ? Cursors.IBeam : Cursors.Pen;
            RefreshEraserCursor();
            EndContinuousInteraction();
            QueueViewportSave();
        }
        else if (_draggingItems)
        {
            _draggingItems = false;
            EndContinuousInteraction();
            if (_itemsDragMoved && _gestureSnapshot is not null) PushUndoSnapshot(_gestureSnapshot);
            _gestureSnapshot = null;
            var membershipChanged = _itemsDragMoved && ApplyPendingGroupMembership();
            if (_itemsDragMoved)
            {
                if (membershipChanged) await PersistLayerTreeAsync();
                else await PersistElementsAsync(AllElements.Where(x => _selected.Contains(x.Id)));
            }
            ClearGroupDropHint();
            FinishGroupMembershipDrag();
            if (membershipChanged) UpdateSelectionVisuals();
        }
        else if (_boxSelecting)
        {
            _boxSelecting = false;
            _individualGroupSelectionId = null;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            var end = e.GetPosition(BoardSurface);
            var left = Math.Min(_mouseStart.X, end.X);
            var top = Math.Min(_mouseStart.Y, end.Y);
            var right = Math.Max(_mouseStart.X, end.X);
            var bottom = Math.Max(_mouseStart.Y, end.Y);
            var worldLeft = (left - _viewPanX) / _viewZoom;
            var worldTop = (top - _viewPanY) / _viewZoom;
            var worldRight = (right - _viewPanX) / _viewZoom;
            var worldBottom = (bottom - _viewPanY) / _viewZoom;
            foreach (var item in AllElements)
            {
                if (RotatedImageBounds(item).IntersectsWith(new Rect(worldLeft, worldTop,
                    Math.Max(0, worldRight - worldLeft), Math.Max(0, worldBottom - worldTop)))) _selected.Add(item.Id);
            }
            UpdateSelectionVisuals();
        }
        else if ((_previewDrawing is not null || _erasing) && e.ChangedButton == MouseButton.Left)
        {
            await CompleteDrawingAsync();
            // The completed gesture already released capture before saving. A new
            // gesture may have started while awaiting I/O; do not release its capture.
            return;
        }
        Mouse.Capture(null);
    }

    private void UpdateSelectionRectangle(System.Windows.Point a, System.Windows.Point b)
    {
        Canvas.SetLeft(SelectionRectangle, Math.Min(a.X, b.X));
        Canvas.SetTop(SelectionRectangle, Math.Min(a.Y, b.Y));
        SelectionRectangle.Width = Math.Abs(a.X - b.X);
        SelectionRectangle.Height = Math.Abs(a.Y - b.Y);
    }

    private void UpdateSelectionVisuals()
    {
        ExpandSelectedImageGroups();
        var wholeGroups = _groups.Where(group =>
        {
            var members = GroupMembers(group.Id);
            return members.Length > 1 && members.All(x => _selected.Contains(x.Id));
        }).Select(group => group.Id).ToHashSet();
        foreach (var pair in _visuals)
        {
            var selectedElement = AllElements.FirstOrDefault(x => x.Id == pair.Key);
            var selected = _selected.Contains(pair.Key) && _previewDrawing is null && !_erasing &&
                           (selectedElement is null || !wholeGroups.Any(groupId =>
                               GroupMembers(groupId).Any(element => element.Id == selectedElement.Id)));
            pair.Value.Border.BorderBrush = selected
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(133, 125, 239))
                : Brushes.Transparent;
            pair.Value.Border.BorderThickness = new Thickness(1.2);
            if (pair.Value.Drawing is not null)
                pair.Value.Drawing.Margin = new Thickness(-pair.Value.Border.BorderThickness.Left);
            pair.Value.Border.Cursor = IsDrawingTool(_toolMode) ? BoardSurface.Cursor : Cursors.SizeAll;
        }
        UpdateResizeHandles();
        UpdateTextPaletteForSelection();
        SyncLayerSelection();
        BoardStatus.Text = _selected.Count == 0
            ? "滚轮缩放 · 中键或 Space 拖动画布 · Ctrl 多选"
            : $"已选择 {_selected.Count} 项";
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (LayersPanel.Visibility == Visibility.Visible &&
            IsPointerInsideLayersPanel(e.OriginalSource as DependencyObject)) return;
        PulseInteractiveRenderQuality();
        var oldZoom = _viewZoom;
        var newZoom = Math.Clamp(oldZoom * (e.Delta > 0 ? 1.12 : 1 / 1.12), .05, 8);
        var point = e.GetPosition(BoardSurface);
        var pan = BoardMath.ZoomAt(point, oldZoom, newZoom, _viewPanX, _viewPanY);
        _viewZoom = newZoom;
        _viewPanX = pan.PanX;
        _viewPanY = pan.PanY;
        RequestViewportRender(save: true);
        BoardStatus.Text = $"{newZoom:P0}";
        e.Handled = true;
    }

    private void BeginContinuousInteraction()
    {
        if (RenderOptions.GetBitmapScalingMode(WorldCanvas) != BitmapScalingMode.LowQuality)
            RenderOptions.SetBitmapScalingMode(WorldCanvas, BitmapScalingMode.LowQuality);
        _renderQualityTimer.Stop();
    }

    private void EndContinuousInteraction()
    {
        _renderQualityTimer.Stop();
        _renderQualityTimer.Start();
    }

    private void PulseInteractiveRenderQuality()
    {
        BeginContinuousInteraction();
        EndContinuousInteraction();
    }

    private async void OnBoardDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (_imageEditBusy || _sceneOperation || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        _imageEditBusy = true;
        var world = ScreenToWorld(e.GetPosition(BoardSurface));
        PushUndoSnapshot();
        try
        {
        await _importService.ImportFilesAsync(_drawerId, files, new DrawingPointF((float)world.X, (float)world.Y));
            await ReloadAsync();
        }
        catch (Exception exception) { BoardStatus.Text = exception.Message; }
        finally { _imageEditBusy = false; }
    }

    private void OnBoardDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPasteClick(object sender, RoutedEventArgs e) => await PasteAsync();
    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySelected();

    private async Task PasteAsync()
    {
        if (_imageEditBusy) return;
        var read = _clipboard.ReadImage();
        using var bitmap = read.Bitmap;
        if (!read.HasImage) { BoardStatus.Text = read.ErrorMessage ?? "剪贴板中没有图片"; return; }
        var center = ScreenToWorld(new System.Windows.Point(BoardSurface.ActualWidth / 2, BoardSurface.ActualHeight / 2));
        _imageEditBusy = true;
        BoardSurface.IsHitTestVisible = false;
        try
        {
            var before = Snapshot();
            var imported = await _importService.ImportClipboardAsync(_drawerId, read, new DrawingPointF((float)center.X, (float)center.Y));
            PushUndoSnapshot(before);
            await ReloadAsync();
            BoardStatus.Text = imported.Any(item => GifAnimationService.IsGif(item.AssetPath)) ? "已收集 GIF 动图" : "已收集图片";
        }
        catch (Exception error) { BoardStatus.Text = $"收集失败：{error.Message}"; }
        finally { _imageEditBusy = false; BoardSurface.IsHitTestVisible = true; }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async Task DeleteSelectedAsync()
    {
        await FlushPendingDrawingAsync();
        if (_selected.Count == 0) return;
        PushUndoSnapshot();
        await _repository.DeleteItemsAsync(_items.Where(x => _selected.Contains(x.Id)).Select(x => x.Id).ToArray());
        await _repository.DeleteTextItemsAsync(_textItems.Where(x => _selected.Contains(x.Id)).Select(x => x.Id).ToArray());
        await _repository.DeleteDrawingItemsAsync(_drawingItems.Where(x => _selected.Contains(x.Id)).Select(x => x.Id).ToArray());
        _items.RemoveAll(item => _selected.Contains(item.Id));
        _textItems.RemoveAll(item => _selected.Contains(item.Id));
        _drawingItems.RemoveAll(item => _selected.Contains(item.Id));
        BoardLayerTreeService.RemoveEmptyGroups(_groups, AllElements);
        await PersistLayerTreeAsync();
        _selected.Clear();
        _explicitSelectedGroupId = null;
        await ReloadAsync();
    }

    private async void OnArrangeClick(object sender, RoutedEventArgs e)
    {
        await ArrangeImagesAsync();
    }

    private void OnFitAllClick(object sender, RoutedEventArgs e)
    {
        FitAll();
        BoardStatus.Text = !AllElements.Any() ? "画板中还没有内容" : "已适应全部内容";
    }

    private void FitAll()
    {
        var elements = AllElements.ToArray();
        if (elements.Length == 0)
        {
            _viewZoom = 1;
            _viewPanX = BoardSurface.ActualWidth / 2;
            _viewPanY = BoardSurface.ActualHeight / 2;
            ApplyViewportTransform();
            UpdateResizeHandles();
            QueueViewportSave();
            return;
        }
        var left = elements.Min(x => x.X);
        var top = elements.Min(x => x.Y);
        var right = elements.Max(x => x.X + x.Width);
        var bottom = elements.Max(x => x.Y + x.Height);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var zoom = Math.Clamp(Math.Min((BoardSurface.ActualWidth - 80) / width, (BoardSurface.ActualHeight - 80) / height), .05, 4);
        _viewZoom = zoom;
        _viewPanX = (BoardSurface.ActualWidth - width * zoom) / 2 - left * zoom;
        _viewPanY = (BoardSurface.ActualHeight - height * zoom) / 2 - top * zoom;
        ApplyViewportTransform();
        UpdateResizeHandles();
        QueueViewportSave();
    }

    private async void OnBringForwardClick(object sender, RoutedEventArgs e) => await ShiftZAsync(1);
    private async void OnSendBackwardClick(object sender, RoutedEventArgs e) => await ShiftZAsync(-1);
    private async void OnBringToFrontClick(object sender, RoutedEventArgs e) => await MoveToExtremeAsync(true);
    private async void OnSendToBackClick(object sender, RoutedEventArgs e) => await MoveToExtremeAsync(false);

    private async Task ShiftZAsync(int direction)
    {
        if (_selected.Count == 0) { BoardStatus.Text = "请先选择一个或多个元素"; return; }
        PushUndoSnapshot();
        var ordered = AllElements.OrderBy(x => x.ZIndex).ToList();
        if (direction > 0)
        {
            for (var index = ordered.Count - 2; index >= 0; index--)
                if (_selected.Contains(ordered[index].Id) && !_selected.Contains(ordered[index + 1].Id))
                    (ordered[index], ordered[index + 1]) = (ordered[index + 1], ordered[index]);
        }
        else
        {
            for (var index = 1; index < ordered.Count; index++)
                if (_selected.Contains(ordered[index].Id) && !_selected.Contains(ordered[index - 1].Id))
                    (ordered[index], ordered[index - 1]) = (ordered[index - 1], ordered[index]);
        }
        for (var index = 0; index < ordered.Count; index++) ordered[index].ZIndex = index;
        await PersistElementsAsync(ordered);
        RenderItems();
        UpdateSelectionVisuals();
        BoardStatus.Text = direction > 0 ? "已上移一层" : "已下移一层";
    }

    private async Task MoveToExtremeAsync(bool toFront)
    {
        if (_selected.Count == 0) { BoardStatus.Text = "请先选择一个或多个元素"; return; }
        PushUndoSnapshot();
        var ordered = AllElements.OrderBy(x => x.ZIndex).ToArray();
        var selected = ordered.Where(x => _selected.Contains(x.Id));
        var unselected = ordered.Where(x => !_selected.Contains(x.Id));
        var result = (toFront ? unselected.Concat(selected) : selected.Concat(unselected)).ToArray();
        for (var index = 0; index < result.Length; index++) result[index].ZIndex = index;
        await PersistElementsAsync(result);
        RenderItems();
        UpdateSelectionVisuals();
        BoardStatus.Text = toFront ? "已置于顶层" : "已置于底层";
    }

    private async void OnResetRotationClick(object sender, RoutedEventArgs e) =>
        await ResetSelectedAsync(resetRotation: true, resetSize: false);

    private async void OnResetSizeClick(object sender, RoutedEventArgs e) =>
        await ResetSelectedAsync(resetRotation: false, resetSize: true);

    private async void OnResetImageClick(object sender, RoutedEventArgs e) =>
        await ResetSelectedAsync(resetRotation: true, resetSize: true);

    private async Task ResetSelectedAsync(bool resetRotation, bool resetSize)
    {
        var selectedItems = _items.Where(x => _selected.Contains(x.Id)).ToArray();
        if (selectedItems.Length == 0)
        {
            BoardStatus.Text = "请先选择一张或多张图片";
            return;
        }
        PushUndoSnapshot();
        foreach (var item in selectedItems)
        {
            if (resetRotation) item.Rotation = 0;
            if (resetSize && File.Exists(item.AssetPath))
            {
                try
                {
                    using var source = new DrawingBitmap(item.AssetPath);
                    var fitted = BoardMath.FitSize(source.Width, source.Height);
                    var centerX = item.X + item.Width / 2;
                    var centerY = item.Y + item.Height / 2;
                    item.Width = fitted.Width;
                    item.Height = fitted.Height;
                    item.X = centerX - item.Width / 2;
                    item.Y = centerY - item.Height / 2;
                }
                catch { }
            }
            UpdateItemVisual(item);
        }
        await _repository.UpdateItemsAsync(selectedItems);
        UpdateSelectionVisuals();
        BoardStatus.Text = resetRotation && resetSize
            ? "已重置图片旋转和大小"
            : resetRotation ? "已重置图片旋转" : "已重置图片大小";
    }

    private async void OnBoardSettingsClick(object sender, RoutedEventArgs e)
    {
        var originalColor = _viewport.BackgroundColor;
        var originalOpacity = _viewport.WindowOpacity;
        var originalAffectsImages = _viewport.OpacityAffectsImages;
        var originalShowFrame = _viewport.ShowWindowFrame;
        var window = new BoardSettingsWindow(
            _viewport.BackgroundColor,
            _viewport.WindowOpacity,
            _viewport.OpacityAffectsImages,
            _viewport.ShowWindowFrame)
        {
            Owner = this
        };
        window.PreviewChanged += (color, opacity, affectsImages) =>
        {
            ApplyBackground(color, opacity);
            var itemOpacity = affectsImages ? Math.Clamp(opacity, .1, 1) : 1;
            foreach (var visual in _visuals.Values) visual.Border.Opacity = itemOpacity;
        };
        window.WindowFramePreviewChanged += ApplyWindowFrame;
        if (window.ShowDialog() != true)
        {
            ApplyBackground(originalColor, originalOpacity);
            var itemOpacity = originalAffectsImages ? Math.Clamp(originalOpacity, .1, 1) : 1;
            foreach (var visual in _visuals.Values) visual.Border.Opacity = itemOpacity;
            ApplyWindowFrame(originalShowFrame);
            return;
        }
        _viewport.BackgroundColor = window.BackgroundColor;
        _viewport.WindowOpacity = window.BackgroundOpacity;
        _viewport.OpacityAffectsImages = window.OpacityAffectsImages;
        _viewport.ShowWindowFrame = window.ShowWindowFrame;
        ApplyBackground(_viewport.BackgroundColor, _viewport.WindowOpacity);
        ApplyWindowFrame(_viewport.ShowWindowFrame);
        ApplyItemOpacity();
        await SaveViewportAsync();
        BoardStatus.Text = "画板背景设置已保存";
    }

    private async void OnUndoClick(object sender, RoutedEventArgs e) => await UndoAsync();
    private async void OnRedoClick(object sender, RoutedEventArgs e) => await RedoAsync();

    private async Task LoadBoardShortcutsAsync()
    {
        var settings = await _settingsService.LoadAsync();
        _boardShortcutsEnabled = settings.BoardShortcutsEnabled;
        ApplyUndoLimit(settings.UndoStepLimit);
        ApplyBoardShortcuts(settings.BoardShortcuts);
        _compatibleRendering = settings.CompatibleRendering;
        ApplyRenderMode();
    }

    public async Task ReloadShortcutsAsync() => await LoadBoardShortcutsAsync();

    private void ApplyBoardShortcuts(IReadOnlyDictionary<string, string>? shortcuts)
    {
        _shortcutValues = BoardShortcutCatalog.Merge(shortcuts);
        _shortcutGestures.Clear();
        foreach (var (id, value) in _shortcutValues)
            if (BoardShortcutCatalog.TryParse(value, out var gesture) && gesture is not null)
                _shortcutGestures[id] = gesture;

        UndoMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.Undo];
        RedoMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.Redo];
        PasteMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.Paste];
        ArrangeMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.Arrange];
        GroupMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.Group];
        UngroupMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.Ungroup];
        FitAllMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.FitAll];
        BringForwardMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.BringForward];
        SendBackwardMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.SendBackward];
        BringToFrontMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.BringToFront];
        SendToBackMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.SendToBack];
        DeleteMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.Delete];
        ResetRotationMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.ResetRotation];
        ResetSizeMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.ResetSize];
        ResetImageMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.ResetImage];
        BoardSettingsMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.BoardSettings];
        AddTextMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.AddText];
        DrawMenuItem.InputGestureText = _shortcutValues[BoardShortcutCatalog.Draw];
    }

    private bool TryExecuteBoardShortcut(KeyEventArgs e)
    {
        if (!_boardShortcutsEnabled) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var command = _shortcutGestures.FirstOrDefault(pair =>
            pair.Value.Key == key && pair.Value.Modifiers == Keyboard.Modifiers).Key;
        if (string.IsNullOrEmpty(command)) return false;

        switch (command)
        {
            case BoardShortcutCatalog.Undo: _ = UndoAsync(); break;
            case BoardShortcutCatalog.Redo: _ = RedoAsync(); break;
            case BoardShortcutCatalog.Paste: _ = PasteAsync(); break;
            case BoardShortcutCatalog.Arrange: OnArrangeClick(this, new RoutedEventArgs()); break;
            case BoardShortcutCatalog.Group: _ = GroupImagesAsync(); break;
            case BoardShortcutCatalog.Ungroup: _ = UngroupImagesAsync(); break;
            case BoardShortcutCatalog.FitAll: FitAll(); BoardStatus.Text = "已适应全部内容"; break;
            case BoardShortcutCatalog.BringForward: _ = ShiftZAsync(1); break;
            case BoardShortcutCatalog.SendBackward: _ = ShiftZAsync(-1); break;
            case BoardShortcutCatalog.BringToFront: _ = MoveToExtremeAsync(true); break;
            case BoardShortcutCatalog.SendToBack: _ = MoveToExtremeAsync(false); break;
            case BoardShortcutCatalog.Delete: _ = DeleteSelectedAsync(); break;
            case BoardShortcutCatalog.ResetRotation: _ = ResetSelectedAsync(true, false); break;
            case BoardShortcutCatalog.ResetSize: _ = ResetSelectedAsync(false, true); break;
            case BoardShortcutCatalog.ResetImage: _ = ResetSelectedAsync(true, true); break;
            case BoardShortcutCatalog.BoardSettings: OnBoardSettingsClick(this, new RoutedEventArgs()); break;
            case BoardShortcutCatalog.AddText: SetToolMode(BoardToolMode.Text); break;
            case BoardShortcutCatalog.Draw: SetToolMode(BoardToolMode.Pen); break;
            case BoardShortcutCatalog.Eraser: SetToolMode(BoardToolMode.Eraser); break;
            default: return false;
        }
        return true;
    }

    private void ApplyBackground(string color, double opacity)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, .1, 1) * 255);
        try
        {
            var parsed = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
            parsed.A = alpha;
            var brush = new SolidColorBrush(parsed);
            BoardSurface.Background = brush;
            Background = Brushes.Transparent;
        }
        catch
        {
            _viewport.BackgroundColor = "#7A7A7A";
            BoardSurface.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(alpha, 122, 122, 122));
            Background = Brushes.Transparent;
        }
    }

    private void ApplyItemOpacity()
    {
        var opacity = _viewport.OpacityAffectsImages
            ? Math.Clamp(_viewport.WindowOpacity, .1, 1) : 1;
        foreach (var visual in _visuals.Values) visual.Border.Opacity = opacity;
    }

    private void ApplyWindowFrame(bool enabled)
    {
        if (BoardWindowFrame is null) return;
        var show = enabled && !_isFullScreen;
        BoardWindowFrame.Margin = show ? new Thickness(9) : new Thickness(0);
        BoardWindowFrame.BorderThickness = show ? new Thickness(1) : new Thickness(0);
        BoardWindowFrame.Effect = show ? _windowFrameShadow : null;
    }

    private void ApplyRenderMode()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        source.CompositionTarget.RenderMode = _compatibleRendering
            ? RenderMode.SoftwareOnly
            : RenderMode.Default;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_imageEditBusy) { e.Handled = true; return; }
        if (e.Key == Key.Escape && _imageToolbarId is not null)
        {
            CloseImageToolbar();
            UpdateImageToolbar();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox numericInput &&
            (ReferenceEquals(numericInput, DrawingThicknessText) ||
             ReferenceEquals(numericInput, DrawingOpacityText) ||
             ReferenceEquals(numericInput, EraserDiameterText))) return;
        if (!_boardShortcutsEnabled && e.Key != Key.Escape) return;
        if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OnFullscreenClick(sender, e);
            e.Handled = true;
            return;
        }
        if (_activeTextEditor?.IsKeyboardFocusWithin == true)
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _ = CommitTextEditingAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control && _activeTextEditor.CanRedo)
            {
                _activeTextEditor.Redo();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _activeTextEditor.CanUndo)
            {
                _activeTextEditor.Undo();
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_isFullScreen && _toolMode == BoardToolMode.Select) ToggleFullScreen();
            SetToolMode(BoardToolMode.Select);
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.None)
        {
            SetToolMode(BoardToolMode.Select);
            e.Handled = true;
        }
        else if (e.Key == Key.Space) { _spaceDown = true; HideEraserCursor(); e.Handled = true; }
        else if (TryExecuteBoardShortcut(e)) e.Handled = true;
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _selected.Clear();
            foreach (var item in AllElements) _selected.Add(item.Id);
            UpdateSelectionVisuals();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopySelected(); e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
            RefreshEraserCursor();
        }
    }

    private void CopySelected()
    {
        var item = _items.FirstOrDefault(x => _selected.Contains(x.Id) && File.Exists(x.AssetPath));
        if (item is null) return;
        try
        {
            using var bitmap = new DrawingBitmap(item.AssetPath);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Clipboard.SetImage(image);
            BoardStatus.Text = "已复制图片";
        }
        catch (Exception exception) { BoardStatus.Text = exception.Message; }
    }

    private void PushUndoSnapshot() => PushUndoSnapshot(Snapshot());
    private void PushUndoSnapshot(BoardSnapshot snapshot)
    {
        _undo.Push(snapshot.DeepCopy());
        TrimHistory(_undo);
        _redo.Clear();
        UpdateUndoButtons();
    }
    private BoardSnapshot Snapshot() => new(
        _items.Select(x => x.Clone()).ToList(),
        _textItems.Select(x => x.Clone()).ToList(),
        _drawingItems.Select(x => x.Clone()).ToList(),
        _groups.Select(x => x.Clone()).ToList());

    private Task UndoAsync() => NavigateHistoryAsync(false);
    private Task RedoAsync() => NavigateHistoryAsync(true);

    private void UpdateUndoButtons()
    {
        UndoMenuItem.IsEnabled = !_historyBusy && _undo.Count > 0;
        RedoMenuItem.IsEnabled = !_historyBusy && _redo.Count > 0;
    }

    private void OnBoardRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var itemSource = IsBoardItemSource(source);
        var controlSource = FindVisualAncestor<Button>(source) is not null ||
                            FindVisualAncestor<Thumb>(source) is not null;
        var shiftItemDrag = itemSource && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_isFullScreen || e.ChangedButton != MouseButton.Right ||
            !ShouldStartRightWindowDrag(itemSource, controlSource, Keyboard.Modifiers)) return;

        _rightWindowDragCandidate = true;
        HideEraserCursor();
        _rightWindowDragMoved = false;
        _rightDragStartScreen = PointToScreen(e.GetPosition(this));
        _rightDragStartLeft = Left;
        _rightDragStartTop = Top;
        BoardSurface.CaptureMouse();
        if (shiftItemDrag) e.Handled = true;
    }

    private static bool ShouldStartRightWindowDrag(
        bool itemSource, bool controlSource, ModifierKeys modifiers) =>
        itemSource ? modifiers.HasFlag(ModifierKeys.Shift) : !controlSource;

    private void OnBoardRightMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_rightWindowDragCandidate || e.RightButton != MouseButtonState.Pressed) return;
        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _rightDragStartScreen.X;
        var dy = current.Y - _rightDragStartScreen.Y;
        if (!_rightWindowDragMoved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _rightWindowDragMoved = true;
        Left = _rightDragStartLeft + dx;
        Top = _rightDragStartTop + dy;
        e.Handled = true;
    }

    private void OnBoardRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rightWindowDragCandidate) return;
        _rightWindowDragCandidate = false;
        BoardSurface.ReleaseMouseCapture();
        if (!_rightWindowDragMoved) return;
        _suppressNextContextMenu = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => _suppressNextContextMenu = false));
    }

    private void OnBoardContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ConfigureGifContextMenu(e.OriginalSource as DependencyObject);
        HideEraserCursor();
        if (_suppressNextContextMenu)
        {
            _suppressNextContextMenu = false;
            e.Handled = true;
            return;
        }
        var hasSelection = _selected.Count > 0;
        var hasSelectedImage = _items.Any(x => _selected.Contains(x.Id));
        CopyMenuItem.IsEnabled = hasSelectedImage;
        ExpandSelectedImageGroups();
        GroupMenuItem.IsEnabled = CanGroupImages();
        UngroupMenuItem.IsEnabled = AllElements.Any(x => _selected.Contains(x.Id) && x.GroupId.Length > 0);
        ArrangeMenuItem.Header = hasSelection ? "自动排列选中图片" : "自动排列全部图片";
        ArrangeMenuItem.IsEnabled = _items.Count(x => !hasSelection || _selected.Contains(x.Id)) >= 2;
        UpdateUndoButtons();
        ResetRotationMenuItem.IsEnabled = hasSelectedImage;
        ResetSizeMenuItem.IsEnabled = hasSelectedImage;
        ResetImageMenuItem.IsEnabled = hasSelectedImage;
    }

    private bool IsBoardItemSource(DependencyObject? current)
    {
        while (current is not null && current != BoardSurface)
        {
            if (current is Border { Tag: string id } && _visuals.ContainsKey(id)) return true;
            current = GetTreeParent(current);
        }
        return false;
    }

    private async Task RestoreSnapshotAsync(BoardSnapshot snapshot)
    {
        var currentIds = _items.Select(x => x.Id).ToHashSet();
        var targetIds = snapshot.Images.Select(x => x.Id).ToHashSet();
        await _repository.DeleteItemsAsync(currentIds.Except(targetIds).ToArray());
        await _repository.AddItemsAsync(snapshot.Images.Where(x => !currentIds.Contains(x.Id)).ToArray());
        await _repository.UpdateItemsAsync(snapshot.Images.Where(x => currentIds.Contains(x.Id)).ToArray());

        var currentTextIds = _textItems.Select(x => x.Id).ToHashSet();
        var targetTextIds = snapshot.TextItems.Select(x => x.Id).ToHashSet();
        await _repository.DeleteTextItemsAsync(currentTextIds.Except(targetTextIds).ToArray());
        await _repository.AddTextItemsAsync(snapshot.TextItems.Where(x => !currentTextIds.Contains(x.Id)).ToArray());
        await _repository.UpdateTextItemsAsync(snapshot.TextItems.Where(x => currentTextIds.Contains(x.Id)).ToArray());

        var currentDrawingIds = _drawingItems.Select(x => x.Id).ToHashSet();
        var targetDrawingIds = snapshot.Drawings.Select(x => x.Id).ToHashSet();
        await _repository.DeleteDrawingItemsAsync(currentDrawingIds.Except(targetDrawingIds).ToArray());
        await _repository.AddDrawingItemsAsync(snapshot.Drawings.Where(x => !currentDrawingIds.Contains(x.Id)).ToArray());
        await _repository.UpdateDrawingItemsAsync(snapshot.Drawings.Where(x => currentDrawingIds.Contains(x.Id)).ToArray());
        await _repository.ApplyLayerTreeAsync(_drawerId, snapshot.Groups,
            snapshot.Images.Cast<BoardElement>().Concat(snapshot.TextItems).Concat(snapshot.Drawings).ToArray());
        await ReloadAsync();
    }

    private System.Windows.Point ScreenToWorld(System.Windows.Point screen) => new(
        (screen.X - _viewPanX) / _viewZoom,
        (screen.Y - _viewPanY) / _viewZoom);

    private void RequestViewportRender(bool save)
    {
        _viewSavePending |= save;
        if (_viewRenderPending) return;
        _viewRenderPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _viewRenderPending = false;
            if (_boardClosed) { _viewSavePending = false; return; }
            ApplyViewportTransform();
            RefreshEraserCursor();
            if (_selected.Count > 0) UpdateResizeHandles();
            if (!_viewSavePending) return;
            _viewSavePending = false;
            QueueViewportSave();
        }));
    }

    private void ApplyViewportTransform() =>
        ViewTransform.Matrix = new Matrix(
            _viewZoom, 0, 0, _viewZoom, _viewPanX, _viewPanY);

    private void QueueViewportSave()
    {
        if (_boardClosed) return;
        _viewportTimer.Stop();
        _viewportTimer.Start();
    }

    private Task SaveViewportAsync()
    {
        _viewport.DrawerId = _drawerId;
        _viewport.PanX = _viewPanX;
        _viewport.PanY = _viewPanY;
        _viewport.Zoom = _viewZoom;
        if (WindowState == WindowState.Normal && !_isFullScreen)
        {
            _viewport.WindowLeft = double.IsFinite(Left) ? Left : null;
            _viewport.WindowTop = double.IsFinite(Top) ? Top : null;
            _viewport.WindowWidth = Width;
            _viewport.WindowHeight = Height;
        }
        _viewport.Topmost = Topmost;
        return _repository.SaveViewportAsync(_viewport);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdatePinText();
        QueueViewportSave();
    }

    private void UpdatePinText()
    {
        PinButton.ToolTip = Topmost ? "取消窗口置顶" : "窗口置顶";
        PinButton.Foreground = (Brush)FindResource(Topmost ? "AccentBrush" : "ToolbarTextBrush");
    }

    internal void RefreshTheme()
    {
        UpdatePinText();
        ApplyPrimaryToolTheme();
        UpdateDrawingToolbarState();
        UpdateTextPaletteForSelection();
    }

    private void OnToolbarToggleClick(object sender, RoutedEventArgs e) => ShowToolbar(!_toolbarVisible);

    private void ShowToolbar(bool show)
    {
        if (_toolbarVisible == show) return;
        _toolbarVisible = show;
        Toolbar.IsHitTestVisible = show;
        ToolbarToggleButton.ToolTip = show ? "收起顶部菜单" : "展开顶部菜单";
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        Toolbar.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(Toolbar.Opacity, show ? 1 : 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = easing
            });
        ToolbarTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(ToolbarTranslate.Y, show ? 0 : -72, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = easing
            });
        ToolbarToggleTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(ToolbarToggleTranslate.Y, show ? 57 : 0, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = easing
            });
        ToolbarToggleArrowRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(ToolbarToggleArrowRotate.Angle, show ? 180 : 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = easing
            });
    }
    private void OnToolbarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isFullScreen && e.ChangedButton == MouseButton.Left &&
            FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is null) DragMove();
    }
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result) return result;
            current = GetTreeParent(current);
        }
        return null;
    }

    private static DependencyObject? GetTreeParent(DependencyObject current)
    {
        if (current is FrameworkContentElement content) return content.Parent;
        if (current is FrameworkElement { Parent: { } parent }) return parent;
        try { return VisualTreeHelper.GetParent(current); }
        catch (InvalidOperationException) { return null; }
    }

    private sealed record ItemVisual(Border Border, RichTextBox? Editor, BoardDrawingVisual? Drawing);
    private sealed record BoardSnapshot(
        List<BoardItem> Images,
        List<BoardTextItem> TextItems,
        List<BoardDrawingItem> Drawings,
        List<BoardGroup> Groups)
    {
        public BoardSnapshot DeepCopy() => new(
            Images.Select(x => x.Clone()).ToList(),
            TextItems.Select(x => x.Clone()).ToList(),
            Drawings.Select(x => x.Clone()).ToList(),
            Groups.Select(x => x.Clone()).ToList());
    }
}

public enum BoardRotationCorner
{
    NorthWest,
    NorthEast,
    SouthEast,
    SouthWest
}

public enum BoardToolMode
{
    Select,
    Text,
    Pen,
    Eraser,
    Line,
    Arrow,
    Rectangle,
    Ellipse
}
