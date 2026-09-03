using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotCollector.Services;

/// <summary>A native cursor keeps following the pointer even while a Thumb captures it.</summary>
internal static class BoardRotationCursor
{
    private static readonly Dictionary<int, Cursor> Cached = new();
    public static Cursor Value => ForDpi(1);
    internal static Cursor ForDpi(double scale)
    {
        var size = Math.Clamp((int)Math.Round(32 * scale), 32, 128);
        if (!Cached.TryGetValue(size, out var cursor))
        {
            using var stream = new MemoryStream(CreateCursorData(size));
            Cached[size] = cursor = new Cursor(stream, false);
        }
        return cursor;
    }

    internal static DrawingVisual CreateVisual()
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        var arrows = Geometry.Parse("M7,13 C9,4 21,3 25,12 M25,19 C23,28 11,29 7,20 M19,10 L25,12 L26,6 M13,22 L7,20 L6,26");
        var halo = new Pen(Brushes.White, 4) { StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var stroke = new Pen(new SolidColorBrush(Color.FromRgb(32, 35, 39)), 2)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, halo, arrows);
        dc.DrawGeometry(null, stroke, arrows);
        return visual;
    }

    internal static byte[] CreateCursorData(int size)
    {
        if (size is < 32 or > 128) throw new ArgumentOutOfRangeException(nameof(size));
        // Rasterize vectors at the monitor's native cursor resolution. Never ask
        // Windows to enlarge a 32-pixel bitmap on a high-DPI display.
        var bitmap = new RenderTargetBitmap(size, size, 96d * size / 32, 96d * size / 32, PixelFormats.Pbgra32);
        bitmap.Render(CreateVisual());
        // CUR uses a bottom-up 32-bit DIB, followed by a one-bit transparency mask.
        var straightAlpha = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        var maskStride = ((size + 31) / 32) * 4;
        straightAlpha.CopyPixels(pixels, size * 4, 0);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)1);
            writer.Write((byte)size); writer.Write((byte)size); writer.Write((ushort)0);
            writer.Write((ushort)(size / 2)); writer.Write((ushort)(size / 2));
            writer.Write(40 + pixels.Length + size * maskStride); writer.Write(22);
            writer.Write(40); writer.Write(size); writer.Write(size * 2);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(pixels.Length); writer.Write(0); writer.Write(0);
            writer.Write(0); writer.Write(0);
            for (var row = size - 1; row >= 0; row--) writer.Write(pixels, row * size * 4, size * 4);
            for (var row = size - 1; row >= 0; row--)
            {
                for (var column = 0; column < maskStride * 8; column += 8)
                {
                    byte mask = 0;
                    for (var bit = 0; bit < 8; bit++)
                        if (column + bit < size && pixels[(row * size + column + bit) * 4 + 3] == 0)
                            mask |= (byte)(0x80 >> bit);
                    writer.Write(mask);
                }
            }
        }
        return stream.ToArray();
    }
}
