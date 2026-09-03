using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenshotCollector.Services;

internal static class RoundedMenus
{
    public static ContextMenu Create()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(FrameworkElement.StyleProperty, "RoundedContextMenu");
        return menu;
    }

    public static MenuItem Item(string header, string glyph, Action? action = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 }
        };
        item.SetResourceReference(FrameworkElement.StyleProperty, "RoundedMenuItem");
        if (action is not null) item.Click += (_, _) => action();
        return item;
    }
}
