using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class ThemeService
{
    private static Application? _application;

    public static AppAppearanceMode CurrentMode { get; private set; } = AppAppearanceMode.FollowSystem;
    public static AppAppearanceMode ResolvedMode { get; private set; } = AppAppearanceMode.Light;
    public static AppAppearanceMode SystemAppearanceMode =>
        SystemUsesLightTheme() ? AppAppearanceMode.Light : AppAppearanceMode.Dark;
    public static event EventHandler? ThemeChanged;

    public static void StartWatching(Application application)
    {
        _application = application;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void StopWatching()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _application = null;
    }

    public static void Apply(Application application, AppAppearanceMode mode)
    {
        CurrentMode = mode;
        ResolvedMode = Resolve(mode, SystemUsesLightTheme());
        var light = ResolvedMode == AppAppearanceMode.Light;

        Set(application, "PageBrush", light ? "#FFF3F3F3" : "#FF1F2023");
        Set(application, "CardBrush", light ? "#FFFFFFFF" : "#FF2B2C30");
        Set(application, "TextBrush", light ? "#E4000000" : "#F0FFFFFF");
        Set(application, "MutedTextBrush", light ? "#9E000000" : "#A8FFFFFF");
        Set(application, "ControlBorderBrush", light ? "#26000000" : "#35FFFFFF");
        Set(application, "SubtleBrush", light ? "#FFF0F0F0" : "#FF3A3B40");
        Set(application, "InputBrush", light ? "#FFFBFBFB" : "#FF242529");
        Set(application, "ControlPressedBrush", light ? "#FFE2E2E2" : "#FF47494F");
        Set(application, "AccentSubtleBrush", light ? "#FFD8EBF9" : "#FF29465C");
        Set(application, "SwitchOffBrush", light ? "#FF8B8B8B" : "#FF676A70");
        Set(application, "ToolbarBrush", light ? "#F7FFFFFF" : "#F21B1D21");
        Set(application, "ToolbarButtonBrush", light ? "#FFF7F7F7" : "#FF292B30");
        Set(application, "ToolbarBorderBrush", light ? "#30000000" : "#FF4B4E55");
        Set(application, "ToolbarTextBrush", light ? "#E6000000" : "#FFF0F0F0");
        Set(application, "ToolbarHoverBrush", light ? "#FFE8E8E8" : "#FF3A3D44");
        Set(application, "ToolbarPressedBrush", light ? "#FFD8D8D8" : "#FF17191D");
        Set(application, "DangerTextBrush", light ? "#FFC42B1C" : "#FFFF8A80");
        Set(application, "TitleBarBrush", light ? "#FFF8F8F8" : "#FF25262A");
        Set(application, "WindowBorderBrush", light ? "#33000000" : "#5AFFFFFF");
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static AppAppearanceMode Resolve(AppAppearanceMode mode, bool systemUsesLightTheme) =>
        mode == AppAppearanceMode.FollowSystem
            ? systemUsesLightTheme ? AppAppearanceMode.Light : AppAppearanceMode.Dark
            : mode;

    private static bool SystemUsesLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is not int number || number != 0;
        }
        catch { return true; }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var application = _application;
        if (application is null || CurrentMode != AppAppearanceMode.FollowSystem) return;
        application.Dispatcher.BeginInvoke(() => Apply(application, AppAppearanceMode.FollowSystem));
    }

    private static void Set(Application application, string key, string color)
    {
        var value = (Color)ColorConverter.ConvertFromString(color);
        if (application.Resources[key] is SolidColorBrush { IsFrozen: false } brush)
            brush.Color = value;
        else
            application.Resources[key] = new SolidColorBrush(value);
    }
}
