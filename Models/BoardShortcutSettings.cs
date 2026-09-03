using System.Windows.Input;

namespace ScreenshotCollector.Models;

public sealed record BoardShortcutDefinition(string Id, string DisplayName, string DefaultGesture);
public sealed record BoardKeyGesture(Key Key, ModifierKeys Modifiers);

public static class BoardShortcutCatalog
{
    public const string Undo = "undo";
    public const string Redo = "redo";
    public const string Paste = "paste";
    public const string Arrange = "arrange";
    public const string Group = "group";
    public const string Ungroup = "ungroup";
    public const string FitAll = "fit_all";
    public const string BringForward = "bring_forward";
    public const string SendBackward = "send_backward";
    public const string BringToFront = "bring_to_front";
    public const string SendToBack = "send_to_back";
    public const string Delete = "delete";
    public const string ResetRotation = "reset_rotation";
    public const string ResetSize = "reset_size";
    public const string ResetImage = "reset_image";
    public const string BoardSettings = "board_settings";
    public const string AddText = "add_text";
    public const string Draw = "draw";
    public const string Eraser = "eraser";

    public static IReadOnlyList<BoardShortcutDefinition> Definitions { get; } =
    [
        new(Undo, "撤回", "Ctrl+Z"),
        new(Redo, "重做", "Ctrl+Y"),
        new(Paste, "粘贴", "Ctrl+V"),
        new(Arrange, "自动排布", "Ctrl+Alt+G"),
        new(Group, "组合", "Ctrl+G"),
        new(Ungroup, "解散组合", "Ctrl+Shift+G"),
        new(FitAll, "适应全部", "Ctrl+0"),
        new(BringForward, "上移一层", "Ctrl+Up"),
        new(SendBackward, "下移一层", "Ctrl+Down"),
        new(BringToFront, "置于顶层", "Ctrl+Shift+Up"),
        new(SendToBack, "置于底层", "Ctrl+Shift+Down"),
        new(Delete, "删除", "Delete"),
        new(ResetRotation, "重置旋转", "Ctrl+Alt+R"),
        new(ResetSize, "重置大小", "Ctrl+Alt+0"),
        new(ResetImage, "重置旋转和大小", "Ctrl+Shift+Alt+R"),
        new(BoardSettings, "画板设置", "Ctrl+Alt+S"),
        new(AddText, "添加注释", "T"),
        new(Draw, "画笔", "B"),
        new(Eraser, "橡皮擦", "E")
    ];

    public static Dictionary<string, string> CreateDefaults() =>
        Definitions.ToDictionary(x => x.Id, x => x.DefaultGesture, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string>? source)
    {
        var result = CreateDefaults();
        if (source is null) return result;
        foreach (var (key, value) in source)
            if (result.ContainsKey(key)) result[key] = value?.Trim() ?? string.Empty;
        // 1.0.21 used Ctrl+Shift+G for arrange. Move that untouched legacy default
        // out of the way so the conventional ungroup shortcut is immediately usable.
        if (!source.ContainsKey(Group) && !source.ContainsKey(Ungroup) &&
            string.Equals(result[Arrange], "Ctrl+Shift+G", StringComparison.OrdinalIgnoreCase))
            result[Arrange] = Definitions.Single(x => x.Id == Arrange).DefaultGesture;
        return result;
    }

    public static bool TryParse(string? value, out BoardKeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            // WPF KeyGesture rejects unmodified letters. Board tools intentionally
            // support B/T/E and other single keys, so parse the key/modifiers directly.
            var parts = value.Split('+', StringSplitOptions.TrimEntries);
            var modifiers = ModifierKeys.None;
            foreach (var part in parts.Take(parts.Length - 1))
            {
                var modifier = part.ToUpperInvariant() switch
                {
                    "CTRL" or "CONTROL" => ModifierKeys.Control,
                    "ALT" => ModifierKeys.Alt,
                    "SHIFT" => ModifierKeys.Shift,
                    "WIN" or "WINDOWS" => ModifierKeys.Windows,
                    _ => (ModifierKeys)(-1)
                };
                if ((int)modifier < 0) return false;
                modifiers |= modifier;
            }
            if (new KeyConverter().ConvertFromInvariantString(parts[^1]) is not Key key || key == Key.None) return false;
            gesture = new BoardKeyGesture(key, modifiers);
            return true;
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException or FormatException) { return false; }
    }

    public static string Format(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(new KeyConverter().ConvertToInvariantString(key) ?? string.Empty);
        return string.Join("+", parts);
    }
}
