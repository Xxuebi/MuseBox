using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenshotCollector.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Bitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using Size = System.Windows.Size;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private sealed class CollectionClipboardStub : IClipboardImageService
    {
        public int Reads { get; private set; }
        public bool Empty { get; set; }
        public Func<Bitmap>? BitmapFactory { get; set; }
        public ClipboardImageResult ReadImage()
        {
            Reads++;
            return new ClipboardImageResult(Empty ? null : BitmapFactory?.Invoke() ?? CoverTestBitmap(), "测试图片", null);
        }
        public ClipboardClearResult Clear() => new(true, null);
    }

    private static T DrawerPart<T>(MainWindow window, string id, string name) where T : FrameworkElement
    {
        var list = (ItemsControl)window.FindName("DrawerList");
        var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(MainDrawers(window).Single(x => x.Id == id));
        return MainDescendants(container).OfType<T>().Single(x => x.Name == name);
    }

    private static void ShowCollectionTestWindow(MainWindow window)
    {
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        PumpDrawerAnimation(40);
    }

    private static void WaitForCollection(Func<bool> complete)
    {
        var clock = Stopwatch.StartNew();
        while (!complete() && clock.ElapsedMilliseconds < 4000) PumpDrawerAnimation(10);
        True(complete(), "等待收集操作超时");
    }

    private static void SampleMainMotion(MainWindow window, string method, object[] args, Action<Task> sample)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            var task = (Task)MainCall(window, method, args)!;
            sample(task);
            WaitForCollection(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void FixedCoverClickFeedback() => WithMainDrawerWindow((window, repository) =>
    {
        var clipboard = new CollectionClipboardStub();
        clipboard.BitmapFactory = () =>
        {
            var incoming = new Bitmap(80, 50);
            using var graphics = System.Drawing.Graphics.FromImage(incoming);
            graphics.Clear(DrawingColor.FromArgb(238, 45, 72));
            return incoming;
        };
        typeof(MainWindow).GetField("_clipboardImageService", PrivateInstance)!.SetValue(window, clipboard);
        var imports = (BoardImportService)typeof(MainWindow).GetField("_importService", PrivateInstance)!.GetValue(window)!;
        using var bitmap = CoverTestBitmap();
        var source = imports.SaveEditedBitmapAsync(bitmap).GetAwaiter().GetResult();
        var cover = imports.SaveDrawerCoverAsync("A", source.FullPath, bitmap, new CoverCropState()).GetAwaiter().GetResult();
        MainCall(window, "ApplyDrawerCover", MainDrawers(window)[0], cover);
        ShowCollectionTestWindow(window);
        var collect = DrawerPart<Button>(window, "A", "DrawerCollectButton");
        var layer = DrawerPart<Canvas>(window, "A", "AnimationLayer");
        var thumbnail = MainDrawers(window)[0].Thumbnail;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            collect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(1, layer.Children.Count);
            var echo = (Image)layer.Children[0];
            True(!ReferenceEquals(thumbnail, echo.Source), "点击仍然使用固定封面作为置入反馈");
            var pixels = new byte[4];
            var feedback = (System.Windows.Media.Imaging.BitmapSource)echo.Source;
            feedback.CopyPixels(new Int32Rect(feedback.PixelWidth / 2, feedback.PixelHeight / 2, 1, 1), pixels, 4, 0);
            True(pixels[2] > 220 && pixels[1] < 80 && pixels[0] < 100, "置入反馈不是剪贴板中的红色图片");
            PumpDrawerAnimation(85);
            var transforms = (TransformGroup)echo.RenderTransform;
            True(transforms.Children.OfType<TranslateTransform>().Single().Y > 0 &&
                transforms.Children.OfType<ScaleTransform>().Single().ScaleX < 1, "封面置入没有向下缩小移动");
            SaveDrawingTestVisual((FrameworkElement)window.Content, "collection-fixed-cover-click.png", false);
            WaitForCollection(() => !(bool)typeof(MainWindow).GetField("_isBusy", PrivateInstance)!.GetValue(window)!);
            Equal(1, repository.GetItemCountAsync("A").GetAwaiter().GetResult());
            Equal(cover, MainDrawers(window)[0].Cover!);
            True(ReferenceEquals(thumbnail, MainDrawers(window)[0].Thumbnail), "置入时替换了固定封面");
            PumpDrawerAnimation(340);
            Equal(0, layer.Children.Count);
            clipboard.Empty = true;
            collect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(0, layer.Children.Count);
            Equal(1, repository.GetItemCountAsync("A").GetAwaiter().GetResult());
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private static void CollectionCompactLayoutAndPersistence() => WithMainDrawerWindow((window, repository) =>
    {
        ArrangeMain(window, 360, 420);
        MainDrawers(window)[0].DisplayName = "机械";
        MainDrawers(window)[1].DisplayName = "雪械重工";
        AwaitMainTask(window, "SetCollectionModeAsync", true);
        var content = ArrangeMain(window, 360, 420);
        True(window.IsCollectionMode && !window.CollectionTransitioning, "沉浸模式没有稳定进入");
        Equal(1d, window.CollectionProgress);
        Equal(Visibility.Collapsed, DrawerPart<Button>(window, "A", "DrawerCollectButton").Visibility);
        Equal(Visibility.Visible, DrawerPart<Button>(window, "A", "DrawerSettingsButton").Visibility);
        Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("AddDrawerHost")).Visibility);
        var footer = DrawerPart<Button>(window, "A", "DrawerOpenButton");
        True(footer.AllowDrop && footer.ToolTip.ToString()!.Contains("收集"), "紧凑按钮提示或拖入行为仍然是打开画板");
        Equal("收集图片到此抽屉", System.Windows.Automation.AutomationProperties.GetName(footer));
        Equal(50d, DrawerPart<Border>(window, "A", "DrawerRoot").ActualHeight, 1);
        var panel = MainDescendants(content).OfType<AdaptiveDrawerPanel>().Single();
        Equal(1d, panel.CollectionProgress);
        SaveDrawingTestVisual(content, "collection-compact-two-columns.png", false);
        content = ArrangeMain(window, 580, 280);
        var roots = MainDrawers(window).Select(x => DrawerPart<Border>(window, x.Id, "DrawerRoot")).ToArray();
        Equal(roots[0].TranslatePoint(new Point(), content).Y, roots[2].TranslatePoint(new Point(), content).Y, .1);
        True(roots[3].TranslatePoint(new Point(), content).Y > roots[2].TranslatePoint(new Point(), content).Y, "紧凑抽屉加宽未自适应");
        SaveDrawingTestVisual(content, "collection-compact-three-columns.png", false);
        AwaitMainTask(window, "AddDrawerAsync");
        Equal(4, MainDrawers(window).Count);
        MainCall(window, "OnDrawerSettingsClick", DrawerPart<Button>(window, "A", "DrawerSettingsButton"), new RoutedEventArgs());
        True(typeof(MainWindow).GetField("_drawerMenu", PrivateInstance)!.GetValue(window) is DrawerMenuPopup,
            "沉浸模式没有恢复抽屉菜单");
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", PrivateInstance)!.GetValue(window)!;
        var saved = System.Text.Json.JsonSerializer.Serialize(settings.Copy());
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(saved)!;
        True(restored.ImmersiveCollectionEnabled, "设置复制和序列化丢失模式");
        True(!System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.ImmersiveCollectionEnabled, "旧设置不应默认开启");
        AwaitMainTask(window, "SetCollectionModeAsync", false);
        content = ArrangeMain(window, 360, 420);
        Equal(Visibility.Visible, DrawerPart<Button>(window, "A", "DrawerCollectButton").Visibility);
        Equal(Visibility.Visible, DrawerPart<Button>(window, "A", "DrawerSettingsButton").Visibility);
        True(!DrawerPart<Button>(window, "A", "DrawerOpenButton").AllowDrop, "退出模式未恢复画板区域");
        True(((Button)window.FindName("AddDrawerButton")).IsEnabled, "退出后新建仍被禁用");
        True(DrawerPart<Border>(window, "A", "DrawerRoot").ActualHeight > 140, "退出后未恢复预览区");
        typeof(MainWindow).GetField("_settings", PrivateInstance)!.SetValue(window, restored);
        MainCall(window, "ApplyInitialCollectionMode");
        ArrangeMain(window, 360, 420);
        True(window.IsCollectionMode && window.CollectionProgress == 1, "启动未恢复模式");
        Equal(4, repository.GetDrawersAsync().GetAwaiter().GetResult().Count);
    });

    private static void CollectionFooterImportsAndFeedback() => WithMainDrawerWindow((window, repository) =>
    {
        var clipboard = new CollectionClipboardStub();
        typeof(MainWindow).GetField("_clipboardImageService", PrivateInstance)!.SetValue(window, clipboard);
        ArrangeMain(window, 360, 420);
        AwaitMainTask(window, "SetCollectionModeAsync", true);
        ShowCollectionTestWindow(window);
        var footer = DrawerPart<Button>(window, "B", "DrawerOpenButton");
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            var beforeReads = clipboard.Reads;
            footer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(beforeReads + 1, clipboard.Reads);
            Equal(1, DrawerPart<Canvas>(window, "B", "CompactCollectionFeedback").Children.Count);
            Equal(0, DrawerPart<Canvas>(window, "B", "AnimationLayer").Children.Count);
            WaitForCollection(() => !(bool)typeof(MainWindow).GetField("_isBusy", PrivateInstance)!.GetValue(window)!);
            Equal(1, repository.GetItemCountAsync("B").GetAwaiter().GetResult());
            var notices = (System.Collections.IDictionary)typeof(MainWindow)
                .GetField("_collectionNotices", PrivateInstance)!.GetValue(window)!;
            Equal(1, notices.Count);
            var notice = (System.Windows.Controls.Primitives.Popup)notices["B"]!;
            True(notice.IsOpen && notice.Placement == System.Windows.Controls.Primitives.PlacementMode.Top,
                "“已收集”提示没有显示在抽屉上方");
            Equal("已收集", ((TextBlock)((Border)notice.Child).Child).Text);
            True(MainDrawers(window)[1].Thumbnail is not null, "紧凑置入未更新普通模式的预览");
            var path = repository.GetItemsAsync("B").GetAwaiter().GetResult().Single().AssetPath;
            AwaitMainTask(window, "ImportFilesAsync", "C", new[] { path });
            Equal(1, repository.GetItemCountAsync("C").GetAwaiter().GetResult());
            True(window.IsCollectionMode, "置入文件退出了收集模式");
            PumpDrawerAnimation(900);
            Equal(0, notices.Count);
            AwaitMainTask(window, "SetCollectionModeAsync", false);
            PumpDrawerAnimation(350);
            True(DrawerPart<Canvas>(window, "B", "CompactCollectionFeedback").Children.Count == 0, "退出后有置入动画残留");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private static void CollectionReversibleAnimation() => WithMainDrawerWindow((window, _) =>
    {
        for (var i = 0; i < 8; i++) AwaitMainTask(window, "AddDrawerAsync");
        ShowCollectionTestWindow(window);
        var scroll = (ScrollViewer)window.FindName("DrawerScroll");
        scroll.ScrollToVerticalOffset(110); window.UpdateLayout();
        var oldOffset = scroll.VerticalOffset;
        SampleMainMotion(window, "SetCollectionModeAsync", new object[] { true }, task =>
        {
            True(!task.IsCompleted && window.CollectionTransitioning && window.HasAnimatedProperties,
                "进入沉浸模式没有建立过渡动画");
            PumpDrawerAnimation(45);
            True(!DrawerPart<Border>(window, "A", "DrawerRoot").IsHitTestVisible, "过渡时未阻止误置入");
            SaveDrawingTestVisual((FrameworkElement)window.Content, "collection-transition-enter.png", false);
        });
        Equal(1d, window.CollectionProgress);
        Equal(0d, scroll.VerticalOffset, 1);
        SampleMainMotion(window, "SetCollectionModeAsync", new object[] { false }, task =>
        {
            True(!task.IsCompleted && window.CollectionTransitioning && window.HasAnimatedProperties,
                "退出沉浸模式没有建立反向过渡动画");
            PumpDrawerAnimation(45);
        });
        Equal(0d, window.CollectionProgress);
        Equal(oldOffset, scroll.VerticalOffset, 1);
        SampleMainMotion(window, "SetCollectionModeAsync", new object[] { true }, _ =>
        {
            True(window.CollectionTransitioning, "快速反向前未进入过渡状态");
            SampleMainMotion(window, "SetCollectionModeAsync", new object[] { false }, _ =>
            {
                True(window.CollectionTransitioning && !window.IsCollectionMode, "快速反向点击没有接管旧动画");
            });
        });
        True(!window.IsCollectionMode && !window.CollectionTransitioning && window.CollectionProgress == 0,
            "旧动画回调覆盖了反向切换结果");
        SampleMainMotion(window, "SetCollectionModeAsync", new object[] { true }, _ =>
        {
            PumpDrawerAnimation(55);
            window.Hide();
            Equal(1d, window.CollectionProgress);
            True(!window.CollectionTransitioning, "隐藏窗口未清理动画");
        });
        window.Show();
        PumpDrawerAnimation(35);
        True(window.IsVisible && window.CollectionProgress == 1, "重新显示没有保持模式或小窗消失");
    });

    private static void NewDrawerEntranceAndScroll() => WithMainDrawerWindow((window, repository) =>
    {
        ShowCollectionTestWindow(window);
        var first = MainDrawers(window)[0];
        var scroll = (ScrollViewer)window.FindName("DrawerScroll");
        SampleMainMotion(window, "AddDrawerAsync", Array.Empty<object>(), task =>
        {
            WaitForCollection(() => MainDrawers(window).Count == 5);
            PumpDrawerAnimation(65);
            var list = (ItemsControl)window.FindName("DrawerList");
            var added = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(MainDrawers(window).Last());
            var reveal = AdaptiveDrawerPanel.GetRevealProgress(added);
            True(!task.IsCompleted && reveal > 0 && reveal < 1, "新抽屉没有逐步展开");
            True(added.Opacity > 0 && added.Opacity < 1, "新抽屉没有淡入");
            True(added.RenderTransform.Value.OffsetY > 0, "新抽屉没有向上入位");
            True(scroll.VerticalOffset < scroll.ScrollableHeight, "新建后滚动直接跳到底部");
            SaveDrawingTestVisual((FrameworkElement)window.Content, "collection-new-drawer-motion.png", false);
            AwaitMainTask(window, "AddDrawerAsync");
            Equal(5, MainDrawers(window).Count);
        });
        var container = (FrameworkElement)((ItemsControl)window.FindName("DrawerList")).ItemContainerGenerator.ContainerFromItem(MainDrawers(window).Last());
        Equal(1d, AdaptiveDrawerPanel.GetRevealProgress(container));
        Equal(1d, container.Opacity);
        True(container.RenderTransform.Value.IsIdentity, "新建结束有变换残留");
        Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 1);
        True(ReferenceEquals(first, MainDrawers(window)[0]), "新增动画重建了已有抽屉");
        Equal(5, repository.GetDrawersAsync().GetAwaiter().GetResult().Count);
        // Next drawer fills the same row and must still animate without changing its neighbour.
        SampleMainMotion(window, "AddDrawerAsync", Array.Empty<object>(), _ => PumpDrawerAnimation(80));
        Equal(6, MainDrawers(window).Count);
    });

    private static void CollectionWindowHeightRoundTrip() => WithMainDrawerWindow((window, _) =>
    {
        window.Height = 500;
        ShowCollectionTestWindow(window);
        window.UpdateLayout();
        var expanded = window.ActualHeight;
        Equal(280d, window.MinHeight);
        SampleMainMotion(window, "SetCollectionModeAsync", new object[] { true }, task =>
        {
            True(!task.IsCompleted && window.CollectionTransitioning && window.HasAnimatedProperties,
                "进入时窗口高度没有建立同步动画");
        });
        True(window.ActualHeight < expanded - 150, $"紧凑模式窗口仍然过长：{window.ActualHeight:F1}");
        Equal(160d, window.MinHeight);
        var compact = window.ActualHeight;
        window.Height = 160;
        PumpDrawerAnimation(60);
        Equal(160d, window.ActualHeight, 2);
        var scroll = (ScrollViewer)window.FindName("DrawerScroll");
        True(scroll.ActualHeight < compact && scroll.ScrollableHeight > 0, "手动缩短后抽屉区没有滚动，仍被空白高度限制");
        SaveDrawingTestVisual((FrameworkElement)window.Content, "collection-manual-min-height.png", false);
        SampleMainMotion(window, "SetCollectionModeAsync", new object[] { false }, task =>
        {
            True(!task.IsCompleted && window.CollectionTransitioning && window.HasAnimatedProperties,
                "退出时窗口高度没有建立同步动画");
        });
        Equal(expanded, window.ActualHeight, 2);
        Equal(280d, window.MinHeight);
    });
}
