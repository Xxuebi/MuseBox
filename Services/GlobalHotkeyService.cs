using System.Runtime.InteropServices;
using System.Windows.Interop;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private readonly int _hotkeyId;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private IntPtr _windowHandle;
    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? Pressed;

    public GlobalHotkeyService(int hotkeyId = 0x5343) => _hotkeyId = hotkeyId;

    public bool Register(IntPtr windowHandle, HotkeyModifiers modifiers, int virtualKey)
    {
        Unregister();

        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WindowProcedure);

        _registered = RegisterHotKey(
            windowHandle,
            _hotkeyId,
            (uint)modifiers | ModNoRepeat,
            (uint)virtualKey);

        if (!_registered)
        {
            _source?.RemoveHook(WindowProcedure);
            _source = null;
            _windowHandle = IntPtr.Zero;
        }

        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, _hotkeyId);
        }

        _source?.RemoveHook(WindowProcedure);
        _source = null;
        _windowHandle = IntPtr.Zero;
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        GC.SuppressFinalize(this);
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == WmHotkey && wordParameter.ToInt32() == _hotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr windowHandle,
        int identifier,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int identifier);
}
