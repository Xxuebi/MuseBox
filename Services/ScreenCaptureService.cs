using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Models;
using FormsScreen = System.Windows.Forms.Screen;

namespace ScreenshotCollector.Services;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    public Task<IReadOnlyList<CapturedScreen>> CaptureAllScreensAsync(
        CancellationToken cancellationToken = default)
    {
        var capturedScreens = new List<CapturedScreen>();

        try
        {
            foreach (var screen in FormsScreen.AllScreens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bounds = screen.Bounds;
                var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);

                try
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(
                            bounds.Left,
                            bounds.Top,
                            0,
                            0,
                            bounds.Size,
                            CopyPixelOperation.SourceCopy);
                    }

                    var (dpiX, dpiY) = GetMonitorDpi(bounds);
                    capturedScreens.Add(new CapturedScreen(
                        screen.DeviceName,
                        bounds,
                        dpiX,
                        dpiY,
                        bitmap,
                        CreateBitmapSource(bitmap)));
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            }

            if (capturedScreens.Count == 0)
            {
                throw new InvalidOperationException("没有检测到可用的显示器。");
            }

            return Task.FromResult<IReadOnlyList<CapturedScreen>>(capturedScreens);
        }
        catch
        {
            foreach (var capturedScreen in capturedScreens)
            {
                capturedScreen.Dispose();
            }

            throw;
        }
    }

    private static BitmapSource CreateBitmapSource(Bitmap bitmap)
    {
        var bitmapHandle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmapHandle);
        }
    }

    private static (uint X, uint Y) GetMonitorDpi(Rectangle bounds)
    {
        var point = new NativePoint
        {
            X = bounds.Left + (bounds.Width / 2),
            Y = bounds.Top + (bounds.Height / 2)
        };

        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero &&
            GetDpiForMonitor(monitor, MonitorDpiType.EffectiveDpi, out var dpiX, out var dpiY) == 0)
        {
            return (dpiX, dpiY);
        }

        return (96, 96);
    }

    private const uint MonitorDefaultToNearest = 2;

    private enum MonitorDpiType
    {
        EffectiveDpi = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
