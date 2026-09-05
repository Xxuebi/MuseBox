using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Forms = System.Windows.Forms;

namespace ScreenshotCollector;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _source;
    private readonly HotkeyModifiers _originalModifiers;
    private readonly int _originalVirtualKey;
    private HotkeyModifiers _selectedModifiers;
    private int _selectedVirtualKey;
    private Dictionary<string, string> _boardShortcuts;
    private readonly List<SettingsShortcutRow> _shortcutRows = new();
    private readonly List<SettingsShortcutGroup> _allShortcutGroups = new();

    public ObservableCollection<SettingsShortcutGroup> ShortcutGroups { get; } = new();

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        ThemedWindowChromeService.Attach(this);
        var icon = new System.Windows.Media.Imaging.IconBitmapDecoder(
            new Uri("pack://application:,,,/MuseBox;component/Assets/app-icon.ico"),
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        AboutAppIcon.Source = icon.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        var version = typeof(App).Assembly.GetName().Version;
        AppVersionText.Text = version is null
            ? "版本未知"
            : $"版本 {version.Major}.{version.Minor}.{version.Build}";
        _source = settings;
        _originalModifiers = settings.HotkeyModifiers;
        _originalVirtualKey = settings.HotkeyVirtualKey;
        _selectedModifiers = settings.HotkeyModifiers;
        _selectedVirtualKey = settings.HotkeyVirtualKey;
        _boardShortcuts = BoardShortcutCatalog.Merge(settings.BoardShortcuts);
        _boardShortcutsEnabled = settings.BoardShortcutsEnabled;
        BuildShortcutGroups();
        DataContext = this;
        HotkeyToggle.IsChecked = settings.HotkeyEnabled;
        MainTopmostToggle.IsChecked = settings.MainTopmost;
        ShowDrawerLettersToggle.IsChecked = settings.ShowDrawerLetters;
        UseSystemScreenshotToggle.IsChecked = settings.UseSystemScreenshot;
        CompatibleRenderingToggle.IsChecked = settings.CompatibleRendering;
        RefreshSystemAppearanceChoice();
        LightAppearanceRadio.IsChecked = settings.AppearanceMode == AppAppearanceMode.Light;
        DarkAppearanceRadio.IsChecked = settings.AppearanceMode == AppAppearanceMode.Dark;
        SystemAppearanceRadio.IsChecked = settings.AppearanceMode == AppAppearanceMode.FollowSystem;
        Activated += (_, _) => RefreshSystemAppearanceChoice();
        StoragePathTextBox.Text = AppDataPaths.ResolveRoot(settings.BoardStoragePath);
        UndoStepLimitInput.Text = Math.Clamp(settings.UndoStepLimit, 1, 500).ToString();
        RefreshHotkeyText();
        UpdateHotkeyEditorState();
        _batchShortcuts = false;
        RefreshShortcutFeedback();
        RefreshShellIntegration();
    }

    public AppSettings? ResultSettings { get; private set; }

    private void RefreshSystemAppearanceChoice()
    {
        var light = ThemeService.SystemAppearanceMode == AppAppearanceMode.Light;
        SystemAppearanceRadio.Content = light ? "系统 · 白天" : "系统 · 黑夜";
        SystemAppearanceRadio.Background = new SolidColorBrush(light
            ? Color.FromRgb(0xF8, 0xF8, 0xF8)
            : Color.FromRgb(0x25, 0x26, 0x2A));
        SystemAppearanceRadio.Foreground = new SolidColorBrush(light
            ? Color.FromRgb(0x1E, 0x1F, 0x22)
            : Color.FromRgb(0xF4, 0xF4, 0xF4));
    }

    private void OnHotkeyToggleChanged(object sender, RoutedEventArgs e)
    {
        UpdateHotkeyEditorState();
        RefreshShortcutFeedback();
    }

    private void OnHotkeyInputMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!HotkeyInputTextBox.IsKeyboardFocusWithin)
        {
            HotkeyInputTextBox.Focus();
            e.Handled = true;
        }
    }

    private void OnHotkeyInputGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyInputBorder.BorderBrush = (Brush)FindResource("AccentBrush");
        HotkeyInputBorder.BorderThickness = new Thickness(2);
        HotkeyInputTextBox.SelectAll();
        ValidationText.Text = string.Empty;
    }

    private void OnHotkeyInputLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyInputBorder.BorderBrush = (Brush)FindResource("ControlBorderBrush");
        HotkeyInputBorder.BorderThickness = new Thickness(1);
        RefreshHotkeyText();
    }

    private void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _selectedModifiers = _originalModifiers;
            _selectedVirtualKey = _originalVirtualKey;
            RefreshHotkeyText();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }
        if (key is Key.Back or Key.Delete)
        {
            _selectedModifiers = HotkeyModifiers.None;
            _selectedVirtualKey = 0;
            RefreshHotkeyText();
            e.Handled = true;
            return;
        }
        if (key == Key.Tab) return;
        if (IsModifierKey(key))
        {
            var prefix = HotkeyFormatter.Format(ReadModifiers(), 0);
            HotkeyInputTextBox.Text = string.IsNullOrEmpty(prefix) ? "请继续按一个按键…" : $"{prefix} + …";
            e.Handled = true;
            return;
        }
        if (key is Key.Enter or Key.Return)
        {
            ValidationText.Text = "Enter 不能作为截图快捷键。";
            e.Handled = true;
            return;
        }
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            ValidationText.Text = "无法识别这个按键。";
            e.Handled = true;
            return;
        }
        _selectedModifiers = ReadModifiers();
        _selectedVirtualKey = virtualKey;
        RefreshHotkeyText();
        e.Handled = true;
    }

    private void OnClearGlobalHotkeyClick(object sender, RoutedEventArgs e)
    {
        _selectedModifiers = HotkeyModifiers.None;
        _selectedVirtualKey = 0;
        RefreshHotkeyText();
        ValidationText.Text = string.Empty;
    }

    private void OnBoardShortcutInputFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox) textBox.SelectAll();
        ValidationText.Text = string.Empty;
    }

    private void OnBoardShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { Tag: string id }) return;
        var row = _shortcutRows.Single(x => x.Id == id);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }
        if (key is Key.Back or Key.Delete)
        {
            row.Gesture = string.Empty;
            e.Handled = true;
            return;
        }
        if (IsModifierKey(key)) { e.Handled = true; return; }
        if (key is Key.Tab or Key.Enter or Key.Return) return;
        try
        {
            row.Gesture = BoardShortcutCatalog.Format(key, Keyboard.Modifiers);
            ValidationText.Text = string.Empty;
        }
        catch (NotSupportedException)
        {
            ValidationText.Text = "这个按键不能用作快捷键。";
        }
        e.Handled = true;
    }

    private void OnClearBoardShortcutClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            _shortcutRows.Single(x => x.Id == id).Gesture =
                id == BoardShortcutCatalog.ExitBoardMode
                    ? BoardShortcutCatalog.Definitions.Single(x => x.Id == id).DefaultGesture
                    : string.Empty;
        ValidationText.Text = string.Empty;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(UndoStepLimitInput.Text, out var limit) || limit is < 1 or > 500)
        {
            SettingsCategories.SelectedIndex = 0;
            ValidationText.Text = "撤回步骤上限请输入 1–500 的整数。";
            return;
        }
        if (HotkeyToggle.IsChecked == true && _selectedVirtualKey == 0)
        {
            ValidationText.Text = "启用快捷键时必须设置一个按键。";
            return;
        }
        var exitModeGesture = _shortcutRows.Single(x => x.Id == BoardShortcutCatalog.ExitBoardMode).Gesture;
        if (!BoardShortcutCatalog.TryParse(exitModeGesture, out var exitModeKey) || exitModeKey is null ||
            exitModeKey.Modifiers == ModifierKeys.None)
        {
            SettingsCategories.SelectedItem = ShortcutSettingsTab;
            ValidationText.Text = "退出画板模式必须设置包含修饰键的快捷键。";
            return;
        }
        var conflicts = RefreshShortcutFeedback();
        if (!string.IsNullOrEmpty(conflicts))
        {
            SettingsCategories.SelectedItem = ShortcutSettingsTab;
            ValidationText.Text = conflicts;
            return;
        }
        string storageRoot;
        try
        {
            storageRoot = AppDataPaths.ResolveRoot(StoragePathTextBox.Text.Trim());
            Directory.CreateDirectory(storageRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ValidationText.Text = $"保存路径不可用：{exception.Message}";
            return;
        }

        ResultSettings = _source.Copy();
        ResultSettings.Version = 7;
        ResultSettings.HotkeyEnabled = HotkeyToggle.IsChecked == true;
        ResultSettings.HotkeyModifiers = _selectedModifiers;
        ResultSettings.HotkeyVirtualKey = _selectedVirtualKey;
        _boardShortcuts = _shortcutRows.ToDictionary(
            x => x.Id, x => x.Gesture, StringComparer.OrdinalIgnoreCase);
        ResultSettings.BoardShortcuts = BoardShortcutCatalog.Merge(_boardShortcuts);
        ResultSettings.MainTopmost = MainTopmostToggle.IsChecked == true;
        ResultSettings.ShowDrawerLetters = ShowDrawerLettersToggle.IsChecked == true;
        ResultSettings.UseSystemScreenshot = UseSystemScreenshotToggle.IsChecked == true;
        ResultSettings.CompatibleRendering = CompatibleRenderingToggle.IsChecked == true;
        ResultSettings.AppearanceMode = DarkAppearanceRadio.IsChecked == true
            ? AppAppearanceMode.Dark
            : LightAppearanceRadio.IsChecked == true
                ? AppAppearanceMode.Light
                : AppAppearanceMode.FollowSystem;
        ResultSettings.LanguageCode = LanguageService.SimplifiedChinese;
        ResultSettings.UndoStepLimit = limit;
        ResultSettings.BoardShortcutsEnabled = _boardShortcutsEnabled;
        ResultSettings.BoardStoragePath = string.Equals(
            storageRoot, AppDataPaths.DefaultRoot, StringComparison.OrdinalIgnoreCase)
            ? null : storageRoot;
        var currentRoot = AppDataPaths.ResolveRoot(_source.BoardStoragePath);
        ResultSettings.PendingStorageMigrationFrom =
            !string.Equals(currentRoot, storageRoot, StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(Path.Combine(storageRoot, "boards.db"))
                ? currentRoot
                : null;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnBrowseStorageClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择画板资料库保存文件夹",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(StoragePathTextBox.Text)
                ? StoragePathTextBox.Text : AppDataPaths.DefaultRoot
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            StoragePathTextBox.Text = dialog.SelectedPath;
    }

    private void OnDefaultStorageClick(object sender, RoutedEventArgs e) =>
        StoragePathTextBox.Text = AppDataPaths.DefaultRoot;

    private void OnRepairFileAssociationClick(object sender, RoutedEventArgs e) =>
        RunShellIntegration("文件关联已修复。", ShellIntegrationService.RepairAssociation);

    private void OnUninstallFileAssociationClick(object sender, RoutedEventArgs e)
    {
        if (!PromptWindow.Confirm(this, "卸载文件关联",
                "将移除 .mubo 的默认打开方式和场景文件图标，不会删除任何场景文件。", "卸载")) return;
        RunShellIntegration("文件关联已卸载。", ShellIntegrationService.UninstallAssociation);
    }

    private void OnRepairSceneThumbnailClick(object sender, RoutedEventArgs e) =>
        RunShellIntegration("资源管理器缩略图已修复。新保存的场景会显示画板总览。", ShellIntegrationService.RepairThumbnailProvider);

    private void OnUninstallSceneThumbnailClick(object sender, RoutedEventArgs e)
    {
        if (!PromptWindow.Confirm(this, "卸载缩略图功能",
                "将停止资源管理器生成 .mubo 画板缩略图，不会删除场景内已保存的缩略图或场景内容。", "卸载")) return;
        RunShellIntegration("资源管理器缩略图功能已卸载。", ShellIntegrationService.UninstallThumbnailProvider);
    }

    private void RunShellIntegration(string success, Action action)
    {
        try
        {
            action();
            ShellIntegrationFeedback.Text = success;
            ValidationText.Text = string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or InvalidOperationException or ArgumentException)
        {
            ShellIntegrationFeedback.Text = "操作失败：" + error.Message;
        }
        RefreshShellIntegration();
    }

    private void RefreshShellIntegration()
    {
        var status = ShellIntegrationService.GetStatus();
        FileAssociationStatusText.Text = status.AssociationDetail;
        SceneThumbnailStatusText.Text = status.ThumbnailDetail;
        RepairSceneThumbnailButton.IsEnabled = status.FilesAvailable;
        UninstallFileAssociationButton.IsEnabled = status.AssociationInstalled;
        UninstallSceneThumbnailButton.IsEnabled = status.ThumbnailInstalled;
    }

    private void UpdateHotkeyEditorState()
    {
        if (HotkeyInputBorder is not null) HotkeyInputBorder.IsEnabled = HotkeyToggle.IsChecked == true;
    }

    private void RefreshHotkeyText()
    {
        HotkeyInputTextBox.Text = _selectedVirtualKey == 0
            ? "未设置" : HotkeyFormatter.Format(_selectedModifiers, _selectedVirtualKey);
        RefreshShortcutFeedback();
    }

    private static HotkeyModifiers ReadModifiers()
    {
        var keyboard = Keyboard.Modifiers;
        var result = HotkeyModifiers.None;
        if (keyboard.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (keyboard.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (keyboard.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (keyboard.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Windows;
        return result;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private void BuildShortcutGroups()
    {
        AddGroup("编辑", BoardShortcutCatalog.Undo, BoardShortcutCatalog.Redo, BoardShortcutCatalog.Paste, BoardShortcutCatalog.Delete);
        AddGroup("画板视图", BoardShortcutCatalog.FitAll, BoardShortcutCatalog.BoardSettings,
            BoardShortcutCatalog.ExitBoardMode);
        AddGroup("排列与组合", BoardShortcutCatalog.Arrange,
            BoardShortcutCatalog.Group, BoardShortcutCatalog.Ungroup);
        AddGroup("层级", BoardShortcutCatalog.BringForward, BoardShortcutCatalog.SendBackward,
            BoardShortcutCatalog.BringToFront, BoardShortcutCatalog.SendToBack);
        AddGroup("图片", BoardShortcutCatalog.ResetRotation,
            BoardShortcutCatalog.ResetSize, BoardShortcutCatalog.ResetImage);
        AddGroup("注释与绘制", BoardShortcutCatalog.AddText,
            BoardShortcutCatalog.Draw, BoardShortcutCatalog.Eraser);
    }

    private void AddGroup(string displayName, params string[] ids)
    {
        var rows = ids.Select(id =>
        {
            var definition = BoardShortcutCatalog.Definitions.Single(x => x.Id == id);
            var row = new SettingsShortcutRow(id, definition.DisplayName, _boardShortcuts[id]);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsShortcutRow.Gesture)) RefreshShortcutFeedback();
            };
            _shortcutRows.Add(row);
            return row;
        }).ToArray();
        var group = new SettingsShortcutGroup(displayName, rows);
        _allShortcutGroups.Add(group);
        ShortcutGroups.Add(group);
    }

    private void OnShortcutSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_allShortcutGroups.Count == 0) return;
        var query = NormalizeGesture(ShortcutSearchBox.Text.Trim());
        bool Matches(string value) => NormalizeGesture(value).Contains(query, StringComparison.OrdinalIgnoreCase);
        ShortcutGroups.Clear();
        foreach (var group in _allShortcutGroups)
        {
            var rows = Matches(group.DisplayName) ? group.Shortcuts
                : group.Shortcuts.Where(row => Matches(row.DisplayName) || Matches(row.Gesture)).ToArray();
            if (rows.Count > 0) ShortcutGroups.Add(new SettingsShortcutGroup(group.DisplayName, rows));
        }
        GlobalHotkeyCard.Visibility = Matches("全局 框选截图 系统范围 " + HotkeyInputTextBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        NoShortcutResults.Visibility = ShortcutGroups.Count == 0 && GlobalHotkeyCard.Visibility == Visibility.Collapsed
            ? Visibility.Visible : Visibility.Collapsed;
        ShortcutScrollViewer.ScrollToTop();
    }

    private static string NormalizeGesture(string value) =>
        BoardShortcutCatalog.TryParse(value, out var parsed) && parsed is not null
            ? BoardShortcutCatalog.Format(parsed.Key, parsed.Modifiers)
            : value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("Control", "Ctrl", StringComparison.OrdinalIgnoreCase)
            .Replace("Windows", "Win", StringComparison.OrdinalIgnoreCase);
}

public sealed record SettingsShortcutGroup(
    string DisplayName, IReadOnlyList<SettingsShortcutRow> Shortcuts);

public sealed class SettingsShortcutRow : INotifyPropertyChanged
{
    private string _gesture;
    private string _conflictMessage = string.Empty;
    public bool HasConflict => _conflictMessage.Length > 0;
    public string ConflictMessage
    {
        get => _conflictMessage;
        set
        {
            if (_conflictMessage == value) return;
            _conflictMessage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConflictMessage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasConflict)));
        }
    }

    public SettingsShortcutRow(string id, string displayName, string gesture)
    {
        Id = id;
        DisplayName = displayName;
        _gesture = gesture;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Gesture
    {
        get => _gesture;
        set
        {
            if (_gesture == value) return;
            _gesture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gesture)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
