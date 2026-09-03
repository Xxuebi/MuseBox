namespace ScreenshotCollector.Models;

public sealed class AppSettings
{
    public int Version { get; set; } = 7;

    public bool HotkeyEnabled { get; set; } = true;

    public HotkeyModifiers HotkeyModifiers { get; set; } =
        HotkeyModifiers.Control | HotkeyModifiers.Shift;

    public int HotkeyVirtualKey { get; set; } = 0x41; // A

    public bool MainTopmost { get; set; } = true;
    public bool ShowDrawerLetters { get; set; } = true;
    public bool ImmersiveCollectionEnabled { get; set; }
    public bool UseSystemScreenshot { get; set; }
    public AppAppearanceMode AppearanceMode { get; set; } = AppAppearanceMode.FollowSystem;
    public string LanguageCode { get; set; } = "zh-CN";

    public bool CompatibleRendering { get; set; } = true;
    public int UndoStepLimit { get; set; } = 100;
    public bool BoardShortcutsEnabled { get; set; } = true;

    public double? MainLeft { get; set; }

    public double? MainTop { get; set; }
    public double MainWidth { get; set; } = 360;
    public double MainHeight { get; set; } = 500;

    public string? BoardStoragePath { get; set; }

    public string? PendingStorageMigrationFrom { get; set; }

    public Dictionary<string, string> BoardShortcuts { get; set; } =
        BoardShortcutCatalog.CreateDefaults();

    public List<string> SavedColors { get; set; } = ["#000000", "#FFFFFF"];

    public AppSettings Copy() => new()
    {
        Version = Version,
        HotkeyEnabled = HotkeyEnabled,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyVirtualKey = HotkeyVirtualKey,
        MainTopmost = MainTopmost,
        ShowDrawerLetters = ShowDrawerLetters,
        ImmersiveCollectionEnabled = ImmersiveCollectionEnabled,
        UseSystemScreenshot = UseSystemScreenshot,
        AppearanceMode = AppearanceMode,
        LanguageCode = string.IsNullOrWhiteSpace(LanguageCode) ? "zh-CN" : LanguageCode,
        CompatibleRendering = CompatibleRendering,
        UndoStepLimit = Math.Clamp(UndoStepLimit, 1, 500),
        BoardShortcutsEnabled = BoardShortcutsEnabled,
        MainLeft = MainLeft,
        MainTop = MainTop,
        MainWidth = MainWidth,
        MainHeight = MainHeight,
        BoardStoragePath = BoardStoragePath,
        PendingStorageMigrationFrom = PendingStorageMigrationFrom,
        BoardShortcuts = BoardShortcutCatalog.Merge(BoardShortcuts),
        SavedColors = SavedColors?.ToList() ?? ["#000000", "#FFFFFF"]
    };
}

public enum AppAppearanceMode
{
    // Keep the first two numeric values stable for settings written by 1.0.x.
    Light = 0,
    Dark = 1,
    FollowSystem = 2
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}
