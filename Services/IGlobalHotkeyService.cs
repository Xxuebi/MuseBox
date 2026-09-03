using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? Pressed;

    bool Register(IntPtr windowHandle, HotkeyModifiers modifiers, int virtualKey);

    void Unregister();
}
