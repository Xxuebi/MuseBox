using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class MainWindow : Window
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly IBoardRepository _repository;
    private readonly BoardImportService _importService;
    private readonly ISettingsService _settingsService = new JsonSettingsService();
    private readonly IScreenCaptureService _screenCaptureService = new ScreenCaptureService();
    private readonly IRegionSelectionService _regionSelectionService = new RegionSelectionService();
    private readonly IClipboardImageService _clipboardImageService = new ClipboardImageService();
    private readonly IGlobalHotkeyService _hotkeyService = new GlobalHotkeyService();
    private readonly ObservableCollection<DrawerCardModel> _drawers = new();
    private AppSettings _settings = new();
    private bool _isBusy;
    private IntPtr _windowHandle;
    private HwndSource? _windowSource;
    private bool _ignoreNextClipboardUpdate;

    public MainWindow() : this(
        new BoardRepository(new AppDataPaths()),
        CreateFallbackImportService())
    {
    }

    public MainWindow(IBoardRepository repository, BoardImportService importService, ISceneDialogs? sceneDialogs = null)
    {
        _sceneDialogs = sceneDialogs ?? new SceneDialogs();
        _repository = repository;
        _importService = importService;
        InitializeComponent();
        DrawerList.ItemsSource = _drawers;
        InitializeDrawerGestures();
        InitializeCollectionMode();
        InitializeScenes();
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        LocationChanged += (_, _) => RememberPosition();
        SizeChanged += (_, _) => RememberPosition();
        _hotkeyService.Pressed += async (_, _) => await BeginCaptureAsync();
    }

    private static BoardImportService CreateFallbackImportService()
    {
        var paths = new AppDataPaths();
        var repository = new BoardRepository(paths);
        return new BoardImportService(new AssetLibraryService(paths, repository), repository);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_mainInitializationStarted) return;
        _mainInitializationStarted = true;
        try
        {
        await _repository.InitializeAsync();
        if (!_startupStatePrepared)
        {
            _settings = await _settingsService.LoadAsync();
            RestoreMainWindowState();
        }
        UpdatePinVisual();
        ApplyHotkeyRegistration();
          await ReloadDrawersAsync();
          ApplyInitialCollectionWindowHeight();
        RefreshClipboardStatus();
        _initialization.TrySetResult();
        }
        catch (Exception error) { _initialization.TrySetException(error); SetStatus($"初始化失败：{error.Message}", true); }
    }
    private bool _mainInitializationStarted;
    private readonly TaskCompletionSource _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Initialization => _initialization.Task;

    private async Task ReloadDrawersAsync()
    {
        var drawers = await _repository.GetDrawersAsync();
        _drawers.Clear();
        foreach (var drawer in drawers)
        {
            var path = drawer.Cover?.PreviewPath ?? await _repository.GetLatestAssetPathAsync(drawer.Id);
            _drawers.Add(new DrawerCardModel(drawer.Id, drawer.DisplayName, LoadThumbnail(path))
                { ShowLetter = _settings.ShowDrawerLetters, Cover = drawer.Cover, ScenePath = drawer.ScenePath, SceneDirty = drawer.HasUnsavedScene });
        }
    }

    private async void OnDrawerCollectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string drawerId } || _isBusy || CollectionTransitioning) return;
        var read = _clipboardImageService.ReadImage();
        using var bitmap = read.Bitmap;
        if (!read.HasImage)
        {
            SetStatus("剪贴板中没有可用图片。", true);
            return;
        }

        try
        {
            SetBusy(true);
            var feedbackPlayed = PlayCollectionFeedback(drawerId, CreateClipboardFeedback(read));
            var imported = await _importService.ImportClipboardAsync(drawerId, read);
            ShowCollectedNotice(drawerId);
            if (!feedbackPlayed) PlayCollectionFeedback(drawerId, LoadThumbnail(imported[^1].AssetPath));
            await UpdateThumbnailAsync(drawerId, imported[^1].AssetPath);
            ((App)Application.Current).NotifyBoardChanged(drawerId);
            var kind = imported.Any(item => GifAnimationService.IsGif(item.AssetPath)) ? "GIF 动图" : "图片";
            SetStatus($"已收集{kind}到画板 {drawerId}", false);
        }
        catch (Exception error) { SetStatus($"导入失败：{Friendly(error)}", true); }
        finally { SetBusy(false); }
    }

    private async Task ImportBitmapAsync(string drawerId, System.Drawing.Bitmap bitmap)
    {
        try
        {
            SetBusy(true);
            var feedbackPlayed = PlayCollectionFeedback(drawerId, ImageEditorWindow.ToSource(bitmap));
            var imported = await _importService.ImportBitmapAsync(drawerId, bitmap);
            ShowCollectedNotice(drawerId);
            if (!feedbackPlayed) PlayCollectionFeedback(drawerId, LoadThumbnail(imported[^1].AssetPath));
            await UpdateThumbnailAsync(drawerId, imported[^1].AssetPath);
            ((App)Application.Current).NotifyBoardChanged(drawerId);
            SetStatus($"已收集到画板 {drawerId}", false);
        }
        catch (Exception exception)
        {
            SetStatus($"导入失败：{Friendly(exception)}", true);
        }
        finally { SetBusy(false); }
    }

    private void OnDrawerDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = !_isBusy && !CollectionTransitioning && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrawerDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string drawerId } || _isBusy || CollectionTransitioning ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        await ImportFilesAsync(drawerId, files);
    }

    private async Task ImportFilesAsync(string drawerId, IEnumerable<string> files)
    {
        try
        {
            SetBusy(true);
            var paths = files.ToArray();
            var feedbackPlayed = PlayCollectionFeedback(drawerId, LoadThumbnail(paths.FirstOrDefault()));
            var imported = await _importService.ImportFilesAsync(drawerId, paths);
            ShowCollectedNotice(drawerId);
            if (!feedbackPlayed) PlayCollectionFeedback(drawerId, LoadThumbnail(imported[^1].AssetPath));
            await UpdateThumbnailAsync(drawerId, imported[^1].AssetPath);
            ((App)Application.Current).NotifyBoardChanged(drawerId);
            SetStatus($"已收集 {imported.Count} 张图片到画板 {drawerId}", false);
        }
        catch (Exception exception) { SetStatus($"导入失败：{Friendly(exception)}", true); }
        finally { SetBusy(false); }
    }

    private async Task UpdateThumbnailAsync(string drawerId, string path)
    {
        var model = _drawers.First(x => x.Id == drawerId);
        if (model.Cover is not null) return;
        var previous = model.Thumbnail;
        var next = await Task.Run(() => LoadThumbnail(path));
        if (model.Cover is not null) return;
        model.Thumbnail = next;
        if (previous is null || IsCollectionMode) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (IsCollectionMode) return;
            if (DrawerList.ItemContainerGenerator.ContainerFromItem(model) is not ContentPresenter presenter) return;
            var layer = FindVisualChild<Canvas>(presenter, "AnimationLayer");
            if (layer is null) return;
            AnimateCollectedThumbnail(layer, previous);
        });
    }

    private void OnOpenBoardClick(object sender, RoutedEventArgs e)
    {
        if (CollectionTransitioning) return;
        if (IsCollectionMode) { OnDrawerCollectClick(sender, e); return; }
        if (!_isBusy && sender is FrameworkElement { Tag: string drawerId })
            ((App)Application.Current).OpenBoard(drawerId);
    }

    private async void OnDeleteDrawerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string drawerId } || drawerId == "A" || _isBusy) return;
        try
        {
            var model = _drawers.First(x => x.Id == drawerId);
            var previousOrder = _drawers.Select(x => x.Id).ToArray();
            var count = await _repository.GetItemCountAsync(drawerId);
            var action = model.IsBuiltIn ? "重置画板" : "删除抽屉";
            var prompt = model.IsBuiltIn
                ? $"确定清空画板 {drawerId} 的 {count} 项内容，并重置名称和状态吗？"
                : $"确定删除抽屉 {drawerId} 及其中的 {count} 项内容吗？";
            if (!PromptWindow.Confirm(this, action, prompt, model.IsBuiltIn ? "清空并重置" : "删除抽屉")) return;
            SetBusy(true);
            ((App)Application.Current).CloseBoard(drawerId);
            var files = await _repository.DeleteDrawerAsync(drawerId);
            if (model.IsBuiltIn)
            {
                await _repository.InitializeAsync();
                await _repository.UpdateDrawerOrderAsync(previousOrder);
            }
            foreach (var file in files)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            if (model.IsBuiltIn)
            {
                model.DisplayName = "未命名";
                model.Thumbnail = null;
                model.Cover = null;
                model.IsEditing = false;
            }
            else _drawers.Remove(model);
            ((App)Application.Current).NotifyBoardChanged(drawerId);
            SetStatus($"已{action} {drawerId}", false);
        }
        catch (Exception error) { SetStatus($"操作失败：{Friendly(error)}", true); }
        finally { SetBusy(false); }
    }

    private void OnRenameDrawerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string drawerId }) return;
        var model = _drawers.First(x => x.Id == drawerId);
        model.IsEditing = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (DrawerList.ItemContainerGenerator.ContainerFromItem(model) is not ContentPresenter presenter) return;
            var input = FindVisualChild<TextBox>(presenter, "DrawerNameInput");
            input?.Focus();
            input?.SelectAll();
        });
    }

    private async void OnSaveDrawerNameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string drawerId }) return;
        await SaveDrawerNameAsync(drawerId);
    }

    private async void OnDrawerNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string drawerId } textBox) return;
        await SaveDrawerNameAsync(drawerId, textBox.Text);
    }

    private void OnDrawerNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox) return;
        _ = SaveDrawerNameAsync((string)textBox.Tag, textBox.Text);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private async Task SaveDrawerNameAsync(string drawerId, string? value = null)
    {
        var model = _drawers.First(x => x.Id == drawerId);
        if (!model.IsEditing) return;
        var name = string.IsNullOrWhiteSpace(value ?? model.DisplayName)
            ? "未命名" : (value ?? model.DisplayName).Trim();
        model.DisplayName = name;
        model.IsEditing = false;
        await _repository.UpdateDrawerNameAsync(drawerId, name);
        ((App)Application.Current).NotifyBoardChanged(drawerId);
        SetStatus($"画板 {drawerId} 已重命名", false);
    }

    private async void OnScreenshotClick(object sender, RoutedEventArgs e) => await BeginCaptureAsync();

    private async Task BeginCaptureAsync()
    {
        if (_isBusy) return;
        IReadOnlyList<CapturedScreen>? screens = null;
        try
        {
            SetBusy(true);
            Hide();
            await Task.Delay(160);
            if (_settings.UseSystemScreenshot)
            {
                await CaptureWithSystemAsync();
                return;
            }
            screens = await _screenCaptureService.CaptureAllScreensAsync();
            var selection = await _regionSelectionService.SelectRegionAsync(screens);
            if (selection is null)
            {
                SetStatus("已取消截图。", false);
                return;
            }
            using var bitmap = selection.Screen.Bitmap.Clone(selection.PixelBounds, PixelFormat.Format32bppPArgb);
            SetClipboardBitmap(bitmap);
            SetStatus(IsCollectionMode ? "截图已进入剪贴板，点击抽屉收集。" : "截图已进入剪贴板，点击抽屉上方收集。", false);
        }
        catch (OperationCanceledException) when (_captureLifetime.IsCancellationRequested) { }
        catch (Exception exception) { SetStatus($"截图失败：{Friendly(exception)}", true); }
        finally
        {
            if (screens is not null) foreach (var screen in screens) screen.Dispose();
            if (!_mainClosed && Application.Current is not App { IsExiting: true })
            {
                ShowCollectorWindow();
            }
            SetBusy(false);
        }
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _hotkeyService.Unregister();
        var window = new SettingsWindow(_settings.Copy()) { Owner = this };
        if (window.ShowDialog() == true && window.ResultSettings is not null)
        {
            var oldStorageRoot = AppDataPaths.ResolveRoot(_settings.BoardStoragePath);
            _settings = window.ResultSettings;
            await _settingsService.SaveAsync(_settings);
            LanguageService.Apply(_settings.LanguageCode);
            ThemeService.Apply(Application.Current, _settings.AppearanceMode);
            ApplyMainRenderMode();
            Topmost = _settings.MainTopmost;
            ApplyDrawerLetterVisibility();
            UpdatePinVisual();
            UpdateCollectionModeButton();
            ((App)Application.Current).NotifyBoardShortcutsChanged();
            ((App)Application.Current).NotifyThemeChanged();
            if (!string.Equals(
                    oldStorageRoot,
                    AppDataPaths.ResolveRoot(_settings.BoardStoragePath),
                    StringComparison.OrdinalIgnoreCase))
                SetStatus("画板保存路径已更新，重启应用后生效。", false);
        }
        ApplyHotkeyRegistration();
    }

    private void ApplyHotkeyRegistration()
    {
        _hotkeyService.Unregister();
        var gesture = HotkeyFormatter.Format(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
        HotkeyText.Text = _settings.HotkeyEnabled
            ? gesture.Replace(" + ", "+")
            : "截图快捷键已关闭";
        if (!_settings.HotkeyEnabled) return;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (!_hotkeyService.Register(handle, _settings.HotkeyModifiers, _settings.HotkeyVirtualKey))
            SetStatus("截图快捷键已被其他程序占用。", true);
    }

    private void OnTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _settings.MainTopmost = Topmost;
        UpdatePinVisual();
        _ = _settingsService.SaveAsync(_settings);
    }

    private void UpdatePinVisual()
    {
        PinButton.Foreground = Topmost
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("TextBrush");
        PinButton.Background = Topmost
            ? (Brush)FindResource("AccentSubtleBrush")
            : Brushes.Transparent;
        PinButton.ToolTip = Topmost ? "取消窗口置顶" : "窗口置顶";
    }

    internal void RefreshTheme()
    {
        UpdatePinVisual();
        UpdateCollectionModeButton();
    }

    private void OnWindowChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || e.GetPosition(this).Y > 50 ||
            FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null ||
            FindVisualAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        DragMove();
    }

    private void OnMainMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        RememberPosition();
        if (Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            Hide();
        }
    }

    public async Task SaveWindowStateAsync()
    {
        RememberPosition();
        await _settingsService.SaveAsync(_settings);
    }

    private void RememberPosition()
    {
        if (!_mainStateReady || _restoringMainState || !IsLoaded || WindowState != WindowState.Normal) return;
        _settings.MainLeft = Left;
        _settings.MainTop = Top;
        _settings.MainTopmost = Topmost;
        _settings.MainWidth = ActualWidth;
          if (!IsCollectionMode && !CollectionTransitioning)
              _settings.MainHeight = ActualHeight;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        // Collection handlers already reject concurrent imports. Disabling the
        // whole list changes every button's appearance for a frame and flashes.
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#C42B1C" : "#0F7B0F"));
    }

    private void RefreshClipboardStatus()
    {
        var read = _clipboardImageService.ReadImage();
        using var bitmap = read.Bitmap;
        var hasImage = read.HasImage;
        ClearClipboardButton.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(hasImage ? (IsCollectionMode ? "剪贴板有图片，点击抽屉收集" : "剪贴板有图片，点击抽屉上方收集") : "等待截图、复制或拖入图片", false);
    }

    private void OnClearClipboardClick(object sender, RoutedEventArgs e)
    {
        _ignoreNextClipboardUpdate = true;
        var result = _clipboardImageService.Clear();
        if (!result.Success)
        {
            _ignoreNextClipboardUpdate = false;
            SetStatus(result.ErrorMessage ?? "无法清除剪贴板图片。", true);
            return;
        }
        ClearClipboardButton.Visibility = Visibility.Collapsed;
        SetStatus("已清除剪贴板图片。", false);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        ApplyMainRenderMode();
        _windowSource?.AddHook(WindowProcedure);
        AddClipboardFormatListener(_windowHandle);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnMainDisplaySettingsChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _mainClosed = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnMainDisplaySettingsChanged;
        _captureLifetime.Cancel();
        if (_windowHandle != IntPtr.Zero) RemoveClipboardFormatListener(_windowHandle);
        _windowSource?.RemoveHook(WindowProcedure);
        _hotkeyService.Dispose();
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0084 && WindowState == WindowState.Normal && ResizeMode == ResizeMode.CanResize)
        {
            var packed = lParam.ToInt64();
            var point = PointFromScreen(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
            var hit = MainResizeHitTest(point, new Size(ActualWidth, ActualHeight));
            if (hit != 0) { handled = true; return new IntPtr(hit); }
        }
        if (message == WmClipboardUpdate && !_isBusy)
        {
            if (_ignoreNextClipboardUpdate) _ignoreNextClipboardUpdate = false;
            else Dispatcher.BeginInvoke(RefreshClipboardStatus);
        }
        return IntPtr.Zero;
    }

    private static void SetClipboardBitmap(System.Drawing.Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        Clipboard.SetImage(image);
    }

    private static ImageSource? LoadThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 400;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static ImageSource? CreateClipboardFeedback(ClipboardImageResult clipboard)
    {
        if (clipboard.Bitmap is not null) return ImageEditorWindow.ToSource(clipboard.Bitmap);
        if (clipboard.FilePaths.FirstOrDefault() is { } path && LoadThumbnail(path) is { } file) return file;
        if (clipboard.EncodedImageBytes is not { Length: > 0 } bytes) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 400;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static T? FindVisualChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T target && target.Name == name) return target;
            var nested = FindVisualChild<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result) return result;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static string Friendly(Exception exception) =>
        exception is IOException or UnauthorizedAccessException ? "无法写入本地资料库。" : exception.Message;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

