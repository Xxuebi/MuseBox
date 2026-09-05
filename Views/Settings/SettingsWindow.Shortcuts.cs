using System.Windows;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class SettingsWindow
{
    private bool _boardShortcutsEnabled = true;
    private bool _batchShortcuts = true;

    private string RefreshShortcutFeedback()
    {
        if (_batchShortcuts || ShortcutConflictText is null) return string.Empty;
        foreach (var row in _shortcutRows) row.ConflictMessage = string.Empty;
        var messages = new List<string>();
        var globalConflict = false;
        if (_boardShortcutsEnabled || _shortcutRows.Any(x => x.Id == BoardShortcutCatalog.ExitBoardMode))
        {
            var active = _shortcutRows.Where(x => !string.IsNullOrWhiteSpace(x.Gesture) &&
                (_boardShortcutsEnabled || x.Id == BoardShortcutCatalog.ExitBoardMode)).ToArray();
            foreach (var group in active.GroupBy(x => NormalizeGesture(x.Gesture), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var message = $"{group.First().Gesture} 冲突：{string.Join("、", group.Select(x => x.DisplayName))}";
                messages.Add(message);
                foreach (var row in group) row.ConflictMessage = message;
            }
            // These are navigation/editing shortcuts kept outside the configurable catalog.
            var reserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["F11"] = "全屏", ["V"] = "选择工具", ["Space"] = "平移", ["Esc"] = "退出当前工具", ["Escape"] = "退出当前工具",
              ["Ctrl+A"] = "全选", ["Ctrl+C"] = "复制", ["Ctrl+Enter"] = "结束文字编辑" };
            foreach (var row in active)
            {
                if (reserved.TryGetValue(NormalizeGesture(row.Gesture), out var name))
                {
                    row.ConflictMessage = $"{row.Gesture} 已用于{name}";
                    messages.Add(row.ConflictMessage);
                }
            }
            if (HotkeyToggle.IsChecked == true && _selectedVirtualKey != 0)
            {
                var global = NormalizeGesture(HotkeyFormatter.Format(_selectedModifiers, _selectedVirtualKey));
                foreach (var row in active.Where(x => string.Equals(NormalizeGesture(x.Gesture), global, StringComparison.OrdinalIgnoreCase)))
                {
                    globalConflict = true;
                    row.ConflictMessage = $"截图快捷键与“{row.DisplayName}”冲突";
                    messages.Add(row.ConflictMessage);
                }
                if (reserved.TryGetValue(global, out var name))
                {
                    globalConflict = true;
                    messages.Add($"截图快捷键已用于{name}");
                }
            }
        }
        if (HotkeyToggle.IsChecked == true && _selectedVirtualKey == 0)
        {
            globalConflict = true;
            messages.Add("截图快捷键已启用，请设置按键");
        }
        var messageText = string.Join("；", messages.Distinct());
        ShortcutConflictText.Text = messageText.Length > 0 ? messageText
            : !_boardShortcutsEnabled ? "普通画板快捷键已禁用；“退出画板模式”仍保持启用" : string.Empty;
        ShortcutConflictText.Visibility = ShortcutConflictText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShortcutConflictText.Foreground = (Brush)FindResource(messageText.Length > 0 ? "TextBrush" : "MutedTextBrush");
        if (messageText.Length > 0) ShortcutConflictText.Foreground = System.Windows.Media.Brushes.Firebrick;
        HotkeyInputBorder.BorderBrush = globalConflict ? System.Windows.Media.Brushes.Firebrick
            : (Brush)FindResource(HotkeyInputTextBox.IsKeyboardFocusWithin ? "AccentBrush" : "ControlBorderBrush");
        DisableAllShortcutsButton.Content = !_boardShortcutsEnabled && HotkeyToggle.IsChecked != true ? "启用所有快捷键" : "禁用所有快捷键";
        return messageText;
    }

    private void OnRestoreShortcutDefaultsClick(object sender, RoutedEventArgs e)
    {
        _batchShortcuts = true;
        var defaults = new AppSettings();
        foreach (var row in _shortcutRows) row.Gesture = defaults.BoardShortcuts[row.Id];
        _selectedModifiers = defaults.HotkeyModifiers;
        _selectedVirtualKey = defaults.HotkeyVirtualKey;
        HotkeyToggle.IsChecked = true;
        _boardShortcutsEnabled = true;
        RefreshHotkeyText();
        _batchShortcuts = false;
        ValidationText.Text = string.Empty;
        RefreshShortcutFeedback();
    }

    private void OnDisableAllShortcutsClick(object sender, RoutedEventArgs e)
    {
        var enable = !_boardShortcutsEnabled && HotkeyToggle.IsChecked != true;
        _batchShortcuts = true;
        _boardShortcutsEnabled = enable;
        HotkeyToggle.IsChecked = enable && _selectedVirtualKey != 0;
        _batchShortcuts = false;
        ValidationText.Text = string.Empty;
        RefreshShortcutFeedback();
    }
}
