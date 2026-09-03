using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Button = System.Windows.Controls.Button;
using Size = System.Windows.Size;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static Bitmap CoverTestBitmap()
    {
        var bitmap = new Bitmap(600, 400);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(102, 154, 163));
        using var sun = new System.Drawing.SolidBrush(Color.FromArgb(246, 216, 155));
        using var hill = new System.Drawing.SolidBrush(Color.FromArgb(41, 79, 94));
        using var front = new System.Drawing.SolidBrush(Color.FromArgb(70, 118, 127));
        graphics.FillEllipse(sun, 350, 65, 125, 125);
        graphics.FillPolygon(hill, new[] { new System.Drawing.Point(0, 400), new(165, 135), new(390, 400) });
        graphics.FillPolygon(front, new[] { new System.Drawing.Point(180, 400), new(420, 210), new(600, 360), new(600, 400) });
        return bitmap;
    }

    private static void CoverCropBoundsAndRendering()
    {
        foreach (var dimensions in new[] { (400d, 100d), (100d, 400d), (600d, 400d) })
        foreach (var zoom in new[] { 1d, 2d, 8d, double.NaN })
        foreach (var pan in new[] { -20d, 0, 20d, double.NaN })
        {
            var placed = DrawerCoverRenderer.Place(dimensions.Item1, dimensions.Item2, 500, 360,
                new CoverCropState { Zoom = zoom, PanX = pan, PanY = pan });
            True(placed.Image.Left <= .001 && placed.Image.Top <= .001 &&
                placed.Image.Right >= 499.999 && placed.Image.Bottom >= 359.999, "移动或缩放后裁切框露出空白");
            var resized = DrawerCoverRenderer.Place(dimensions.Item1, dimensions.Item2, 1000, 720, placed.Crop);
            Equal(placed.Image.X * 2, resized.Image.X, .001);
            Equal(placed.Image.Y * 2, resized.Image.Y, .001);
        }
        var pixels = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
        {
            var offset = (y * 8 + x) * 4;
            pixels[offset] = (byte)(x < 4 ? 0 : 200);
            pixels[offset + 1] = (byte)(y < 4 ? 0 : 200);
            pixels[offset + 2] = (byte)(x < 4 ? 200 : 0);
            pixels[offset + 3] = 180;
        }
        var source = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 32);
        source.Freeze();
        foreach (var turn in Enumerable.Range(0, 4))
        foreach (var flipX in new[] { false, true })
        foreach (var flipY in new[] { false, true })
        {
            var state = new CoverCropState { QuarterTurns = turn, FlipX = flipX, FlipY = flipY };
            var oriented = DrawerCoverRenderer.Orient(source, state);
            using var output = DrawerCoverRenderer.Render(oriented, state);
            Equal(1000, output.Width); Equal(720, output.Height);
            Equal(180d, output.GetPixel(250, 180).A, 2);
            // Quarter turns and mirror operations must agree with independent GDI transforms.
            using var reference = new Bitmap(8, 8);
            for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
                reference.SetPixel(x, y, Color.FromArgb(180, x < 4 ? 200 : 0, y < 4 ? 0 : 200, x < 4 ? 0 : 200));
            reference.RotateFlip((System.Drawing.RotateFlipType)turn);
            if (flipX) reference.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipX);
            if (flipY) reference.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);
            var actual = output.GetPixel(250, 180);
            var expected = reference.GetPixel(2, 2);
            Equal((double)expected.R, actual.R, 3); Equal((double)expected.G, actual.G, 3); Equal((double)expected.B, actual.B, 3);
        }
        var original = new byte[pixels.Length]; source.CopyPixels(original, 32, 0);
        True(pixels.SequenceEqual(original), "封面调整修改了原图");
    }

    private static void DrawerCoverPersistence() => WithMainDrawerWindow((window, repository) =>
    {
        var imports = (BoardImportService)typeof(MainWindow).GetField("_importService", PrivateInstance)!.GetValue(window)!;
        using var original = CoverTestBitmap();
        var shared = imports.ImportBitmapAsync("B", original).GetAwaiter().GetResult().Single();
        var crop = new CoverCropState { Zoom = 1.8, PanX = .1, PanY = -.12, QuarterTurns = 1, FlipX = true };
        var source = DrawerCoverRenderer.Orient(DrawerCoverRenderer.Load(shared.AssetPath), crop);
        using var rendered = DrawerCoverRenderer.Render(source, crop);
        var cover = imports.SaveDrawerCoverAsync("A", shared.AssetPath, rendered, crop).GetAwaiter().GetResult();
        MainCall(window, "ApplyDrawerCover", MainDrawers(window)[0], cover);
        var thumbnail = MainDrawers(window)[0].Thumbnail;
        Equal(0, repository.GetItemCountAsync("A").GetAwaiter().GetResult());
        using var added = new Bitmap(40, 25);
        var latest = imports.ImportBitmapAsync("A", added).GetAwaiter().GetResult().Single();
        AwaitMainTask(window, "UpdateThumbnailAsync", "A", latest.AssetPath);
        True(ReferenceEquals(thumbnail, MainDrawers(window)[0].Thumbnail), "后续置入覆盖了固定封面");
        var files = repository.DeleteDrawerAsync("B").GetAwaiter().GetResult();
        True(!files.Contains(cover.SourcePath), "删除其他抽屉会删除仍在使用的封面源图");
        var assets = (string)typeof(BoardRepository).GetField("_assetDirectory", PrivateInstance)!.GetValue(repository)!;
        var reopened = new BoardRepository(new AppDataPaths(Path.GetDirectoryName(assets)!));
        reopened.InitializeAsync().GetAwaiter().GetResult();
        var recovered = reopened.GetDrawersAsync().GetAwaiter().GetResult().Single(x => x.Id == "A").Cover!;
        Equal(cover, recovered);
        AwaitMainTask(window, "ReloadDrawersAsync");
        True(MainDrawers(window)[0].Cover is not null && MainDrawers(window)[0].Thumbnail is not null, "重启未恢复封面");
        var menu = (DrawerMenuPopup)MainCall(window, "CreateDrawerMenu", MainDrawers(window)[0])!;
        Equal(8, menu.Actions.Children.Count);
        Equal("编辑封面", System.Windows.Automation.AutomationProperties.GetName(menu.Actions.Children[5]));
        Equal("移除封面", System.Windows.Automation.AutomationProperties.GetName(menu.Actions.Children[6]));
        var content = ArrangeMain(window, 520, 650);
        SaveDrawingTestVisual(content, "drawer-fixed-cover.png", false);
        AwaitMainTask(window, "ClearDrawerCoverAsync", "A");
        True(MainDrawers(window)[0].Cover is null && MainDrawers(window)[0].Thumbnail is not null, "移除封面未恢复最新图片");
        True(reopened.GetDrawersAsync().GetAwaiter().GetResult().Single(x => x.Id == "A").Cover is null, "移除封面未持久化");
        Equal(1, repository.GetItemCountAsync("A").GetAwaiter().GetResult());
    });

    private static void DrawerCoverEditorLayoutAndRoundTrip() => WithMainDrawerWindow((window, repository) =>
    {
        var imports = (BoardImportService)typeof(MainWindow).GetField("_importService", PrivateInstance)!.GetValue(window)!;
        using var bitmap = CoverTestBitmap();
        var asset = imports.SaveEditedBitmapAsync(bitmap).GetAwaiter().GetResult();
        var editor = new DrawerCoverWindow(asset.FullPath);
        try
        {
            var chrome = (FrameworkElement)editor.Content;
            chrome.Measure(new Size(680, 614)); chrome.Arrange(new Rect(0, 0, 680, 614)); chrome.UpdateLayout();
            var frame = (Border)editor.FindName("CoverFrame");
            Equal(CoverCropState.DrawerAspect, frame.ActualWidth / frame.ActualHeight, .001);
            var before = new Size(frame.ActualWidth, frame.ActualHeight);
            ((Slider)editor.FindName("CoverZoom")).Value = 2;
            ((Button)editor.FindName("CoverRotateRight")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)editor.FindName("CoverFlipX")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            chrome.UpdateLayout();
            Equal(before, new Size(frame.ActualWidth, frame.ActualHeight));
            True(editor.Crop.QuarterTurns == 1 && editor.Crop.FlipX && editor.Crop.Zoom == 2, "封面工具按钮没有生效");
            var loaded = DrawerCoverRenderer.Load(asset.FullPath);
            using var first = DrawerCoverRenderer.Render(DrawerCoverRenderer.Orient(loaded, editor.Crop), editor.Crop);
            var reopened = new DrawerCoverWindow(asset.FullPath, editor.Crop);
            try
            {
                using var second = DrawerCoverRenderer.Render(DrawerCoverRenderer.Orient(loaded, reopened.Crop), reopened.Crop);
                Equal(first.GetPixel(300, 200), second.GetPixel(300, 200));
            }
            finally { reopened.Close(); }
            ((Button)editor.FindName("CoverReset")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(new CoverCropState(), editor.Crop);
            chrome.RenderTransform = Transform.Identity;
            chrome.Arrange(new Rect(new Point(-chrome.Margin.Left, -chrome.Margin.Top), new Size(680, 614)));
            chrome.UpdateLayout();
            SaveDrawingTestVisual(chrome, "drawer-cover-editor.png", false);
        }
        finally { editor.Close(); }
        True(editor.Result is null && repository.GetDrawersAsync().GetAwaiter().GetResult().All(x => x.Cover is null),
            "取消编辑修改了资料库");
    });

    private static void DrawerMenuUpwardPlacement()
    {
        var anchor = new Border { Width = 40, Height = 30 };
        var host = new Window { Width = 600, Height = 500, Content = anchor, ShowActivated = false,
            Opacity = 0, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var menu = new DrawerMenuPopup { PlacementTarget = anchor, Placement = PlacementMode.Top };
        menu.Actions.Children.Add(new TextBlock { Text = "上侧展开", Height = 50 });
        try
        {
            host.Show(); PumpDrawerAnimation(40);
            menu.ShowMenu(); PumpDrawerAnimation(210);
            menu.Dismiss(); PumpDrawerAnimation(65);
            True(menu.IsOpen && ((TranslateTransform)menu.Child.RenderTransform).Y > 0, "上侧菜单没有向下收回");
            PumpDrawerAnimation(170);
            True(!menu.IsOpen, "上侧菜单淡出结束未关闭");
            menu.ShowMenu(); menu.Dismiss(); menu.ShowMenu();
            PumpDrawerAnimation(240);
            True(menu.IsExpanded && menu.IsOpen, "快速开关执行了过期关闭");
        }
        finally { menu.IsOpen = false; host.Close(); }
    }

    private static void DrawerCoverDialogResults() => WithMainDrawerWindow((window, _) =>
    {
        var imports = (BoardImportService)typeof(MainWindow).GetField("_importService", PrivateInstance)!.GetValue(window)!;
        using var bitmap = CoverTestBitmap();
        var asset = imports.SaveEditedBitmapAsync(bitmap).GetAwaiter().GetResult();
        foreach (var action in new[] { "confirm", "cancel", "close" })
        {
            var editor = new DrawerCoverWindow(asset.FullPath) { Opacity = 0, ShowActivated = false };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (action == "confirm")
                    ((Button)editor.FindName("CoverConfirm")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (action == "cancel")
                    typeof(DrawerCoverWindow).GetMethod("OnCancelClick", PrivateInstance)!.Invoke(editor, new object[] { editor, new RoutedEventArgs() });
                else editor.Close();
                True(editor.IsVisible, "编辑器没有等待原位缩小淡出");
            };
            editor.Loaded += (_, _) => timer.Start();
            try
            {
                Equal(action == "confirm", editor.ShowDialog() == true);
                True((editor.Result is not null) == (action == "confirm"), "取消产生了封面或确认丢失结果");
                True(!editor.IsVisible, "关闭动画结束仍残留编辑窗口");
            }
            finally { timer.Stop(); editor.Result?.Dispose(); editor.Close(); }
        }
    });
}
