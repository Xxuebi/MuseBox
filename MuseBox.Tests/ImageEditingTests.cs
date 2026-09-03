using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Size = System.Windows.Size;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static BoardItem AddEditableImage(BoardWindow window)
    {
        // Isolate the UI task pump from the production Loaded handler, which also
        // loads the user's global settings and restores a saved viewport.
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(BoardWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        using var bitmap = new Bitmap(320, 200);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(255, 107, 167, 180));
            using var brush = new System.Drawing.SolidBrush(Color.FromArgb(255, 237, 208, 148));
            graphics.FillEllipse(brush, 90, 30, 145, 145);
        }
        var imports = (BoardImportService)typeof(BoardWindow).GetField("_importService", PrivateInstance)!.GetValue(window)!;
        var result = imports.ImportBitmapAsync("A", bitmap).GetAwaiter().GetResult().Single();
        window.ReloadAsync().GetAwaiter().GetResult();
        var item = LiveImages(window).Single(x => x.Id == result.Id);
        item.X = 220; item.Y = 190;
        CallDrawing(window, "UpdateItemVisual", item);
        ((Grid)DrawingBorder(window, item.Id).Child).Children.OfType<Image>().Single().Source =
            (ImageSource)typeof(ImageEditorWindow).GetMethod("ToSource", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { bitmap })!;
        BoardSelection(window).Clear();
        BoardSelection(window).Add(item.Id);
        ArrangeBoardSurface(window);
        CallDrawing(window, "UpdateSelectionVisuals");
        return item;
    }

    private static void NumericInputsAreDiscreet() => WithDrawingBoard((window, _) =>
    {
        var input = (TextBox)window.FindName("DrawingThicknessText");
        var opacity = (TextBox)window.FindName("DrawingOpacityText");
        var eraser = (TextBox)window.FindName("EraserDiameterText");
        Equal("4", input.Text);
        Equal("100", opacity.Text);
        Equal("28", eraser.Text);
        Equal((byte)0, ((SolidColorBrush)input.Background).Color.A);
        Equal((byte)0, ((SolidColorBrush)input.BorderBrush).Color.A);
        True(((StackPanel)input.Parent).Children.OfType<TextBlock>().Any(x => x.Text == "px"), "px 未放在输入框外侧");
        True(((StackPanel)opacity.Parent).Children.OfType<TextBlock>().Any(x => x.Text == "%"), "% 未放在输入框外侧");
        input.Text = "10.8";
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        CallDrawing(window, "OnDrawingNumericMouseDown", input, click);
        Equal(input.Text.Length, input.SelectionLength);
        True(click.Handled, "初次点击重新放置了插入光标");
        CallDrawing(window, "CommitDrawingNumericInput", input);
        Equal(10.8, ((Slider)window.FindName("DrawingThicknessSlider")).Value, .0001);
        input.Text = "NaN";
        CallDrawing(window, "CommitDrawingNumericInput", input);
        Equal("10.8", input.Text);
        var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("DrawingSettingsPopup");
        SaveDrawingTestVisual((FrameworkElement)popup.Child, "drawing-discreet-values.png");
    });

    private static void ImageToolbarSpotlight() => WithDrawingBoard((window, _) =>
    {
        var item = AddEditableImage(window);
        ArrangeBoardSurface(window);
        CallDrawing(window, "UpdateImageToolbar");
        var toggle = (Button)window.FindName("ImageEditToggle");
        var palette = (Border)window.FindName("ImagePalette");
        var shade = (System.Windows.Shapes.Path)window.FindName("ImageFocusShade");
        Equal(Visibility.Visible, toggle.Visibility);
        Equal(Visibility.Collapsed, palette.Visibility);
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(Visibility.Visible, palette.Visibility);
        Equal(Visibility.Visible, shade.Visibility);
        True(!shade.IsHitTestVisible, "聚焦遮罩挡住了画板交互");
        True(!shade.Data.FillContains(new Point(item.X + item.Width / 2, item.Y + item.Height / 2)), "聚焦图片也被压暗");
        True(shade.Data.FillContains(new Point(20, 120)), "图片外没有聚焦遮罩");
        True(Canvas.GetLeft(palette) >= 8 && Canvas.GetTop(palette) >= 8, "图片工具栏越过窗口边缘");
        SaveDrawingTestVisual((Grid)window.FindName("BoardSurface"), "image-toolbar-spotlight.png", false);
        item.Rotation = 35;
        CallDrawing(window, "UpdateResizeHandles");
        True(!shade.Data.FillContains(new Point(item.X + item.Width / 2, item.Y + item.Height / 2)), "旋转后遮罩未随图像更新");
        BoardSelection(window).Clear();
        CallDrawing(window, "UpdateSelectionVisuals");
        Equal(Visibility.Collapsed, shade.Visibility);
        Equal(Visibility.Collapsed, toggle.Visibility);
    });

    private static void ImagePixelEditing()
    {
        using var bitmap = new Bitmap(6, 4);
        bitmap.SetPixel(1, 1, Color.FromArgb(128, 180, 60, 30));
        using var crop = ImageEditService.Crop(bitmap, new Rectangle(1, 1, 3, 2));
        Equal(3, crop.Width); Equal(2, crop.Height);
        Equal(128, (int)crop.GetPixel(0, 0).A);
        using var gray = ImageEditService.Adjust(crop, 0, 1, 0);
        var pixel = gray.GetPixel(0, 0);
        True(Math.Abs(pixel.R - pixel.G) <= 2 && Math.Abs(pixel.G - pixel.B) <= 2, "去饱和度未产生灰色");
        Equal(128, (int)pixel.A);
        using var bright = ImageEditService.Adjust(crop, .1, 1, 1);
        True(bright.GetPixel(0, 0).R > crop.GetPixel(0, 0).R, "亮度调整未生效");
        Equal(128, (int)bitmap.GetPixel(1, 1).A);
    }

    private static void ImageEditHistoryAndAssets() => WithDrawingBoard((window, repository) =>
    {
        var item = AddEditableImage(window);
        var originalId = item.AssetId;
        var originalPath = item.AssetPath;
        using var original = new Bitmap(item.AssetPath);
        using var cropped = ImageEditService.Crop(original, new Rectangle(0, 0, 160, 100));
        AwaitImageUiTask(window, "ReplaceImageBitmapAsync", item, cropped);
        var editedId = item.AssetId;
        True(originalId != editedId, "图片编辑覆盖了原始资源");
        True(File.Exists(originalPath), "原始资源被删除，无法撤回");
        Equal(160d, item.Width); Equal(100d, item.Height);
        Equal(editedId, repository.GetItemsAsync("A").GetAwaiter().GetResult().Single().AssetId);
        AwaitDrawing(window, "UndoAsync");
        Equal(originalId, LiveImages(window).Single().AssetId);
        Equal(320d, LiveImages(window).Single().Width);
        AwaitDrawing(window, "RedoAsync");
        Equal(editedId, LiveImages(window).Single().AssetId);
        Equal(160d, LiveImages(window).Single().Width);
        True(BoardSurfaceEnabled(window), "图片编辑结束后未恢复交互");
    });

    private static bool BoardSurfaceEnabled(BoardWindow window) => ((Grid)window.FindName("BoardSurface")).IsHitTestVisible;

    private static void AwaitImageUiTask(BoardWindow window, string method, params object[] arguments)
    {
        var previous = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            var task = (Task)CallDrawing(window, method, arguments)!;
            if (!task.IsCompleted)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void ImageLinksPersistAndUndo() => WithDrawingBoard((window, repository) =>
    {
        var item = AddEditableImage(window);
        AwaitDrawing(window, "SaveImageLinksAsync", item, "https://example.com/image", "");
        Equal(Visibility.Visible, ((Button)window.FindName("ImageWebLinkButton")).Visibility);
        Equal(Visibility.Collapsed, ((Button)window.FindName("ImageFileLinkButton")).Visibility);
        var target = Path.GetDirectoryName(item.AssetPath)!;
        AwaitDrawing(window, "SaveImageLinksAsync", item, item.WebLink, target);
        Equal(Visibility.Visible, ((Button)window.FindName("ImageFileLinkButton")).Visibility);
        var saved = repository.GetItemsAsync("A").GetAwaiter().GetResult().Single();
        Equal(target, saved.FileLink);
        Equal("https://example.com/image", saved.WebLink);
        AwaitDrawing(window, "UndoAsync");
        Equal("", LiveImages(window).Single().FileLink);
        AwaitDrawing(window, "RedoAsync");
        Equal(target, LiveImages(window).Single().FileLink);
        AwaitDrawing(window, "SaveImageLinksAsync", LiveImages(window).Single(), "", target);
        Equal(Visibility.Collapsed, ((Button)window.FindName("ImageWebLinkButton")).Visibility);
        Equal(Visibility.Visible, ((Button)window.FindName("ImageFileLinkButton")).Visibility);
        True(ImageLinkService.NormalizeWeb("example.com").StartsWith("https://"), "无协议网页未规范化");
        var rejected = false;
        try { ImageLinkService.NormalizeWeb("javascript:alert(1)"); } catch (ArgumentException) { rejected = true; }
        True(rejected, "接受了可执行协议网页链接");
        var dialog = new ImageLinksWindow(saved.WebLink, target);
        try
        {
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(530, 346)); content.Arrange(new Rect(0, 0, 530, 346));
            SaveDrawingTestVisual(content, "image-link-dialog.png", false);
        }
        finally { dialog.Close(); }
    });

    private static void ImageEditorLayoutAndCrop() => WithDrawingBoard((window, _) =>
    {
        var item = AddEditableImage(window);
        var editor = new ImageEditorWindow(item.AssetPath, "Crop");
        try
        {
            var content = (FrameworkElement)editor.Content;
            content.Measure(new Size(920, 650)); content.Arrange(new Rect(0, 0, 920, 650)); content.UpdateLayout();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ImageEditorWindow).GetField("_cropPixels", flags)!.SetValue(editor, new Rect(40, 20, 240, 160));
            typeof(ImageEditorWindow).GetMethod("RenderCropSelection", flags)!.Invoke(editor, null);
            SaveDrawingTestVisual((FrameworkElement)editor.Content, "image-editor-crop.png", false);
            typeof(ImageEditorWindow).GetMethod("ApplyCrop", flags)!.Invoke(editor, null);
            var working = (Bitmap)typeof(ImageEditorWindow).GetField("_working", flags)!.GetValue(editor)!;
            Equal(240, working.Width); Equal(160, working.Height);
            typeof(ImageEditorWindow).GetMethod("OnTransformClick", flags)!.Invoke(editor,
                new object[] { new Button { Tag = "Rotate" }, new RoutedEventArgs() });
            working = (Bitmap)typeof(ImageEditorWindow).GetField("_working", flags)!.GetValue(editor)!;
            Equal(160, working.Width); Equal(240, working.Height);
            ((Slider)editor.FindName("SaturationSlider")).Value = 0;
            typeof(ImageEditorWindow).GetMethod("RefreshPreview", flags)!.Invoke(editor, null);
            True(((Image)editor.FindName("PreviewImage")).Source is not null, "调色预览没有图像");
        }
        finally { editor.Close(); }
    });
}
