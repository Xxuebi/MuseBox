using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitmap = System.Drawing.Bitmap;

namespace ScreenshotCollector.Services;

public sealed record GifFrame(int Index, BitmapSource Image, int DelayMilliseconds)
{
    public string Label => $"第 {Index + 1} 帧";
}

public sealed record GifAnimation(int PixelWidth, int PixelHeight, IReadOnlyList<GifFrame> Frames);

public static class GifAnimationService
{
    public static bool IsGif(string path) => ImageFileFormatService.FromFile(path) == ".gif";

    public static Task<GifAnimation> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Decode(path, null, cancellationToken), cancellationToken);

    public static Bitmap ExtractFrame(string path, int index)
    {
        var source = Decode(path, index, CancellationToken.None).Frames[0].Image;
        var result = new Bitmap(source.PixelWidth, source.PixelHeight, PixelFormat.Format32bppArgb);
        var data = result.LockBits(new Rectangle(0, 0, result.Width, result.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { source.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * result.Height, data.Stride); }
        finally { result.UnlockBits(data); }
        return result;
    }

    private static int Number(BitmapMetadata? metadata, string query, int fallback = 0)
    {
        try { return metadata?.GetQuery(query) is { } value ? Convert.ToInt32(value) : fallback; }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        { return fallback; }
    }

    private static GifAnimation Decode(string path, int? onlyFrame, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count is 0 or > 5000) throw new InvalidDataException("GIF 帧数超出支持范围（1–5000 帧）。");
        if (onlyFrame is { } requested && (requested < 0 || requested >= decoder.Frames.Count))
            throw new ArgumentOutOfRangeException(nameof(onlyFrame));
        var metadata = decoder.Metadata as BitmapMetadata;
        var width = Number(metadata, "/logscrdesc/Width", decoder.Frames[0].PixelWidth);
        var height = Number(metadata, "/logscrdesc/Height", decoder.Frames[0].PixelHeight);
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000)
            throw new InvalidDataException("GIF 画布尺寸过大。");
        // Bound the composed preview cache to 64 MiB per unique animation.
        // Frame extraction replays at native resolution, never exporting the preview.
        var scale = onlyFrame.HasValue ? 1 : Math.Min(1, Math.Min(1000d / Math.Max(width, height),
            Math.Sqrt(64d * 1024 * 1024 / (4d * width * height * decoder.Frames.Count))));
        var previewWidth = Math.Max(1, (int)(width * scale));
        var previewHeight = Math.Max(1, (int)(height * scale));
        var stride = previewWidth * 4;
        var canvas = new byte[checked(stride * previewHeight)];
        var background = Colors.Transparent;
        var transparent = decoder.Frames.Any(frame => Number(frame.Metadata as BitmapMetadata, "/grctlext/TransparencyFlag") != 0);
        var backgroundIndex = Number(metadata, "/logscrdesc/BackgroundColorIndex");
        if (!transparent && decoder.Palette is { } palette && backgroundIndex < palette.Colors.Count)
            background = palette.Colors[backgroundIndex];
        Fill(canvas, stride, new Int32Rect(0, 0, previewWidth, previewHeight), background);
        var frames = new List<GifFrame>();
        var last = onlyFrame ?? decoder.Frames.Count - 1;
        for (var index = 0; index <= last; index++)
        {
            token.ThrowIfCancellationRequested();
            var raw = decoder.Frames[index];
            var frameMetadata = raw.Metadata as BitmapMetadata;
            var left = Number(frameMetadata, "/imgdesc/Left");
            var top = Number(frameMetadata, "/imgdesc/Top");
            var x = Math.Clamp((int)Math.Round(left * previewWidth / (double)width), 0, previewWidth);
            var y = Math.Clamp((int)Math.Round(top * previewHeight / (double)height), 0, previewHeight);
            var right = Math.Clamp((int)Math.Round((left + raw.PixelWidth) * previewWidth / (double)width), x, previewWidth);
            var bottom = Math.Clamp((int)Math.Round((top + raw.PixelHeight) * previewHeight / (double)height), y, previewHeight);
            var area = new Int32Rect(x, y, right - x, bottom - y);
            var disposal = Number(frameMetadata, "/grctlext/Disposal");
            var saved = disposal == 3 ? (byte[])canvas.Clone() : null;
            if (area.Width > 0 && area.Height > 0)
            {
                BitmapSource pixels = new FormatConvertedBitmap(raw, PixelFormats.Bgra32, null, 0);
                if (area.Width != raw.PixelWidth || area.Height != raw.PixelHeight)
                    pixels = new TransformedBitmap(pixels, new ScaleTransform(area.Width / (double)raw.PixelWidth, area.Height / (double)raw.PixelHeight));
                var rawStride = area.Width * 4;
                var bytes = new byte[checked(rawStride * area.Height)];
                pixels.CopyPixels(new Int32Rect(0, 0, area.Width, area.Height), bytes, rawStride, 0);
                Composite(canvas, stride, bytes, rawStride, area);
            }
            if (!onlyFrame.HasValue || index == onlyFrame)
            {
                var source = BitmapSource.Create(previewWidth, previewHeight, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
                source.Freeze();
                var delay = Number(frameMetadata, "/grctlext/Delay", 10) * 10;
                frames.Add(new GifFrame(index, source, delay <= 10 ? 100 : delay));
            }
            // GIF frame rectangles can be partial; honor restore-background and
            // restore-previous disposal before compositing the next frame.
            if (disposal == 2) Fill(canvas, stride, area, background);
            else if (saved is not null) canvas = saved;
        }
        return new GifAnimation(width, height, frames);
    }

    private static void Fill(byte[] canvas, int stride, Int32Rect area, Color color)
    {
        for (var y = area.Y; y < area.Y + area.Height; y++)
        for (var x = area.X; x < area.X + area.Width; x++)
        {
            var offset = y * stride + x * 4;
            canvas[offset] = color.B; canvas[offset + 1] = color.G;
            canvas[offset + 2] = color.R; canvas[offset + 3] = color.A;
        }
    }

    private static void Composite(byte[] canvas, int stride, byte[] source, int sourceStride, Int32Rect area)
    {
        for (var y = 0; y < area.Height; y++)
        for (var x = 0; x < area.Width; x++)
        {
            var sourceOffset = y * sourceStride + x * 4;
            var targetOffset = (y + area.Y) * stride + (x + area.X) * 4;
            var alpha = source[sourceOffset + 3];
            if (alpha == 0) continue;
            if (alpha == 255)
            {
                canvas[targetOffset] = source[sourceOffset];
                canvas[targetOffset + 1] = source[sourceOffset + 1];
                canvas[targetOffset + 2] = source[sourceOffset + 2];
                canvas[targetOffset + 3] = 255;
                continue;
            }
            var oldAlpha = canvas[targetOffset + 3] / 255d;
            var blend = alpha / 255d;
            var resultAlpha = blend + oldAlpha * (1 - blend);
            for (var channel = 0; channel < 3; channel++)
                canvas[targetOffset + channel] = (byte)Math.Round((source[sourceOffset + channel] * blend +
                    canvas[targetOffset + channel] * oldAlpha * (1 - blend)) / resultAlpha);
            canvas[targetOffset + 3] = (byte)Math.Round(resultAlpha * 255);
        }
    }
}

