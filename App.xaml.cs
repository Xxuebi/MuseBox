using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Forms = System.Windows.Forms;

namespace ScreenshotCollector;

public partial class App : Application
{
    private const string InstanceName = "Local\\MuseBox.Instance";
    private const string WakeName = "Local\\MuseBox.Wake";
    private readonly Dictionary<string, BoardWindow> _boards = new();
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _wakeEvent;
    private Forms.NotifyIcon? _trayIcon;
    private readonly IGlobalHotkeyService _boardModeHotkey = new GlobalHotkeyService(0x5344);
    private readonly List<BoardWindow> _boardModeOrder = new();
    private HotkeyModifiers _boardModeHotkeyModifiers;
    private int _boardModeHotkeyVirtualKey;

    public bool IsExiting { get; private set; }
    public IBoardRepository Repository { get; private set; } = null!;
    public BoardImportService ImportService { get; private set; } = null!;
    public MainWindow CollectorWindow { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, InstanceName, out var isFirst);
        _ownsMutex = isFirst;
        if (!isFirst)
        {
            var scenes = SceneActivationService.ScenePaths(e.Args);
            if (scenes.Length > 0)
            {
                try { await SceneActivationService.SendAsync(scenes); }
                catch { PromptWindow.Inform("无法转交场景", "请先退出正在运行的旧版本，再使用新版打开场景文件。"); }
            }
            else { try { EventWaitHandle.OpenExisting(WakeName).Set(); } catch { } }
            Shutdown();
            return;
        }
        InitializeSceneActivation();

        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName);
        _ = Task.Run(() =>
        {
            while (!IsExiting)
            {
                _wakeEvent.WaitOne();
                if (!IsExiting) Dispatcher.BeginInvoke(ShowCollector);
            }
        });

        var settingsService = new JsonSettingsService();
        var startupSettings = await settingsService.LoadAsync();
        LanguageService.Apply(startupSettings.LanguageCode);
        ThemeService.ThemeChanged += OnThemeChanged;
        ThemeService.Apply(this, startupSettings.AppearanceMode);
        ThemeService.StartWatching(this);
        if (!string.IsNullOrWhiteSpace(startupSettings.PendingStorageMigrationFrom))
        {
            try
            {
                await BoardStorageMigrationService.MigrateAsync(
                    startupSettings.PendingStorageMigrationFrom,
                    AppDataPaths.ResolveRoot(startupSettings.BoardStoragePath));
                startupSettings.PendingStorageMigrationFrom = null;
                await settingsService.SaveAsync(startupSettings);
            }
            catch (Exception exception)
            {
                startupSettings.BoardStoragePath = startupSettings.PendingStorageMigrationFrom;
                startupSettings.PendingStorageMigrationFrom = null;
                await settingsService.SaveAsync(startupSettings);
                PromptWindow.Inform("保存路径迁移失败",
                    $"无法迁移画板资料库，已继续使用原路径。\n{exception.Message}");
            }
        }

        var paths = new AppDataPaths(startupSettings.BoardStoragePath);
        Repository = new BoardRepository(paths);
        await Repository.InitializeAsync();
        var assets = new AssetLibraryService(paths, Repository);
        ImportService = new BoardImportService(assets, Repository);
        CollectorWindow = new MainWindow(Repository, ImportService);
        CollectorWindow.PrepareStartupWindow(startupSettings);
        MainWindow = CollectorWindow;
        CreateTrayIcon();
        CollectorWindow.Show();
        _boardModeHotkey.Pressed += (_, _) => Dispatcher.Invoke(ExitMostRecentBoardMode);
        if (!TryConfigureBoardModeHotkey(startupSettings))
            _trayIcon?.ShowBalloonTip(3500, "MuseBox", "退出画板模式快捷键已被其他程序占用。",
                Forms.ToolTipIcon.Warning);
        try
        {
            await CollectorWindow.Initialization;
            _sceneStartup.TrySetResult();
            await HandleSceneFilesAsync(SceneActivationService.ScenePaths(e.Args));
        }
        catch (Exception error) { _sceneStartup.TrySetException(error); PromptWindow.Inform("场景服务初始化失败", error.Message); }
    }

    public void ShowCollector()
    {
        CollectorWindow?.ShowCollectorWindow();
    }

    public void OpenBoard(string drawerId)
    {
        if (_boards.TryGetValue(drawerId, out var existing))
        {
            existing.Show();
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new BoardWindow(drawerId, Repository, ImportService);
        window.Closed += (_, _) => _boards.Remove(drawerId);
        _boards[drawerId] = window;
        window.Show();
    }

    public BoardWindow? FindBoard(string drawerId) => _boards.GetValueOrDefault(drawerId);

    public void NotifyBoardChanged(string drawerId)
    {
        if (_boards.TryGetValue(drawerId, out var board)) _ = board.ReloadAsync();
    }

    public void NotifyBoardShortcutsChanged()
    {
        foreach (var board in _boards.Values)
            _ = board.ReloadShortcutsAsync();
    }

    public bool TryConfigureBoardModeHotkey(AppSettings settings)
    {
        var value = BoardShortcutCatalog.Merge(settings.BoardShortcuts)[BoardShortcutCatalog.ExitBoardMode];
        if (!BoardShortcutCatalog.TryParse(value, out var gesture) || gesture is null ||
            gesture.Modifiers == ModifierKeys.None) return false;
        var modifiers = HotkeyModifiers.None;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Windows;
        var virtualKey = KeyInterop.VirtualKeyFromKey(gesture.Key);
        var handle = new WindowInteropHelper(CollectorWindow).EnsureHandle();
        var oldModifiers = _boardModeHotkeyModifiers;
        var oldVirtualKey = _boardModeHotkeyVirtualKey;
        if (!_boardModeHotkey.Register(handle, modifiers, virtualKey))
        {
            if (oldVirtualKey != 0) _boardModeHotkey.Register(handle, oldModifiers, oldVirtualKey);
            return false;
        }
        _boardModeHotkeyModifiers = modifiers;
        _boardModeHotkeyVirtualKey = virtualKey;
        return true;
    }

    public void NotifyBoardModeEntered(BoardWindow board)
    {
        _boardModeOrder.Remove(board);
        _boardModeOrder.Add(board);
    }

    public void NotifyBoardModeExited(BoardWindow board) => _boardModeOrder.Remove(board);

    public void ExitMostRecentBoardMode()
    {
        _boardModeOrder.RemoveAll(board => !board.HasPresentationMode);
        _boardModeOrder.LastOrDefault()?.ExitPresentationMode();
    }

    public void NotifyThemeChanged()
    {
        foreach (var board in _boards.Values) board.RefreshTheme();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        CollectorWindow?.RefreshTheme();
        NotifyThemeChanged();
    }

    public void CloseBoard(string drawerId)
    {
        if (_boards.Remove(drawerId, out var board)) board.Close();
    }

    public async void ExitApplication()
    {
        if (IsExiting || _requestingExit) return;
        _requestingExit = true;
        try { if (!await CollectorWindow.ConfirmSceneExitAsync()) return; }
        finally { _requestingExit = false; }
        IsExiting = true;
        _sceneActivation?.Dispose();
        await CollectorWindow.SaveWindowStateAsync();
        foreach (var board in _boards.Values.ToArray()) board.Close();
        CollectorWindow.Close();
        _trayIcon?.Dispose();
        _boardModeHotkey.Dispose();
        _wakeEvent?.Set();
        Shutdown();
    }
    private bool _requestingExit;

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "MuseBox",
            Visible = true,
            Icon = new System.Drawing.Icon(GetResourceStream(new Uri("Assets/app-icon.ico", UriKind.Relative))!.Stream)
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowCollector);
        _trayIcon.MouseUp += async (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
                await Dispatcher.InvokeAsync(ShowTrayMenuAsync).Task.Unwrap();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        ThemeService.ThemeChanged -= OnThemeChanged;
        ThemeService.StopWatching();
        _boardModeHotkey.Dispose();
        _sceneActivation?.Dispose();
        if (_trayMenu is not null) _trayMenu.IsOpen = false;
        _trayIcon?.Dispose();
        _wakeEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
