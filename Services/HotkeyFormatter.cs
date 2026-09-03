using System.Windows.Input;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class HotkeyFormatter
{
    public static string Format(HotkeyModifiers modifiers, int virtualKey)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        if (virtualKey != 0) parts.Add(GetKeyName(virtualKey));
        return string.Join(" + ", parts);
    }

    private static string GetKeyName(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A || virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return virtualKey switch
        {
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x2C => "Print Screen",
            0x2D => "Insert",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            _ => FormatWpfKey(virtualKey)
        };
    }

    private static string FormatWpfKey(int virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        return key switch
        {
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.None => $"VK {virtualKey:X2}",
            _ => key.ToString()
        };
    }
}
