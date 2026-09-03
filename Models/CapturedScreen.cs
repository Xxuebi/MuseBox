using System.Drawing;
using System.Windows.Media.Imaging;

namespace ScreenshotCollector.Models;

public sealed class CapturedScreen : IDisposable
{
    public CapturedScreen(
        string deviceName,
        Rectangle bounds,
        uint dpiX,
        uint dpiY,
        Bitmap bitmap,
        BitmapSource preview)
    {
        DeviceName = deviceName;
        Bounds = bounds;
        DpiX = dpiX;
        DpiY = dpiY;
        Bitmap = bitmap;
        Preview = preview;
    }

    public string DeviceName { get; }

    public Rectangle Bounds { get; }

    public uint DpiX { get; }

    public uint DpiY { get; }

    public Bitmap Bitmap { get; }

    public BitmapSource Preview { get; }

    public void Dispose() => Bitmap.Dispose();
}
