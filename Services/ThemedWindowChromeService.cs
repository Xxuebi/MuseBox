using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenshotCollector.Services;

/// <summary>Provides client-side title-bar interaction without letting the non-client area swallow button clicks.</summary>
internal static class ThemedWindowChromeService
{
    public static void Attach(Window window)
    {
        var titleBarAttached = false;
        window.Loaded += (_, _) =>
        {
            if (titleBarAttached) return;
            window.ApplyTemplate();
            if (window.Template.FindName("ThemedTitleBar", window) is not Border titleBar) return;
            titleBarAttached = true;
            titleBar.PreviewMouseLeftButtonDown += (_, e) => OnTitleBarMouseDown(window, e);
        };
    }

    private static void OnTitleBarMouseDown(Window window, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;

        if (e.ClickCount == 2 && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        if (window.WindowState == WindowState.Maximized) return;
        try
        {
            window.DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException) { }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}

public static class ThemedWindowCommands
{
    public static ICommand Minimize { get; } = new WindowActionCommand(
        window => SystemCommands.MinimizeWindow(window));

    public static ICommand ToggleMaximize { get; } = new WindowActionCommand(window =>
    {
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    });

    public static ICommand Close { get; } = new WindowActionCommand(
        window => SystemCommands.CloseWindow(window));

    private sealed class WindowActionCommand(Action<Window> execute) : ICommand
    {
        public bool CanExecute(object? parameter) => parameter is Window;
        public void Execute(object? parameter)
        {
            if (parameter is Window window) execute(window);
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