public sealed class GifPlaybackState
{
    private double _elapsed;
    private readonly double _cycleMilliseconds;
    public GifAnimation Animation { get; }
    public int FrameIndex { get; private set; }
    public bool IsPlaying { get; private set; } = true;
    public double Speed { get; private set; } = 1;
    public GifPlaybackState(GifAnimation animation)
    {
        Animation = animation;
        _cycleMilliseconds = animation.Frames.Sum(frame => (double)frame.DelayMilliseconds);
    }
    public void SetPlaying(bool playing) { IsPlaying = playing; _elapsed = 0; }
    public void SetSpeed(double speed)
    {
        if (!double.IsFinite(speed)) return;
        Speed = Math.Clamp(speed, .25, 4);
    }
    public void Seek(int index)
    {
        FrameIndex = Math.Clamp(index, 0, Animation.Frames.Count - 1);
        SetPlaying(false);
    }
    public void Step(int direction) => Seek((FrameIndex + direction + Animation.Frames.Count) % Animation.Frames.Count);
    public bool Advance(double milliseconds)
    {
        if (!IsPlaying || Animation.Frames.Count < 2 || !double.IsFinite(milliseconds) || milliseconds <= 0) return false;
        var previous = FrameIndex;
        _elapsed += milliseconds * Speed;
        if (_elapsed >= _cycleMilliseconds) _elapsed %= _cycleMilliseconds;
        while (_elapsed >= Animation.Frames[FrameIndex].DelayMilliseconds)
        {
            _elapsed -= Animation.Frames[FrameIndex].DelayMilliseconds;
            FrameIndex = (FrameIndex + 1) % Animation.Frames.Count;
        }
        return FrameIndex != previous;
    }
}
