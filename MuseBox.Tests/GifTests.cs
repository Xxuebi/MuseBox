using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Color = System.Drawing.Color;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using ListBox = System.Windows.Controls.ListBox;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static string WriteTestGif(string directory)
    {
        var path = Path.Combine(directory, "animation-disposal-test.gif");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
        writer.Write((ushort)4); writer.Write((ushort)3);
        writer.Write(new byte[] { 0xF1, 0, 0, 0,0,0, 255,0,0, 0,255,0, 0,0,255 });
        writer.Write(new byte[] { 0x21,0xFF,11 });
        writer.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        writer.Write(new byte[] { 3,1,0,0,0 });
        WriteGifFrame(writer, 0, 0, 4, 3, Enumerable.Repeat((byte)1, 12).ToArray(), 1, 10);
        WriteGifFrame(writer, 1, 0, 1, 1, new byte[] { 2 }, 2, 20);
        WriteGifFrame(writer, 2, 1, 1, 1, new byte[] { 3 }, 3, 5);
        WriteGifFrame(writer, 0, 2, 1, 1, new byte[] { 2 }, 1, 10);
        writer.Write((byte)0x3B);
        return path;
    }
    private static void WriteGifFrame(BinaryWriter writer, int left, int top, int width, int height,
        byte[] pixels, int disposal, int delay)
    {
        writer.Write(new byte[] { 0x21,0xF9,4,(byte)((disposal << 2) | 1),(byte)delay,0,0,0,0x2C });
        writer.Write((ushort)left); writer.Write((ushort)top); writer.Write((ushort)width); writer.Write((ushort)height);
        writer.Write((byte)0);
        // Reset the tiny LZW dictionary per pixel: every code stays three bits.
        var codes = pixels.SelectMany(pixel => new[] { 4, (int)pixel }).Append(5).ToArray();
        var packed = new byte[(codes.Length * 3 + 7) / 8];
        for (var code = 0; code < codes.Length; code++)
        for (var bit = 0; bit < 3; bit++)
            if ((codes[code] & (1 << bit)) != 0) packed[(code * 3 + bit) / 8] |= (byte)(1 << ((code * 3 + bit) % 8));
        writer.Write((byte)2);
        for (var offset = 0; offset < packed.Length; offset += 255)
        {
            var length = Math.Min(255, packed.Length - offset);
            writer.Write((byte)length); writer.Write(packed, offset, length);
        }
        writer.Write((byte)0);
    }
    private static Color GifPixel(BitmapSource image, int x, int y)
    {
        var bytes = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    private static void GifCompositionAndExtraction()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = WriteTestGif(directory);
            var data = GifAnimationService.LoadAsync(path).GetAwaiter().GetResult();
            Equal(4, data.PixelWidth); Equal(3, data.PixelHeight); Equal(4, data.Frames.Count);
            Equal(100, data.Frames[0].DelayMilliseconds);
            Equal(200, data.Frames[1].DelayMilliseconds);
            Equal(50, data.Frames[2].DelayMilliseconds);
            Equal(Color.Red.ToArgb(), GifPixel(data.Frames[0].Image, 1, 0).ToArgb());
            Equal(Color.Lime.ToArgb(), GifPixel(data.Frames[1].Image, 1, 0).ToArgb());
            Equal(0, (int)GifPixel(data.Frames[2].Image, 1, 0).A);
            Equal(Color.Blue.ToArgb(), GifPixel(data.Frames[2].Image, 2, 1).ToArgb());
            Equal(Color.Red.ToArgb(), GifPixel(data.Frames[3].Image, 2, 1).ToArgb());
            Equal(Color.Lime.ToArgb(), GifPixel(data.Frames[3].Image, 0, 2).ToArgb());
            True(data.Frames.All(frame => frame.Image.IsFrozen), "GIF 帧未冻结，不能安全跨线程");
            using var extracted = GifAnimationService.ExtractFrame(path, 2);
            Equal(4, extracted.Width); Equal(3, extracted.Height);
            Equal(0, (int)extracted.GetPixel(1, 0).A);
            Equal(Color.Blue.ToArgb(), extracted.GetPixel(2, 1).ToArgb());
            var fileData = new DataObject(DataFormats.FileDrop, new[] { path });
            var paths = (IReadOnlyList<string>)typeof(ClipboardImageService).GetMethod("GetImageFilePaths", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { fileData })!;
            Equal(path, paths.Single());
            var widePath = Path.Combine(directory, "wide.gif");
            using (var writer = new BinaryWriter(File.Create(widePath)))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
                writer.Write((ushort)2048); writer.Write((ushort)8);
                writer.Write(new byte[] { 0xF1,0,0, 0,0,0, 255,0,0, 0,255,0, 0,0,255 });
                WriteGifFrame(writer, 0, 0, 2048, 8, Enumerable.Repeat((byte)1, 2048 * 8).ToArray(), 1, 10);
                WriteGifFrame(writer, 1000, 0, 12, 8, Enumerable.Repeat((byte)2, 12 * 8).ToArray(), 1, 10);
                writer.Write((byte)0x3B);
            }
            var wide = GifAnimationService.LoadAsync(widePath).GetAwaiter().GetResult();
            Equal(1000, wide.Frames[0].Image.PixelWidth);
            using var nativeFrame = GifAnimationService.ExtractFrame(widePath, 1);
            Equal(2048, nativeFrame.Width);
            Equal(Color.Lime.ToArgb(), nativeFrame.GetPixel(1005, 3).ToArgb());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void GifPlaybackTiming()
    {
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0,0,255,255 }, 4);
        source.Freeze();
        var state = new GifPlaybackState(new GifAnimation(1, 1, new[]
        {
            new GifFrame(0, source, 100), new GifFrame(1, source, 200), new GifFrame(2, source, 50)
        }));
        True(!state.Advance(99), "未到帧时长就切帧");
        True(state.Advance(1), "未按首帧时长切帧");
        Equal(1, state.FrameIndex);
        state.SetPlaying(false); state.Advance(1000);
        Equal(1, state.FrameIndex);
        state.Step(1); Equal(2, state.FrameIndex); True(!state.IsPlaying, "逐帧未暂停");
        state.Step(1); Equal(0, state.FrameIndex);
        state.Step(-1); Equal(2, state.FrameIndex);
        state.Seek(0); state.SetSpeed(2); state.SetPlaying(true); state.Advance(50);
        Equal(1, state.FrameIndex);
        state.SetSpeed(double.NaN); Equal(2d, state.Speed);
        state.Seek(0); state.SetSpeed(.5); state.SetPlaying(true); state.Advance(199);
        Equal(0, state.FrameIndex); state.Advance(1); Equal(1, state.FrameIndex);
    }

    private static BoardItem AddAnimatedImage(BoardWindow window, BoardRepository repository)
    {
        var original = AddEditableImage(window);
        var path = WriteTestGif(Path.GetDirectoryName(original.AssetPath)!);
        var imports = (BoardImportService)typeof(BoardWindow).GetField("_importService", PrivateInstance)!.GetValue(window)!;
        var imported = imports.ImportFilesAsync("A", new[] { path }).GetAwaiter().GetResult().Single();
        repository.DeleteItemsAsync(new[] { original.Id }).GetAwaiter().GetResult();
        AwaitImageUiTask(window, "ReloadAsync");
        var item = LiveImages(window).Single(x => x.Id == imported.Id);
        item.X = 180; item.Y = 190; item.Width = 240; item.Height = 180;
        repository.UpdateItemsAsync(new[] { item }).GetAwaiter().GetResult();
        CallDrawing(window, "UpdateItemVisual", item);
        var image = ((Grid)DrawingBorder(window, item.Id).Child).Children.OfType<Image>().Single();
        AwaitImageUiTask(window, "AttachGifAsync", item, image);
        BoardSelection(window).Clear(); BoardSelection(window).Add(item.Id);
        ArrangeBoardSurface(window);
        CallDrawing(window, "UpdateSelectionVisuals");
        return item;
    }

    private static void GifBoardToolbarAndFrames() => WithDrawingBoard((window, repository) =>
    {
        var item = AddAnimatedImage(window, repository);
        True(item.AssetPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase), "置入GIF变成静态资源");
        var state = (GifPlaybackState)CallDrawing(window, "SelectedGif")!;
        Equal(4, state.Animation.Frames.Count);
        Equal(Visibility.Visible, ((System.Windows.Shapes.Path)window.FindName("GifEditMotionIcon")).Visibility);
        Equal(Visibility.Collapsed, ((System.Windows.Shapes.Path)window.FindName("ImageEditPencilIcon")).Visibility);
        Equal("打开动图工具栏", ((Button)window.FindName("ImageEditToggle")).ToolTip.ToString()!);
        SaveDrawingTestVisual((Grid)window.FindName("BoardSurface"), "gif-motion-entry.png", false);
        ((Button)window.FindName("ImageEditToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(Visibility.Visible, ((StackPanel)window.FindName("GifImageTools")).Visibility);
        Equal(Visibility.Collapsed, ((StackPanel)window.FindName("StaticImageTools")).Visibility);
        var playback = (Button)window.FindName("GifPlaybackButton");
        True(window.FindName("GifPlayButton") is null && window.FindName("GifPauseButton") is null, "仍有两个独立播放/暂停按钮");
        Equal("暂停", playback.ToolTip.ToString()!);
        Equal(Visibility.Visible, ((System.Windows.Shapes.Path)window.FindName("GifPauseIcon")).Visibility);
        playback.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(!state.IsPlaying, "暂停按钮无效");
        Equal("播放", playback.ToolTip.ToString()!);
        Equal(Visibility.Visible, ((System.Windows.Shapes.Path)window.FindName("GifPlayIcon")).Visibility);
        Equal(Visibility.Collapsed, ((System.Windows.Shapes.Path)window.FindName("GifPauseIcon")).Visibility);
        playback.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(state.IsPlaying, "同一按钮没有恢复播放");
        Equal("暂停", System.Windows.Automation.AutomationProperties.GetName(playback));
        ((Button)window.FindName("GifNextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(1, state.FrameIndex);
        Equal("播放", playback.ToolTip.ToString()!);
        CallDrawing(window, "OnGifSpeedSelected", new Button { Tag = "2" }, new RoutedEventArgs());
        Equal(2d, state.Speed);
        ((Button)window.FindName("GifFramesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var list = (ListBox)window.FindName("GifFramesList");
        Equal(4, list.Items.Count);
        Equal("第 4 帧", ((GifFrame)list.Items[3]).Label);
        list.SelectedIndex = 3;
        Equal(3, state.FrameIndex);
        True(!state.IsPlaying, "选择帧后仍继续播放");
        var visual = ((Grid)DrawingBorder(window, item.Id).Child).Children.OfType<Image>().Single();
        True(ReferenceEquals(state.Animation.Frames[3].Image, visual.Source), "选择帧后主图未跳转");
        CallDrawing(window, "ConfigureGifContextMenu", DrawingBorder(window, item.Id));
        Equal(Visibility.Visible, ((MenuItem)window.FindName("SaveGifFrameMenuItem")).Visibility);
        CallDrawing(window, "ConfigureGifContextMenu", window.FindName("BoardSurface"));
        Equal(Visibility.Collapsed, ((MenuItem)window.FindName("SaveGifFrameMenuItem")).Visibility);
        var palette = (Border)window.FindName("ImagePalette");
        True(palette.RenderTransform is TransformGroup transforms &&
            transforms.Children.OfType<ScaleTransform>().Any(scale => scale.ScaleX is > 0 and < 1),
            "按钮展开工具栏没有初始化缩放过渡");
        palette.RenderTransform = Transform.Identity; palette.BeginAnimation(UIElement.OpacityProperty, null);
        SaveDrawingTestVisual((Grid)window.FindName("BoardSurface"), "gif-toolbar-frames.png", false);
        AwaitImageUiTask(window, "ReloadAsync");
        True(ReferenceEquals(state, CallDrawing(window, "SelectedGif")), "画板重载丢失GIF播放状态");
        Equal(3, state.FrameIndex);
        state.SetPlaying(true);
        CallDrawing(window, "ConfigureGifContextMenu", DrawingBorder(window, item.Id));
        Equal(Visibility.Collapsed, ((MenuItem)window.FindName("SaveGifFrameMenuItem")).Visibility);
        state.SetPlaying(false);
        BoardSelection(window).Clear(); CallDrawing(window, "UpdateSelectionVisuals");
        Equal(Visibility.Collapsed, ((Border)window.FindName("GifFramesPanel")).Visibility);
        AddEditableImage(window);
        Equal(Visibility.Collapsed, ((System.Windows.Shapes.Path)window.FindName("GifEditMotionIcon")).Visibility);
        Equal(Visibility.Visible, ((System.Windows.Shapes.Path)window.FindName("ImageEditPencilIcon")).Visibility);
        Equal("打开图片工具栏", ((Button)window.FindName("ImageEditToggle")).ToolTip.ToString()!);
    });

    private static void GifFrameSaveAndUndo() => WithDrawingBoard((window, repository) =>
    {
        var item = AddAnimatedImage(window, repository);
        var originalAsset = item.AssetId;
        var originalPath = item.AssetPath;
        ((GifPlaybackState)CallDrawing(window, "SelectedGif")!).Seek(2);
        AwaitImageUiTask(window, "SaveGifFrameAsync", item, 2);
        Equal(2, LiveImages(window).Count);
        Equal(originalAsset, item.AssetId); Equal(originalPath, item.AssetPath);
        var frame = LiveImages(window).Single(image => image.Id != item.Id);
        True(frame.AssetPath.EndsWith(".png"), "另存帧未生成静态图片");
        using (var bitmap = new System.Drawing.Bitmap(frame.AssetPath))
        {
            Equal(4, bitmap.Width); Equal(3, bitmap.Height);
            Equal(Color.Blue.ToArgb(), bitmap.GetPixel(2, 1).ToArgb());
        }
        AwaitImageUiTask(window, "UndoAsync");
        Equal(1, LiveImages(window).Count); Equal(originalAsset, LiveImages(window).Single().AssetId);
        AwaitImageUiTask(window, "RedoAsync");
        Equal(2, LiveImages(window).Count);
    });

    private static void ImageEditorPolishAndLinks() => WithDrawingBoard((window, _) =>
    {
        var item = AddEditableImage(window);
        var editor = new ImageEditorWindow(item.AssetPath);
        var links = new ImageLinksWindow("https://example.com", @"C:\images");
        try
        {
            var content = (FrameworkElement)editor.Content;
            content.Measure(new Size(920, 650)); content.Arrange(new Rect(0, 0, 920, 650)); content.UpdateLayout();
            foreach (var button in ((StackPanel)editor.FindName("EditorTools")).Children.OfType<Button>())
            {
                var icon = (System.Windows.Shapes.Path)button.Content;
                var bounds = icon.TransformToAncestor(button).TransformBounds(new Rect(icon.RenderSize));
                True(bounds.Top >= 1 && bounds.Bottom <= button.ActualHeight - 1, "工具按钮图标被裁切");
            }
            var input = (TextBox)editor.FindName("HueValue");
            input.Text = "-53.7";
            True(!(bool)EditorCall(editor, "DismissNumberEditor", input, input)!, "点数字内部却结束输入");
            True((bool)EditorCall(editor, "DismissNumberEditor", input, editor.FindName("PreviewHost"))!, "点外部未结束输入");
            Equal(-53.7, ((Slider)editor.FindName("HueSlider")).Value, .001);
            SaveDrawingTestVisual(content, "image-editor-icons-fixed.png", false);
            ((Button)links.FindName("ClearWebLinkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal("", ((TextBox)links.FindName("WebLinkInput")).Text);
            Equal(@"C:\images", ((TextBox)links.FindName("FileLinkInput")).Text);
            ((Button)links.FindName("ClearFileLinkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal("", ((TextBox)links.FindName("FileLinkInput")).Text);
            Equal(Visibility.Collapsed, ((TextBlock)links.FindName("LinkStatus")).Visibility);
            ((TextBox)links.FindName("WebLinkInput")).Text = "https://example.com/reference";
            var linkContent = (FrameworkElement)links.Content;
            linkContent.Measure(new Size(530, 346)); linkContent.Arrange(new Rect(0, 0, 530, 346)); linkContent.UpdateLayout();
            SaveDrawingTestVisual(linkContent, "image-links-clear-buttons.png", false);
            var tools = (StackPanel)window.FindName("StaticImageTools");
            True(tools.Children.OfType<Button>().All(button => button.Tag?.ToString() is not ("Crop" or "Color")), "静态工具栏仍有裁剪和颜色入口");
            True(tools.Children.OfType<Button>().Any(button => button.Tag?.ToString() == "Edit"), "图片编辑按钮被移除");
        }
        finally { editor.Close(); links.Close(); }
    });
}
