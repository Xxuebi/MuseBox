using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenshotCollector.Services;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static object? EditorCall(ImageEditorWindow editor, string method, params object[] args) =>
        typeof(ImageEditorWindow).GetMethod(method, PrivateInstance)!.Invoke(editor, args);
    private static T EditorField<T>(ImageEditorWindow editor, string field) =>
        (T)typeof(ImageEditorWindow).GetField(field, PrivateInstance)!.GetValue(editor)!;

    private static void ImageHueAndRawPreview()
    {
        using var source = new Bitmap(5, 3);
        source.SetPixel(1, 1, Color.FromArgb(128, 220, 30, 20));
        using var hue = ImageEditService.Adjust(source, 0, 1, 1, 120);
        True(hue.GetPixel(1, 1).G > hue.GetPixel(1, 1).R, "色相未将红色转向绿色");
        Equal(128, (int)hue.GetPixel(1, 1).A);
        using var gray = ImageEditService.Adjust(source, 0, 1, 0, 80);
        var pixel = gray.GetPixel(1, 1);
        True(Math.Abs(pixel.R - pixel.G) <= 2 && Math.Abs(pixel.G - pixel.B) <= 2, "零饱和度的色相变化染色了灰度");
        var bitmapSource = (System.Windows.Media.Imaging.BitmapSource)typeof(ImageEditorWindow)
            .GetMethod("ToSource", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { source })!;
        var bytes = new byte[5 * 3 * 4];
        bitmapSource.CopyPixels(bytes, 5 * 4, 0);
        Equal((byte)128, bytes[(5 + 1) * 4 + 3]);
        Equal((byte)220, bytes[(5 + 1) * 4 + 2]);
        True(bitmapSource.IsFrozen, "原始像素预览未冻结");
    }

    private static void ImageEditorAdjustmentUndo() => WithDrawingBoard((window, _) =>
    {
        var item = AddEditableImage(window);
        var editor = new ImageEditorWindow(item.AssetPath);
        var previousAppearance = ThemeService.CurrentMode;
        try
        {
            var hue = (Slider)editor.FindName("HueSlider");
            var input = (TextBox)editor.FindName("HueValue");
            ThemeService.Apply(System.Windows.Application.Current, ScreenshotCollector.Models.AppAppearanceMode.Dark);
            editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Equal(((SolidColorBrush)System.Windows.Application.Current.FindResource("TextBrush")).Color,
                ((SolidColorBrush)input.Foreground).Color);
            True(hue.Background is LinearGradientBrush gradient && gradient.GradientStops.Count >= 6, "色相未使用彩虹色条");
            input.Text = "42";
            EditorCall(editor, "CommitNumber", input);
            Equal(42d, hue.Value);
            EditorCall(editor, "UndoEdit");
            Equal(0d, hue.Value);
            input.Text = "NaN";
            EditorCall(editor, "CommitNumber", input);
            Equal("0", input.Text);
            input.Text = "999";
            EditorCall(editor, "CommitNumber", input);
            Equal(180d, hue.Value);
            EditorCall(editor, "UndoEdit");
            var preview = ((Image)editor.FindName("PreviewImage")).Source;
            var cached = EditorField<Bitmap>(editor, "_previewBase");
            EditorCall(editor, "OnAdjustmentStart", hue, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            for (var i = 1; i <= 25; i++)
            {
                hue.Value = i * 3;
                EditorCall(editor, "RefreshPreview");
            }
            EditorCall(editor, "EndAdjustment");
            True(ReferenceEquals(preview, ((Image)editor.FindName("PreviewImage")).Source), "每帧重建了预览图源");
            True(ReferenceEquals(cached, EditorField<Bitmap>(editor, "_previewBase")), "每帧重新缩放原始图片");
            Equal(1, EditorField<HashSet<Bitmap>>(editor, "_ownedPixels").Count);
            EditorCall(editor, "UndoEdit");
            Equal(0d, hue.Value);
            True(!((Button)editor.FindName("UndoEditorButton")).IsEnabled, "一次拖动产生了多步撤回");
            var content = (FrameworkElement)editor.Content;
            content.Measure(new Size(920, 650)); content.Arrange(new Rect(0, 0, 920, 650)); content.UpdateLayout();
            SaveDrawingTestVisual(content, "image-editor-colors.png", false);
        }
        finally
        {
            ThemeService.Apply(System.Windows.Application.Current, previousAppearance);
            editor.Close();
        }
    });

    private static void ImageEditorStructuralUndo() => WithDrawingBoard((window, _) =>
    {
        var item = AddEditableImage(window);
        var editor = new ImageEditorWindow(item.AssetPath, "Crop");
        try
        {
            var original = EditorField<Bitmap>(editor, "_working");
            EditorCall(editor, "OnTransformClick", new Button { Tag = "RotateLeft" }, new RoutedEventArgs());
            Equal(200, EditorField<Bitmap>(editor, "_working").Width);
            Equal(320, original.Width);
            EditorCall(editor, "OnTransformClick", new Button { Tag = "Rotate" }, new RoutedEventArgs());
            Equal(320, EditorField<Bitmap>(editor, "_working").Width);
            EditorCall(editor, "UndoEdit");
            Equal(200, EditorField<Bitmap>(editor, "_working").Width);
            EditorCall(editor, "UndoEdit");
            True(ReferenceEquals(original, EditorField<Bitmap>(editor, "_working")), "撤回旋转未恢复原始像素版本");
            var content = (FrameworkElement)editor.Content;
            content.Measure(new Size(920, 650)); content.Arrange(new Rect(0, 0, 920, 650)); content.UpdateLayout();
            typeof(ImageEditorWindow).GetField("_cropPixels", PrivateInstance)!.SetValue(editor, new Rect(30, 20, 200, 140));
            EditorCall(editor, "RenderCropSelection");
            var actions = (Border)editor.FindName("CropActions");
            var selection = (System.Windows.Shapes.Rectangle)editor.FindName("CropRectangle");
            Equal(Visibility.Visible, actions.Visibility);
            Equal(Canvas.GetLeft(selection) + selection.Width, Canvas.GetLeft(actions) + actions.DesiredSize.Width, 1);
            EditorCall(editor, "ApplyCrop");
            Equal(200, EditorField<Bitmap>(editor, "_working").Width);
            Equal(Visibility.Collapsed, actions.Visibility);
            EditorCall(editor, "UndoEdit");
            Equal(320, EditorField<Bitmap>(editor, "_working").Width);
            ((Slider)editor.FindName("BrightnessSlider")).Value = 20;
            EditorCall(editor, "OnResetClick", new Button(), new RoutedEventArgs());
            Equal(0d, ((Slider)editor.FindName("BrightnessSlider")).Value);
            EditorCall(editor, "UndoEdit");
            Equal(20d, ((Slider)editor.FindName("BrightnessSlider")).Value);
            True(((StackPanel)editor.FindName("EditorTools")).Children.OfType<Button>()
                .All(x => x.Content is System.Windows.Shapes.Path), "编辑器操作未统一为图标");
        }
        finally { editor.Close(); }
    });

    private static void ImageSaveAsPreservesOriginal() => WithDrawingBoard((window, repository) =>
    {
        var original = AddEditableImage(window);
        var id = original.Id;
        var asset = original.AssetId;
        var path = original.AssetPath;
        var x = original.X; var y = original.Y;
        using var bitmap = new Bitmap(path);
        using var crop = ImageEditService.Crop(bitmap, new Rectangle(10, 10, 150, 100));
        AwaitImageUiTask(window, "AddEditedImageAsync", original, crop);
        Equal(2, LiveImages(window).Count);
        Equal(asset, original.AssetId); Equal(path, original.AssetPath);
        Equal(x, original.X); Equal(y, original.Y);
        Equal(320d, original.Width); Equal(200d, original.Height);
        var added = LiveImages(window).Single(item => item.Id != id);
        True(added.AssetId != asset && File.Exists(path), "另存覆盖了原始资源");
        Equal(150d, added.Width); Equal(100d, added.Height);
        Equal(2, repository.GetItemsAsync("A").GetAwaiter().GetResult().Count);
        True(BoardSelection(window).Contains(added.Id), "另存后未选择新图片");
        AwaitDrawing(window, "UndoAsync");
        Equal(1, LiveImages(window).Count);
        Equal(asset, LiveImages(window).Single().AssetId);
        AwaitDrawing(window, "RedoAsync");
        Equal(2, LiveImages(window).Count);
    });

    private static void ImageEditorLargePreview() => WithDrawingBoard((window, _) =>
    {
        var item = AddEditableImage(window);
        var path = Path.Combine(Path.GetDirectoryName(item.AssetPath)!, "large-preview-test.png");
        using (var bitmap = new Bitmap(4000, 3000))
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(255, 180, 70, 45));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        var editor = new ImageEditorWindow(path);
        try
        {
            var hue = (Slider)editor.FindName("HueSlider");
            var source = ((Image)editor.FindName("PreviewImage")).Source;
            Equal(1000, EditorField<Bitmap>(editor, "_previewBase").Width);
            EditorCall(editor, "OnAdjustmentStart", hue, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 1; i <= 30; i++)
            {
                hue.Value = i * 4;
                EditorCall(editor, "RefreshPreview");
            }
            watch.Stop();
            EditorCall(editor, "EndAdjustment");
            Console.WriteLine($"INFO  4000×3000 图片，1000×750 预览：{watch.Elapsed.TotalMilliseconds / 30:0.0} ms/帧（30帧均值，不含显示合成）");
            True(ReferenceEquals(source, ((Image)editor.FindName("PreviewImage")).Source), "大图预览图源被重复分配");
            True((bool)EditorCall(editor, "PrepareResult", true)!, "另存准备失败");
            True(editor.SaveAsNewImage, "另存未标记新增图片");
            Equal(4000, editor.ResultBitmap!.Width); Equal(3000, editor.ResultBitmap.Height);
            True(editor.ResultBitmap.GetPixel(50, 50).G > editor.ResultBitmap.GetPixel(50, 50).R, "最终输出未应用色相");
            Equal(4000, EditorField<Bitmap>(editor, "_original").Width);
            typeof(ImageEditorWindow).GetField("_cropPixels", PrivateInstance)!.SetValue(editor, new Rect(10, 10, 500, 500));
            True(!(bool)EditorCall(editor, "PrepareResult", false)!, "未确定的裁切被自动应用");
            EditorCall(editor, "OnCropCancelClick", new Button(), new RoutedEventArgs());
            EditorCall(editor, "UndoEdit");
            Equal(0d, hue.Value);
            True((bool)EditorCall(editor, "PrepareResult", false)!, "应用准备失败");
            True(!editor.SaveAsNewImage, "应用被错误标记为另存");
        }
        finally { editor.ResultBitmap?.Dispose(); editor.Close(); }
    });
}