public sealed class DrawerCardModel : INotifyPropertyChanged
{
    public string? ScenePath { get; set; }
    private bool _sceneDirty;
    public bool SceneDirty
    {
        get => _sceneDirty;
        set { _sceneDirty = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SceneMarkerVisibility))); }
    }
    public Visibility SceneMarkerVisibility => SceneDirty ? Visibility.Visible : Visibility.Collapsed;
    private DrawerCover? _cover;
    public DrawerCover? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailStretch)));
        }
    }
    public Stretch ThumbnailStretch => Cover is null ? Stretch.Uniform : Stretch.UniformToFill;
    private ImageSource? _thumbnail;
    private string _displayName;
    private bool _isEditing;
    private bool _showLetter = true;
    private bool _isDragging;
    public DrawerCardModel(string id, string displayName, ImageSource? thumbnail)
    {
        Id = id;
        _displayName = displayName;
        _thumbnail = thumbnail;
    }
    public string Id { get; }
    public bool ShowLetter
    {
        get => _showLetter;
        set { _showLetter = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LetterVisibility))); }
    }
    public Visibility LetterVisibility => _showLetter ? Visibility.Visible : Visibility.Collapsed;
    public bool IsDragging
    {
        get => _isDragging;
        set { _isDragging = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardOpacity))); }
    }
    public double CardOpacity => _isDragging ? .18 : 1;
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpenToolTip)));
        }
    }
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyHintVisibility)));
        }
    }
    public Visibility EmptyHintVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    public string OpenToolTip => $"打开画板 {Id} · {DisplayName}";
    public bool IsBuiltIn => Id is "A" or "B" or "C" or "D";
    public string DeleteToolTip => IsBuiltIn ? "清空并重置画板" : "删除抽屉及其内容";
    public Visibility DeleteVisibility => Id == "A" ? Visibility.Collapsed : Visibility.Visible;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayModeVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditModeVisibility)));
        }
    }
    public Visibility DisplayModeVisibility => IsEditing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditModeVisibility => IsEditing ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
}
