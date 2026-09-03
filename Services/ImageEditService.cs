using System.Drawing;
using System.Drawing.Imaging;

namespace ScreenshotCollector.Services;

public static class ImageEditService
{
    public static Bitmap Crop(Bitmap source, Rectangle crop)
    {
        crop.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (crop.Width < 1 || crop.Height < 1) throw new ArgumentException("裁切区域不能为空。");
        return source.Clone(crop, PixelFormat.Format32bppArgb);
    }

    public static Bitmap Adjust(Bitmap source, double brightness, double contrast, double saturation, double hue = 0, double opacity = 1)
    {
        if (!double.IsFinite(brightness) || !double.IsFinite(contrast) || !double.IsFinite(saturation) || !double.IsFinite(hue) || !double.IsFinite(opacity))
            throw new ArgumentException("颜色参数无效。");
        var b = (float)Math.Clamp(brightness, -1, 1);
        var c = (float)Math.Clamp(contrast, 0, 2);
        var s = (float)Math.Clamp(saturation, 0, 2);
        var alpha = (float)Math.Clamp(opacity, 0, 1);
        if (b == 0 && c == 1 && s == 1 && hue % 360 == 0 && alpha == 1) return (Bitmap)source.Clone();
        var cos = Math.Cos(hue * Math.PI / 180);
        var sin = Math.Sin(hue * Math.PI / 180);
        // Luminance-preserving hue rotation, composed with saturation and contrast.
        // One native color-matrix pass handles all five controls for a smooth preview.
        var rotation = new[,]
        {
            { .213 + .787*cos - .213*sin, .715 - .715*cos - .715*sin, .072 - .072*cos + .928*sin },
            { .213 - .213*cos + .143*sin, .715 + .285*cos + .140*sin, .072 - .072*cos - .283*sin },
            { .213 - .213*cos - .787*sin, .715 - .715*cos + .715*sin, .072 + .928*cos + .072*sin }
        };
        var luminance = new[] { .2126, .7152, .0722 };
        var channels = new float[3,3];
        for (var output = 0; output < 3; output++)
        for (var input = 0; input < 3; input++)
        for (var k = 0; k < 3; k++)
            channels[output,input] += (float)(rotation[output,k] * (luminance[input] * (1-s) + (k == input ? s : 0)) * c);
        var offset = .5f * (1 - c) + b;
        var matrix = new ColorMatrix(new[]
        {
            new[] { channels[0,0], channels[1,0], channels[2,0], 0f, 0f },
            new[] { channels[0,1], channels[1,1], channels[2,1], 0f, 0f },
            new[] { channels[0,2], channels[1,2], channels[2,2], 0f, 0f },
            new[] { 0f, 0f, 0f, alpha, 0f },
            new[] { offset, offset, offset, 0f, 1f }
        });
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        graphics.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }
}
