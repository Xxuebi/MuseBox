using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class App
{
    private ContextMenu? _trayMenu;
    private bool _openingTrayMenu;

    private async Task ShowTrayMenuAsync()
    {
        if (IsExiting || _openingTrayMenu) return;
        if (_trayMenu?.IsOpen == true) { _trayMenu.IsOpen = false; return; }
        _openingTrayMenu = true;
        try
        {
            var drawers = await Repository.GetDrawersAsync();
            if (IsExiting) return;
            _trayMenu = CreateTrayMenu(drawers);
            _trayMenu.Placement = PlacementMode.MousePoint;
            _trayMenu.Opened += (_, _) =>
            {
                // A hidden collector cannot own foreground activation. Activate the
                // popup itself so click-away and keyboard dismissal also work from tray.
                if (PresentationSource.FromVisual(_trayMenu) is HwndSource source)
                    SetForegroundWindow(source.Handle);
                _trayMenu.Focus();
                Keyboard.Focus(_trayMenu);
            };
            _trayMenu.IsOpen = true;
        }
        catch (Exception)
        {
            _trayIcon?.ShowBalloonTip(2500, "MuseBox", "暂时无法读取抽屉列表，请重新打开菜单。", System.Windows.Forms.ToolTipIcon.Warning);
        }
        finally { _openingTrayMenu = false; }
    }

    internal ContextMenu CreateTrayMenu(IReadOnlyList<Drawer> drawers)
    {
        var menu = RoundedMenus.Create();
        menu.Items.Add(RoundedMenus.Item("显示 MuseBox", "\uE80F", ShowCollector));
        var boards = RoundedMenus.Item("打开画板", "\uE8A5");
        foreach (var drawer in drawers)
        {
            var id = drawer.Id;
            boards.Items.Add(RoundedMenus.Item($"{id} · {drawer.DisplayName}", "\uE7C3", () => OpenBoard(id)));
        }
        menu.Items.Add(boards);
        menu.Items.Add(new Separator());
        menu.Items.Add(RoundedMenus.Item("退出", "\uE8BB", ExitApplication));
        return menu;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
