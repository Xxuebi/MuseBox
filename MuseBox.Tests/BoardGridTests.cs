using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static void BoardGridMenuAndDisplayModes() => WithDrawingBoard((window, _) =>
    {
        var none = window.FindName("GridNoneMenuItem") as MenuItem ??
                   throw new InvalidOperationException("缺少无网格菜单项");
        var lines = window.FindName("GridLinesMenuItem") as MenuItem ??
                    throw new InvalidOperationException("缺少线网格菜单项");
        var dots = window.FindName("GridDotsMenuItem") as MenuItem ??
                   throw new InvalidOperationException("缺少点网格菜单项");
        var snap = window.FindName("SnapToGridMenuItem") as MenuItem ??
                   throw new InvalidOperationException("缺少网格吸附菜单项");
        var grayscale = window.FindName("GrayscaleMenuItem") as MenuItem ??
                        throw new InvalidOperationException("缺少去色菜单项");
        var board = window.FindName("BoardViewMenuItem") as MenuItem ??
                    throw new InvalidOperationException("缺少画板子菜单");
        var contextMenu = ((FrameworkElement)window.FindName("BoardSurface")).ContextMenu;
        var visibleHeaders = contextMenu.Items.OfType<MenuItem>()
            .Where(item => item.Visibility == Visibility.Visible)
            .Select(item => item.Header?.ToString()).ToArray();
        Equal("撤回|重做|复制|粘贴|删除|组合|解散组合|排列|层级|添加注释|绘制|保存|画板|重置图片|画板模式|设置…",
            string.Join('|', visibleHeaders));
        var layer = (MenuItem)window.FindName("LayerOrderMenuItem");
        Equal("上移一层|下移一层|置于顶层|置于底层",
            string.Join('|', layer.Items.OfType<MenuItem>().Select(item => item.Header)));
        var arrange = (MenuItem)window.FindName("ArrangeMenuItem");
        arrange.ApplyTemplate();
        var submenuPopup = arrange.Template.FindName("PART_Popup", arrange) as Popup;
        True(submenuPopup is not null && !PopupTransitions.GetExitEnabled(submenuPopup),
            "子菜单仍启用了关闭淡出动画");

        True(board.Items.Contains(grayscale), "去色功能没有收进画板子菜单");
        True(board.Items.OfType<MenuItem>().Any(item => item.Items.Contains(lines)),
            "网格功能没有收进画板子菜单");
        True(snap.StaysOpenOnClick, "切换吸附到网格时不应关闭右键菜单");

        CallDrawing(window, "OnGridStyleClick", lines, new RoutedEventArgs());
        var viewport = (BoardViewport)typeof(BoardWindow).GetField("_viewport", PrivateInstance)!.GetValue(window)!;
        Equal(BoardGridStyle.Lines, viewport.GridStyle);
        True(lines.IsChecked && !none.IsChecked && !dots.IsChecked, "网格样式没有保持单选状态");
        CallDrawing(window, "OnSnapToGridClick", snap, new RoutedEventArgs());
        True(viewport.SnapToGrid && snap.IsChecked, "吸附开关没有独立启用");

        ArrangeBoardSurface(window);
        SetDrawingField(window, "_viewZoom", 2d);
        SetDrawingField(window, "_viewPanX", 123d);
        SetDrawingField(window, "_viewPanY", 234d);
        CallDrawing(window, "OnResetCameraClick", window, new RoutedEventArgs());
        Equal(2d, (double)typeof(BoardWindow).GetField("_viewZoom", PrivateInstance)!.GetValue(window)!);
        Equal(((FrameworkElement)window.FindName("BoardSurface")).ActualWidth / 2,
            (double)typeof(BoardWindow).GetField("_viewPanX", PrivateInstance)!.GetValue(window)!, .001);

        SetDrawingField(window, "_viewPanX", 100d);
        SetDrawingField(window, "_viewPanY", 80d);
        var surface = (FrameworkElement)window.FindName("BoardSurface");
        var worldX = (surface.ActualWidth / 2 - 100) / 2;
        var worldY = (surface.ActualHeight / 2 - 80) / 2;
        CallDrawing(window, "OnResetCameraZoomClick", window, new RoutedEventArgs());
        Equal(1d, (double)typeof(BoardWindow).GetField("_viewZoom", PrivateInstance)!.GetValue(window)!);
        Equal(surface.ActualWidth / 2 - worldX,
            (double)typeof(BoardWindow).GetField("_viewPanX", PrivateInstance)!.GetValue(window)!, .001);
        Equal(surface.ActualHeight / 2 - worldY,
            (double)typeof(BoardWindow).GetField("_viewPanY", PrivateInstance)!.GetValue(window)!, .001);

        var pixels = new byte[] { 20, 80, 200, 117 };
        var color = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        var gray = (BitmapSource)typeof(BoardWindow).GetMethod("CreateGrayscaleSource",
                BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { color })!;
        var output = new byte[4];
        gray.CopyPixels(output, 4, 0);
        True(output[0] == output[1] && output[1] == output[2] && output[3] == 117,
            "去色图片没有保留 Alpha 或 RGB 未转为灰度");
        True(BoardShortcutCatalog.CreateDefaults()[BoardShortcutCatalog.ResetCamera] == "" &&
             BoardShortcutCatalog.CreateDefaults()[BoardShortcutCatalog.ResetCameraZoom] == "" &&
             BoardShortcutCatalog.CreateDefaults()[BoardShortcutCatalog.ToggleGrayscale] == "",
            "新增命令占用了现有快捷键");
        True(!grayscale.IsChecked, "去色应默认为当前会话关闭");

        var spacingMethod = typeof(BoardGridVisual).GetMethod("CalculateVisibleWorldSpacing",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var normalSpacing = (double)spacingMethod.Invoke(null, new object[] { 32d, 1d })!;
        var zoomedSpacing = (double)spacingMethod.Invoke(null, new object[] { 32d, 2d })!;
        Equal(16d, normalSpacing);
        Equal(8d, zoomedSpacing);
        True(zoomedSpacing < normalSpacing, "放大画板时应显示更细的世界坐标网格");
        var beforeSubdivision = (double)spacingMethod.Invoke(null, new object[] { 32d, 1.5d })! * 1.5;
        var afterSubdivision = (double)spacingMethod.Invoke(null, new object[] { 32d, 1.6d })! * 1.6;
        True(afterSubdivision < beforeSubdivision, "放大跨过细分级别时屏幕网格应变小");
        var brushMethod = typeof(BoardGridVisual).GetMethod("CreateGridBrush",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var gridBrush = (SolidColorBrush)brushMethod.Invoke(null,
            new object[] { false, (byte)28, (byte)20 })!;
        True(gridBrush.Color.A is > 0 and < 128,
            "线点网格颜色应保持半透明");
    });

    private static void BoardGridSnapping() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        ChooseImages(window, 0, 1);
        var selected = LiveImages(window).Where(item => BoardSelection(window).Contains(item.Id)).ToArray();
        var offset = new Vector(selected[1].X - selected[0].X, selected[1].Y - selected[0].Y);
        var snapshot = CallDrawing(window, "Snapshot")!;
        SetDrawingField(window, "_gestureSnapshot", snapshot);
        SetDrawingField(window, "_mouseStart", new Point(0, 0));
        SetDrawingField(window, "_viewZoom", 1d);
        var viewport = (BoardViewport)typeof(BoardWindow).GetField("_viewport", PrivateInstance)!.GetValue(window)!;
        viewport.GridSpacing = 32;
        viewport.SnapToGrid = true;
        CallDrawing(window, "ApplySnappedDrag", new Point(19, 27));
        selected = LiveImages(window).Where(item => BoardSelection(window).Contains(item.Id)).ToArray();
        var bounds = selected.Select(item => new Rect(item.X, item.Y, item.Width, item.Height))
            .Aggregate(Rect.Empty, (current, next) => { current.Union(next); return current; });
        Equal(0d, bounds.Left % 16, .001);
        Equal(0d, bounds.Top % 16, .001);
        Equal(offset.X, selected[1].X - selected[0].X, .001);
        Equal(offset.Y, selected[1].Y - selected[0].Y, .001);
    });

    private static void BoardGridPersistence()
    {
        var directory = CreateTempDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            var repository = new BoardRepository(paths);
            repository.InitializeAsync().GetAwaiter().GetResult();
            repository.SaveViewportAsync(new BoardViewport
            {
                DrawerId = "A", GridStyle = BoardGridStyle.Dots,
                GridSpacing = 32, SnapToGrid = true
            }).GetAwaiter().GetResult();
            var restored = new BoardRepository(paths);
            restored.InitializeAsync().GetAwaiter().GetResult();
            var viewport = restored.GetViewportAsync("A").GetAwaiter().GetResult();
            Equal(BoardGridStyle.Dots, viewport.GridStyle);
            Equal(32d, viewport.GridSpacing);
            True(viewport.SnapToGrid, "网格吸附设置没有持久化");

            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={paths.Database};Pooling=False");
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('viewports') WHERE name IN ('grid_style','grid_spacing','snap_to_grid')";
            Equal(3L, (long)command.ExecuteScalar()!);
        }
        finally { Directory.Delete(directory, true); }
    }
}
