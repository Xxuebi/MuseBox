using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenshotCollector.Models;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExTopmost = 0x00000008L;
    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const uint GaRoot = 2;
    private const uint GwHwndPrevious = 3;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int IdcArrow = 32512;
    private const int IdcCross = 32515;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipOwnprocess = 0x0002;

    private BoardPresentationMode _presentationMode;
    private bool _modeRestoreTopmost;
    private bool _modeRestoreToolbar;
    private bool _modeRestoreLayers;
    private bool _modeActivationExitArmed;
    private bool _smartHidden;
    private bool _pickerPreviousLeft;
    private bool _pickerPreviousRight;
    private bool _modeInputPreviousLeft;
    private int _modeEpoch;
    private DateTime _lastTaskbarClickUtc = DateTime.MinValue;
    private IntPtr _boardHandle;
    private IntPtr _smartTarget;
    private IntPtr _smartForegroundHook;
    private HwndSource? _boardSource;
    private DispatcherTimer? _pickerTimer;
    private DispatcherTimer? _smartTargetTimer;
    private DispatcherTimer? _modeInputTimer;
    private DispatcherTimer? _modeToastTimer;
    private WinEventDelegate? _smartForegroundCallback;

    internal bool HasPresentationMode => _presentationMode != BoardPresentationMode.None;
    internal BoardPresentationMode PresentationMode => _presentationMode;
    internal string PresentationModeText => ModeName(_presentationMode);

    private void InitializePresentationModes()
    {
        SourceInitialized += (_, _) =>
        {
            _boardHandle = new WindowInteropHelper(this).Handle;
            _boardSource = HwndSource.FromHwnd(_boardHandle);
            _boardSource?.AddHook(BoardModeWindowProcedure);
        };
        Activated += OnPresentationModeActivated;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && HasPresentationMode)
            {
                WindowState = WindowState.Normal;
                ExitPresentationMode();
            }
        };
    }

    private async void OnBoardModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name } ||
            !Enum.TryParse<BoardPresentationMode>(name, out var mode)) return;
        if (_presentationMode == mode) ExitPresentationMode();
        else await EnterPresentationModeAsync(mode);
        e.Handled = true;
    }

    private async Task EnterPresentationModeAsync(BoardPresentationMode mode)
    {
        if (mode == BoardPresentationMode.None) return;
        await CommitTextEditingAsync();
        await FlushPendingDrawingAsync();
        CloseToolPopups();
        if (!HasPresentationMode)
        {
            _modeRestoreTopmost = Topmost;
            _modeRestoreToolbar = _toolbarVisible;
            _modeRestoreLayers = LayersButton.IsChecked == true;
        }
        else
        {
            SetMouseTransparent(false);
            DetachSmartTarget();
            if (_smartHidden)
            {
                _smartHidden = false;
                Show();
            }
            Topmost = _modeRestoreTopmost;
            RestorePresentationVisuals();
            StopModeServices();
        }
        _presentationMode = mode;
        _modeActivationExitArmed = false;
        _modeEpoch++;
        UpdateBoardModeMenu();
        if (Application.Current is App app) app.NotifyBoardModeEntered(this);
        StartModeInputTracking();

        if (mode == BoardPresentationMode.IgnoreMouse)
        {
            HidePersistentBoardUi();
            OverlayCanvas.Visibility = Visibility.Collapsed;
            TextPalette.Visibility = Visibility.Collapsed;
            DrawingPalette.Visibility = Visibility.Collapsed;
            Topmost = true;
            ShowModeToast("无视鼠标模式",
                $"按 {_shortcutValues[BoardShortcutCatalog.ExitBoardMode]}、点击任务栏图标或从 MuseBox 托盘菜单退出");
            SetMouseTransparent(true);
            ArmTaskbarExit();
        }
        else if (mode == BoardPresentationMode.Transparent)
        {
            HidePersistentBoardUi();
            ApplyBackground("#000000", 0);
            // Keep one almost-invisible alpha value so Windows still routes an empty-area
            // click to WPF. This lets the usual surface handler clear the selection.
            BoardSurface.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            ApplyWindowFrame(false);
            ShowModeToast("画板透明模式",
                $"画板内容仍可选择和编辑 · {_shortcutValues[BoardShortcutCatalog.ExitBoardMode]} 退出");
            ArmTaskbarExit();
        }
        else
        {
            Topmost = false;
            ShowModeToast("选择需要置顶的应用窗口", "移动鼠标后单击目标窗口 · 右键或 Esc 取消", persistent: true);
            StartSmartTargetPicker();
        }
    }

    internal void ExitPresentationMode() => ExitPresentationModeCore(showToast: true);

    private void ExitPresentationModeCore(bool showToast)
    {
        if (!HasPresentationMode) return;
        var old = _presentationMode;
        _presentationMode = BoardPresentationMode.None;
        _modeEpoch++;
        _modeActivationExitArmed = false;
        StopModeServices();
        SetMouseTransparent(false);
        DetachSmartTarget();
        if (_smartHidden)
        {
            _smartHidden = false;
            Show();
            SetWindowPos(_boardHandle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        RestorePresentationVisuals();
        Topmost = _modeRestoreTopmost;
        UpdatePinText();
        UpdateBoardModeMenu();
        if (Application.Current is App app) app.NotifyBoardModeExited(this);
        if (showToast) ShowModeToast($"已退出{ModeName(old)}", "画板界面和窗口状态已恢复");
    }

    private void RestorePresentationVisuals()
    {
        Toolbar.Visibility = Visibility.Visible;
        ToolbarToggleButton.Visibility = Visibility.Visible;
        BoardStatusHost.Visibility = Visibility.Visible;
        OverlayCanvas.Visibility = Visibility.Visible;
        ApplyBackground(_viewport.BackgroundColor, _viewport.WindowOpacity);
        ApplyWindowFrame(_viewport.ShowWindowFrame);
        if (_modeRestoreLayers)
        {
            LayersButton.IsChecked = true;
            LayersPanel.Visibility = Visibility.Visible;
            LayersPanel.Opacity = 1;
            LayersPanel.IsHitTestVisible = true;
        }
        else
        {
            LayersButton.IsChecked = false;
            LayersPanel.Visibility = Visibility.Collapsed;
        }
        if (!_modeRestoreToolbar)
        {
            Toolbar.Opacity = 0;
            Toolbar.IsHitTestVisible = false;
            ToolbarTranslate.Y = -72;
            ToolbarToggleTranslate.Y = 0;
            ToolbarToggleArrowRotate.Angle = 0;
        }
        else
        {
            Toolbar.Opacity = 1;
            Toolbar.IsHitTestVisible = true;
            ToolbarTranslate.Y = 0;
            ToolbarToggleTranslate.Y = 57;
            ToolbarToggleArrowRotate.Angle = 180;
        }
        UpdateSelectionVisuals();
    }

    private void HidePersistentBoardUi()
    {
        Toolbar.Visibility = Visibility.Collapsed;
        ToolbarToggleButton.Visibility = Visibility.Collapsed;
        LayersPanel.Visibility = Visibility.Collapsed;
        BoardStatusHost.Visibility = Visibility.Collapsed;
    }

    private void UpdateBoardModeMenu()
    {
        if (IgnoreMouseModeMenuItem is null) return;
        IgnoreMouseModeMenuItem.IsChecked = _presentationMode == BoardPresentationMode.IgnoreMouse;
        TransparentModeMenuItem.IsChecked = _presentationMode == BoardPresentationMode.Transparent;
        SmartTopmostModeMenuItem.IsChecked = _presentationMode == BoardPresentationMode.SmartTopmost;
    }

    private static string ModeName(BoardPresentationMode mode) => mode switch
    {
        BoardPresentationMode.IgnoreMouse => "无视鼠标模式",
        BoardPresentationMode.Transparent => "画板透明模式",
        BoardPresentationMode.SmartTopmost => "智能置顶模式",
        _ => "画板模式"
    };

    private void ShowModeToast(string title, string detail, bool persistent = false)
    {
        _modeToastTimer?.Stop();
        ModeToast.BeginAnimation(OpacityProperty, null);
        ModeToastTitle.Text = title;
        ModeToastDetail.Text = detail;
        ModeToast.Visibility = Visibility.Visible;
        ModeToast.Opacity = 0;
        ModeToast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        if (persistent) return;
        _modeToastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _modeToastTimer.Tick -= OnModeToastElapsed;
        _modeToastTimer.Tick += OnModeToastElapsed;
        _modeToastTimer.Start();
    }

    private void OnModeToastElapsed(object? sender, EventArgs e)
    {
        _modeToastTimer?.Stop();
        var animation = new DoubleAnimation(ModeToast.Opacity, 0, TimeSpan.FromMilliseconds(180));
        animation.Completed += (_, _) => ModeToast.Visibility = Visibility.Collapsed;
        ModeToast.BeginAnimation(OpacityProperty, animation);
    }

    private async void ArmTaskbarExit()
    {
        var epoch = _modeEpoch;
        await Task.Delay(700);
        if (epoch == _modeEpoch && HasPresentationMode) _modeActivationExitArmed = true;
    }

    private async void OnPresentationModeActivated(object? sender, EventArgs e)
    {
        if (!_modeActivationExitArmed || !HasPresentationMode) return;
        if (_presentationMode == BoardPresentationMode.IgnoreMouse)
        {
            ExitPresentationMode();
            return;
        }

        // Activation can arrive before the polling tick that records the taskbar press.
        // Recheck after one short input interval while the pointer is still over the taskbar.
        await Task.Delay(55);
        if (_modeActivationExitArmed && HasPresentationMode &&
            (WasRecentTaskbarClick() || IsCursorOverTaskbar()))
            ExitPresentationMode();
    }

    private void StartModeInputTracking()
    {
        _modeInputPreviousLeft = IsPressed(0x01);
        _lastTaskbarClickUtc = DateTime.MinValue;
        _modeInputTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _modeInputTimer.Tick -= OnModeInputTick;
        _modeInputTimer.Tick += OnModeInputTick;
        _modeInputTimer.Start();
    }

    private void OnModeInputTick(object? sender, EventArgs e)
    {
        var left = IsPressed(0x01);
        if (left && !_modeInputPreviousLeft && IsCursorOverTaskbar())
            _lastTaskbarClickUtc = DateTime.UtcNow;
        _modeInputPreviousLeft = left;
    }

    private bool WasRecentTaskbarClick() =>
        DateTime.UtcNow - _lastTaskbarClickUtc <= TimeSpan.FromSeconds(1);

    private void StartSmartTargetPicker()
    {
        _pickerPreviousLeft = IsPressed(0x01);
        _pickerPreviousRight = IsPressed(0x02);
        _pickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _pickerTimer.Tick += OnSmartTargetPickerTick;
        _pickerTimer.Start();
    }

    private void OnSmartTargetPickerTick(object? sender, EventArgs e)
    {
        SetCursor(LoadCursor(IntPtr.Zero, IdcCross));
        var left = IsPressed(0x01);
        var right = IsPressed(0x02);
        if (right && !_pickerPreviousRight)
        {
            ExitPresentationModeCore(showToast: false);
            ShowModeToast("已退出智能置顶模式", "已取消选择目标窗口");
            return;
        }
        if (left && !_pickerPreviousLeft && GetCursorPos(out var point))
        {
            var target = GetAncestor(WindowFromPoint(point), GaRoot);
            if (!CanUseSmartTarget(target))
                ShowModeToast("无法置顶到这个窗口", "请选择其他应用的普通窗口", persistent: true);
            else AttachSmartTarget(target);
        }
        _pickerPreviousLeft = left;
        _pickerPreviousRight = right;
    }

    private bool CanUseSmartTarget(IntPtr target)
    {
        if (target == IntPtr.Zero || target == _boardHandle || !IsWindow(target) ||
            !IsWindowVisible(target)) return false;
        GetWindowThreadProcessId(target, out var processId);
        if (processId == (uint)Environment.ProcessId) return false;
        var className = new StringBuilder(128);
        GetClassName(target, className, className.Capacity);
        return className.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW");
    }

    private static bool IsCursorOverTaskbar()
    {
        if (!GetCursorPos(out var point)) return false;
        var root = GetAncestor(WindowFromPoint(point), GaRoot);
        if (root == IntPtr.Zero) return false;
        var className = new StringBuilder(128);
        GetClassName(root, className, className.Capacity);
        return className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private void AttachSmartTarget(IntPtr target)
    {
        _pickerTimer?.Stop();
        _smartTarget = target;
        var style = GetWindowLongPtr(_boardHandle, GwlExStyle).ToInt64() | WsExAppWindow;
        SetWindowLongPtr(_boardHandle, GwlExStyle, new IntPtr(style));
        _smartForegroundCallback = OnSmartForegroundChanged;
        _smartForegroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground,
            IntPtr.Zero, _smartForegroundCallback, 0, 0, WineventOutofcontext | WineventSkipOwnprocess);
        PositionAboveSmartTarget();
        var title = new StringBuilder(260);
        GetWindowText(target, title, title.Capacity);
        ShowModeToast("智能置顶已开启",
            string.IsNullOrWhiteSpace(title.ToString()) ? "已绑定目标窗口" : $"已绑定：{title}");
        _smartTargetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _smartTargetTimer.Tick += OnSmartTargetTick;
        _smartTargetTimer.Start();
        ArmTaskbarExit();
    }

    private void OnSmartForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (eventType == EventSystemForeground && hwnd == _smartTarget &&
            _presentationMode == BoardPresentationMode.SmartTopmost)
            PositionAboveSmartTarget();
    }

    private void OnSmartTargetTick(object? sender, EventArgs e)
    {
        if (_smartTarget == IntPtr.Zero || !IsWindow(_smartTarget))
        {
            ExitPresentationMode();
            return;
        }
        var unavailable = IsIconic(_smartTarget) || !IsWindowVisible(_smartTarget);
        if (unavailable && !_smartHidden)
        {
            _smartHidden = true;
            Hide();
        }
        else if (!unavailable && _smartHidden)
        {
            _smartHidden = false;
            Show();
            PositionAboveSmartTarget();
        }
        else if (!unavailable) PositionAboveSmartTarget();
    }

    private void StopModeServices()
    {
        _pickerTimer?.Stop();
        _pickerTimer = null;
        SetCursor(LoadCursor(IntPtr.Zero, IdcArrow));
        _smartTargetTimer?.Stop();
        _smartTargetTimer = null;
        _modeInputTimer?.Stop();
        _modeInputTimer = null;
        _lastTaskbarClickUtc = DateTime.MinValue;
        _modeToastTimer?.Stop();
    }

    private void DetachSmartTarget()
    {
        var hook = _smartForegroundHook;
        _smartForegroundHook = IntPtr.Zero;
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        _smartForegroundCallback = null;
        if (_boardHandle != IntPtr.Zero)
            SetWindowPos(_boardHandle, new IntPtr(-2), 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        _smartTarget = IntPtr.Zero;
    }

    private void PositionAboveSmartTarget()
    {
        if (_boardHandle == IntPtr.Zero || _smartTarget == IntPtr.Zero || !IsWindow(_smartTarget)) return;
        var targetTopmost = (GetWindowLongPtr(_smartTarget, GwlExStyle).ToInt64() & WsExTopmost) != 0;
        if (targetTopmost)
        {
            SetWindowPos(_boardHandle, new IntPtr(-1), 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
            return;
        }
        SetWindowPos(_boardHandle, new IntPtr(-2), 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
        var previous = GetWindow(_smartTarget, GwHwndPrevious);
        if (previous != _boardHandle)
            SetWindowPos(_boardHandle, previous == IntPtr.Zero ? IntPtr.Zero : previous, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void SetMouseTransparent(bool enabled)
    {
        if (_boardHandle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(_boardHandle, GwlExStyle).ToInt64();
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(_boardHandle, GwlExStyle, new IntPtr(style));
        SetWindowPos(_boardHandle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
    }

    private IntPtr BoardModeWindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScMinimize && HasPresentationMode)
        {
            handled = true;
            Dispatcher.BeginInvoke(ExitPresentationMode);
        }
        return IntPtr.Zero;
    }

    private void DisposePresentationModes()
    {
        if (HasPresentationMode)
        {
            _presentationMode = BoardPresentationMode.None;
            SetMouseTransparent(false);
            DetachSmartTarget();
            if (Application.Current is App app) app.NotifyBoardModeExited(this);
        }
        StopModeServices();
        _boardSource?.RemoveHook(BoardModeWindowProcedure);
        _boardSource = null;
    }

    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int cursorName);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder value, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
