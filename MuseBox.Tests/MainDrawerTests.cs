using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenshotCollector.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using TextBox = System.Windows.Controls.TextBox;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static void WithMainDrawerWindow(Action<MainWindow, BoardRepository> test) => WithDrawingBoard((board, repository) =>
    {
        board.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), board,
            typeof(BoardWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        var imports = (BoardImportService)typeof(BoardWindow).GetField("_importService", PrivateInstance)!.GetValue(board)!;
        var window = new MainWindow(repository, imports);
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(MainWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window,
            typeof(MainWindow).GetMethod("OnClosing", PrivateInstance | BindingFlags.DeclaredOnly)!);
        try
        {
            AwaitMainTask(window, "ReloadDrawersAsync");
            test(window, repository);
        }
        finally { window.Close(); }
    });

    private static object? MainCall(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, PrivateInstance)!.Invoke(window, args);

    private static void AwaitMainTask(MainWindow window, string method, params object[] args)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            var task = (Task)MainCall(window, method, args)!;
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static ObservableCollection<DrawerCardModel> MainDrawers(MainWindow window) =>
        (ObservableCollection<DrawerCardModel>)typeof(MainWindow).GetField("_drawers", PrivateInstance)!.GetValue(window)!;

    private static IEnumerable<FrameworkElement> MainDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var nested in MainDescendants(child)) yield return nested;
        }
    }

    private static FrameworkElement ArrangeMain(MainWindow window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        return content;
    }

    private static void UnlimitedDrawerPersistence() => WithMainDrawerWindow((window, repository) =>
    {
        repository.UpdateDrawerNameAsync("B", "已有内容不要覆盖").GetAwaiter().GetResult();
        var original = new BoardTextItem { DrawerId = "B", DocumentData = "original text" };
        repository.AddTextItemsAsync(new[] { original }).GetAwaiter().GetResult();
        var added = Task.WhenAll(Enumerable.Range(0, 30).Select(_ => repository.AddNextDrawerAsync())).GetAwaiter().GetResult();
        Equal(30, added.Select(x => x.Id).Distinct().Count());
        Equal("E", added[0].Id); Equal("Z", added[21].Id); Equal("AA", added[22].Id); Equal("AH", added[29].Id);
        True(added.Select(x => x.SortOrder).SequenceEqual(Enumerable.Range(4, 30)), "新增抽屉未连续排在列表底部");
        foreach (var drawer in added)
            Equal(drawer.Id, repository.GetViewportAsync(drawer.Id).GetAwaiter().GetResult().DrawerId);
        repository.InitializeAsync().GetAwaiter().GetResult();
        Equal(34, repository.GetDrawersAsync().GetAwaiter().GetResult().Count);
        Equal("已有内容不要覆盖", repository.GetDrawersAsync().GetAwaiter().GetResult().Single(x => x.Id == "B").DisplayName);
        Equal("original text", repository.GetTextItemsAsync("B").GetAwaiter().GetResult().Single().DocumentData);
        var alpha = typeof(BoardRepository).GetMethod("DrawerIdFromIndex", BindingFlags.Static | BindingFlags.NonPublic)!;
        Equal("ZZ", (string)alpha.Invoke(null, new object[] { 701 })!);
        Equal("AAA", (string)alpha.Invoke(null, new object[] { 702 })!);
        repository.DeleteDrawerAsync("F").GetAwaiter().GetResult();
        Equal("AI", repository.AddNextDrawerAsync().GetAwaiter().GetResult().Id);
        repository.InitializeAsync().GetAwaiter().GetResult();
        True(repository.GetDrawersAsync().GetAwaiter().GetResult().All(x => x.Id != "F"), "已删除的新增抽屉被重新创建");
        using var bitmap = CreateBitmap();
        var imports = (BoardImportService)typeof(MainWindow).GetField("_importService", PrivateInstance)!.GetValue(window)!;
        imports.ImportBitmapAsync("AA", bitmap).GetAwaiter().GetResult();
        Equal(1, repository.GetItemsAsync("AA").GetAwaiter().GetResult().Count);
    });

    private static void ResponsiveDrawerLayout() => WithMainDrawerWindow((window, repository) =>
    {
        var models = MainDrawers(window);
        var labels = new[] { "机械", "雪械重工", "角色参考", "界面素材" };
        var colors = new[] { "#AABDC9", "#D9BFB0", "#B6CCB3", "#B8BDE0" };
        for (var i = 0; i < models.Count; i++)
        {
            models[i].DisplayName = labels[i];
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing((Brush)new BrushConverter().ConvertFromString(colors[i])!, null,
                new RectangleGeometry(new Rect(0, 0, 180, 100), 8, 8)));
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), null,
                new EllipseGeometry(new Point(90, 50), 32, 32)));
            var image = new DrawingImage(drawing); image.Freeze(); models[i].Thumbnail = image;
        }
        ((TextBlock)window.FindName("HotkeyText")).Text = "截图快捷键已关闭";
        ((TextBlock)window.FindName("StatusText")).Text = "等待截图、复制或拖入图片";
        var content = ArrangeMain(window, 360, 500);
        var roots = MainDescendants(content).Where(x => x.Name == "DrawerRoot").ToArray();
        Equal(4, roots.Length);
        var first = roots[0].TranslatePoint(new Point(), content);
        var second = roots[1].TranslatePoint(new Point(), content);
        var third = roots[2].TranslatePoint(new Point(), content);
        Equal(first.Y, second.Y, .01); True(second.X > first.X + roots[0].ActualWidth, "抽屉未改成两列");
        True(third.Y > first.Y + roots[0].ActualHeight, "下一行抽屉未向下排布");
        var collect = MainDescendants(roots[0]).OfType<Button>().Single(x => x.Name == "DrawerCollectButton");
        var open = MainDescendants(roots[0]).OfType<Button>().Single(x => x.Name == "DrawerOpenButton");
        var collectBounds = collect.TransformToAncestor(roots[0]).TransformBounds(new Rect(collect.RenderSize));
        var openBounds = open.TransformToAncestor(roots[0]).TransformBounds(new Rect(open.RenderSize));
        True(openBounds.Top >= collectBounds.Bottom, "打开画板区域没有位于置入区域下方");
        True(collect.ActualHeight > open.ActualHeight * 2, "置入预览区太小");
        Equal("A", collect.Tag.ToString()!); Equal("A", open.Tag.ToString()!);
        True(collect.AllowDrop, "新置入区域丢失拖入支持");
        SaveDrawingTestVisual(content, "main-drawers-two-columns.png", false);
        content = ArrangeMain(window, 580, 390);
        roots = MainDescendants(content).Where(x => x.Name == "DrawerRoot").ToArray();
        Equal(roots[0].TranslatePoint(new Point(), content).Y, roots[2].TranslatePoint(new Point(), content).Y, .01);
        True(roots[3].TranslatePoint(new Point(), content).Y > roots[0].TranslatePoint(new Point(), content).Y, "加宽后未自动变成三列");
        SaveDrawingTestVisual(content, "main-drawers-three-columns.png", false);
        for (var i = 0; i < 12; i++) AwaitMainTask(window, "AddDrawerAsync");
        content = ArrangeMain(window, 360, 370);
        var scroll = (ScrollViewer)window.FindName("DrawerScroll");
        True(scroll.ScrollableHeight > 0 && scroll.ViewportHeight > 0, "更多抽屉没有形成纵向滚动");
        Equal(0d, scroll.ScrollableWidth);
        var scrollbar = MainDescendants(scroll).OfType<System.Windows.Controls.Primitives.ScrollBar>()
            .Single(x => x.Orientation == System.Windows.Controls.Orientation.Vertical);
        True(scrollbar.ActualWidth <= 10.01, $"小窗未使用细圆角滚动条：Actual={scrollbar.ActualWidth}, Width={scrollbar.Width}, MinWidth={scrollbar.MinWidth}");
        True(MainDescendants(scrollbar).OfType<System.Windows.Controls.Primitives.Track>().Single().ViewportSize > 0,
            "自定义滚动条没有绑定实际滚动范围");
        var add = (Button)window.FindName("AddDrawerButton");
        True(add.TranslatePoint(new Point(0, add.ActualHeight), content).Y <= content.ActualHeight - 8, "新增按钮超出窗口");
        scroll.ScrollToEnd(); content.UpdateLayout();
        True(scroll.VerticalOffset > 0, "不能滚到后续抽屉");
        SaveDrawingTestVisual(content, "main-drawers-scroll.png", false);
        // The layout primitive also has a safe one-column fallback for narrow hosts.
        var panel = new AdaptiveDrawerPanel();
        panel.Children.Add(new Border()); panel.Children.Add(new Border());
        panel.Measure(new Size(180, double.PositiveInfinity)); panel.Arrange(new Rect(new Point(), panel.DesiredSize));
        True(panel.Children[1].TranslatePoint(new Point(), panel).Y > 0, "窄宽度没有单列回退");
    });

    private static void NewDrawerCollectAndRename() => WithMainDrawerWindow((window, repository) =>
    {
        ArrangeMain(window, 360, 420);
        var first = MainDrawers(window)[0];
        AwaitMainTask(window, "AddDrawerAsync");
        Equal(5, MainDrawers(window).Count); Equal("E", MainDrawers(window)[4].Id);
        True(ReferenceEquals(first, MainDrawers(window)[0]), "新增抽屉重新创建了已有卡片");
        typeof(MainWindow).GetField("_isBusy", PrivateInstance)!.SetValue(window, true);
        AwaitMainTask(window, "AddDrawerAsync");
        Equal(5, MainDrawers(window).Count);
        typeof(MainWindow).GetField("_isBusy", PrivateInstance)!.SetValue(window, false);
        var added = MainDrawers(window).Last();
        added.IsEditing = true;
        AwaitMainTask(window, "SaveDrawerNameAsync", added.Id, "新的画板名称很长也需要正常截断显示");
        Equal(added.DisplayName, repository.GetDrawersAsync().GetAwaiter().GetResult().Single(x => x.Id == added.Id).DisplayName);
        True(added.OpenToolTip.Contains(added.DisplayName), "长名称未在打开按钮提示中完整显示");
        Equal("删除抽屉及其内容", added.DeleteToolTip);
        Equal("清空并重置画板", MainDrawers(window)[1].DeleteToolTip);
        var directory = Path.GetDirectoryName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            (string)typeof(BoardRepository).GetField("_connectionString", PrivateInstance)!.GetValue(repository)!).DataSource)!;
        var source = Path.Combine(directory, "new-drawer-test.png");
        using (var bitmap = CreateBitmap()) bitmap.Save(source, System.Drawing.Imaging.ImageFormat.Png);
        AwaitMainTask(window, "ImportFilesAsync", added.Id, new[] { source });
        Equal(1, repository.GetItemsAsync(added.Id).GetAwaiter().GetResult().Count);
        True(added.Thumbnail is not null, "新抽屉置入图片后未更新预览");
        True(((ItemsControl)window.FindName("DrawerList")).IsEnabled, "置入期间禁用了整个列表");
        AwaitMainTask(window, "ReloadDrawersAsync");
        Equal(5, MainDrawers(window).Count);
        True(MainDrawers(window).Last().Thumbnail is not null, "重载未恢复新抽屉预览");
    });

    private static void MainResizeAndSizeSettings() => WithMainDrawerWindow((window, _) =>
    {
        Equal(ResizeMode.CanResize, window.ResizeMode); Equal(SizeToContent.Manual, window.SizeToContent);
        var hitTest = typeof(MainWindow).GetMethod("MainResizeHitTest", BindingFlags.Static | BindingFlags.NonPublic)!;
        var size = new Size(360, 500);
        foreach (var (point, expected) in new[]
        {
            (new Point(2, 250), 10), (new Point(358, 250), 11), (new Point(180, 2), 12), (new Point(180, 498), 15),
            (new Point(12, 12), 13), (new Point(348, 12), 14), (new Point(12, 488), 16), (new Point(348, 488), 17),
            (new Point(100, 100), 0), (new Point(-1, 30), 0), (new Point(180, 25), 0)
        })
            Equal(expected, (int)hitTest.Invoke(null, new object[] { point, size })!);
        var settings = new AppSettings { MainWidth = 580, MainHeight = 420 };
        typeof(MainWindow).GetField("_settings", PrivateInstance)!.SetValue(window, settings);
        MainCall(window, "ApplySavedMainSize", new Size(1920, 1080));
        Equal(580d, window.Width); Equal(420d, window.Height);
        var serialized = System.Text.Json.JsonSerializer.Serialize(settings.Copy());
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(serialized)!;
        Equal(580d, restored.MainWidth); Equal(420d, restored.MainHeight);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
        Equal(360d, legacy.MainWidth); Equal(500d, legacy.MainHeight);
        settings.MainWidth = 99999; settings.MainHeight = 1;
        MainCall(window, "ApplySavedMainSize", new Size(900, 700));
        Equal(900d, window.Width); Equal(window.MinHeight, window.Height);
        settings.MainWidth = double.NaN; settings.MainHeight = double.PositiveInfinity;
        MainCall(window, "ApplySavedMainSize", new Size(900, 700));
        Equal(360d, window.Width); Equal(500d, window.Height);
    });
}
