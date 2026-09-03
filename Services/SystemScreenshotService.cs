using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenshotCollector.Services;

internal enum SystemSnipResult { Captured, Cancelled, Unavailable, TimedOut }

// The collector is an unpackaged Win32 app: use the normal Windows shortcut,
// not the MSIX-only Snipping Tool callback protocol, and leave captured pixels
// in the system clipboard for the user to place in whichever drawer they choose.
internal sealed class SystemScreenshotService
{
    public async Task<SystemSnipResult> CaptureAsync(CancellationToken cancellationToken)
    {
        var releaseWait = Stopwatch.StartNew();
        while (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(IsKeyDown))
        {
            if (releaseWait.Elapsed.TotalSeconds > 4)
                throw new InvalidOperationException("请松开 Ctrl、Shift、Alt 和 Win 键后再截图。");
            await Task.Delay(40, cancellationToken);
        }
        var clipboardBefore = GetClipboardSequenceNumber();
        var inputs = ScreenshotShortcut();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动 Windows 系统截图。");
        var state = new SystemSnipSession();
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(80, cancellationToken);
            var newImage = GetClipboardSequenceNumber() != clipboardBefore &&
                (IsClipboardFormatAvailable(2) || IsClipboardFormatAvailable(8) || IsClipboardFormatAvailable(17));
            var result = state.Observe(elapsed.Elapsed, IsSnippingForeground(), newImage, IsKeyDown(0x1B));
            if (result.HasValue) return result.Value;
        }
    }

    private static bool IsSnippingForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var id);
        if (id == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName is "SnippingTool" or "ScreenClippingHost";
        }
        catch (ArgumentException) { return false; }
        catch (Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static NativeInput[] ScreenshotShortcut() =>
    [
        Key(0x5B), Key(0x10), Key(0x53), Key(0x53, true), Key(0x10, true), Key(0x5B, true)
    ];
    private static NativeInput Key(ushort key, bool up = false) => new()
        { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0 } } };

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInput { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

internal sealed class SystemSnipSession
{
    private bool _seenOverlay;
    private TimeSpan? _leftOverlayAt;

    public SystemSnipResult? Observe(TimeSpan elapsed, bool overlay, bool newImage, bool escape)
    {
        if (newImage) return SystemSnipResult.Captured;
        if (escape) return SystemSnipResult.Cancelled;
        if (overlay) { _seenOverlay = true; _leftOverlayAt = null; }
        else if (_seenOverlay)
        {
            _leftOverlayAt ??= elapsed;
            // Clipboard completion can trail the overlay closing by several frames.
            if (elapsed - _leftOverlayAt >= TimeSpan.FromSeconds(1.2)) return SystemSnipResult.Cancelled;
        }
        if (!_seenOverlay && elapsed >= TimeSpan.FromSeconds(12)) return SystemSnipResult.Unavailable;
        if (elapsed >= TimeSpan.FromMinutes(5)) return SystemSnipResult.TimedOut;
        return null;
    }
}
