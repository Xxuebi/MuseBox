using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class DrawerCoverRenderer
{
    public static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static BitmapSource Orient(BitmapSource source, CoverCropState crop)
    {
        var turns = ((crop.QuarterTurns % 4) + 4) % 4;
        var transforms = new TransformGroup();
        transforms.Children.Add(new RotateTransform(turns * 90));
        transforms.Children.Add(new ScaleTransform(crop.FlipX ? -1 : 1, crop.FlipY ? -1 : 1));
        transforms.Freeze();
        var image = new TransformedBitmap(source, transforms);
        image.Freeze();
        return image;
    }

    public static (Rect Image, CoverCropState Crop) Place(double imageWidth, double imageHeight,
        double frameWidth, double frameHeight, CoverCropState crop)
    {
        var zoom = double.IsFinite(crop.Zoom) ? Math.Clamp(crop.Zoom, 1, 8) : 1;
        var scale = Math.Max(frameWidth / imageWidth, frameHeight / imageHeight) * zoom;
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        var maxX = Math.Max(0, (width - frameWidth) / 2);
        var maxY = Math.Max(0, (height - frameHeight) / 2);
        var x = double.IsFinite(crop.PanX) ? Math.Clamp(crop.PanX * frameWidth, -maxX, maxX) : 0;
        var y = double.IsFinite(crop.PanY) ? Math.Clamp(crop.PanY * frameHeight, -maxY, maxY) : 0;
        var state = crop with { Zoom = zoom, PanX = x / frameWidth, PanY = y / frameHeight };
        return (new Rect((frameWidth - width) / 2 + x, (frameHeight - height) / 2 + y, width, height), state);
    }

    public static System.Drawing.Bitmap Render(BitmapSource oriented, CoverCropState crop)
    {
        const int width = 1000, height = 720;
        var placement = Place(oriented.PixelWidth, oriented.PixelHeight, width, height, crop);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(oriented, placement.Image);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var decoded = new System.Drawing.Bitmap(stream);
        return new System.Drawing.Bitmap(decoded);
    }
}
