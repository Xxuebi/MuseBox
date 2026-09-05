using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;
using Panel = System.Windows.Controls.Panel;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static VisualBrush SnapshotBrush(FrameworkElement visual)
    {
        var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
        if (VisualTreeHelper.GetTransform(visual) is Transform transform) bounds = transform.TransformBounds(bounds);
        bounds.Offset(VisualTreeHelper.GetOffset(visual));
        return new VisualBrush(visual) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill };
    }

    private static void SaveSettingsSnapshot(SettingsWindow window, string filename)
    {
        var content = (FrameworkElement)window.Content;
        var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, bounds);
            dc.DrawRectangle(SnapshotBrush(content), null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * 2),
            (int)Math.Ceiling(bounds.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, filename));
        encoder.Save(stream);
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void ModernSelectionControls() => WithDrawingBoard((window, _) =>
    {
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        Stroke(window, new Point(230, 170), new Point(490, 300));
        Stroke(window, new Point(230, 290), new Point(490, 190));
        CallDrawing(window, "SetToolMode", BoardToolMode.Select);
        var group = LiveDrawings(window).Single();
        ClickBoardItem(window, group, 1);
        var surface = ArrangeBoardSurface(window);
        var border = DrawingBorder(window, group.Id);
        Equal(new CornerRadius(3), border.CornerRadius);
        True(border.BorderThickness.Left <= 1.5, "选择线仍然过粗");
        var handles = (Dictionary<BoardResizeDirection, Thumb>)typeof(BoardWindow)
            .GetField("_resizeHandles", PrivateInstance)!.GetValue(window)!;
        Equal(8, handles.Count);
        foreach (var handle in handles.Values)
        {
            handle.ApplyTemplate();
            var grip = (Border)handle.Template.FindName("Grip", handle);
            True(grip.CornerRadius.TopLeft > 0, "拉伸手柄未使用圆角自定义模板");
            True(handle.Width >= 18 && grip.Width <= 8, "小手柄没有保留足够的命中面积");
            Equal(Visibility.Visible, handle.Visibility);
        }
        var rotations = (Dictionary<BoardRotationCorner, Thumb>)typeof(BoardWindow)
            .GetField("_rotationHandles", PrivateInstance)!.GetValue(window)!;
        foreach (var handle in rotations.Values)
        {
            True(handle.Cursor is not null && handle.Cursor != Cursors.Hand && handle.Cursor != Cursors.Arrow,
                "旋转手柄仍使用默认光标");
            Equal(Visibility.Visible, handle.Visibility);
            foreach (var resize in handles.Values)
            {
                var rotationBounds = new Rect(Canvas.GetLeft(handle), Canvas.GetTop(handle), handle.Width, handle.Height);
                var resizeBounds = new Rect(Canvas.GetLeft(resize), Canvas.GetTop(resize), resize.Width, resize.Height);
                True(!rotationBounds.IntersectsWith(resizeBounds) || Panel.GetZIndex(resize) > Panel.GetZIndex(handle),
                    "旋转命中区域遮挡了角落拉伸手柄");
            }
        }
        surface.Background = new SolidColorBrush(Color.FromRgb(184, 186, 189));
        SaveDrawingTestVisual(surface, "modern-selection.png", false);

        var cursorType = typeof(BoardWindow).Assembly.GetType("ScreenshotCollector.Services.BoardRotationCursor")!;
        var visual = (DrawingVisual)cursorType.GetMethod("CreateVisual", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var bitmap = new RenderTargetBitmap(128, 128, 384, 384, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "rotation-cursor.png"))) encoder.Save(stream);

        group.Rotation = 31;
        CallDrawing(window, "UpdateItemVisual", group);
        CallDrawing(window, "UpdateResizeHandles");
        foreach (var handle in handles.Values) Equal(31d, ((RotateTransform)handle.RenderTransform).Angle);
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        True(handles.Values.Concat(rotations.Values).All(h => h.Visibility == Visibility.Collapsed),
            "绘制时仍然显示控制手柄");
    });

    private static void CompactShortcutLayout()
    {
        var window = new SettingsWindow(new AppSettings());
        try
        {
            ((TabControl)window.FindName("SettingsCategories")).SelectedItem =
                window.FindName("ShortcutSettingsTab");
            window.Opacity = 0;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.Measure(new Size(780, 590));
            window.Arrange(new Rect(0, 0, 780, 590));
            window.UpdateLayout();
            var scroll = (ScrollViewer)window.FindName("ShortcutScrollViewer");
            var rows = VisualChildren<TextBox>(scroll).Where(t => t.Tag is string).ToArray();
            Equal(20, rows.Length);
            True(rows.All(t => t.ActualHeight == 26), "快捷键输入框没有压缩高度");
            True(scroll.ExtentHeight <= 750, $"快捷键列表仍过长：{scroll.ExtentHeight}");
            True(rows.All(t => t.ActualWidth > 140), "按键框被压得太窄");
            SaveSettingsSnapshot(window, "compact-shortcut-settings.png");
            var search = (TextBox)window.FindName("ShortcutSearchBox");
            search.Text = "层级";
            Equal(4, window.ShortcutGroups.SelectMany(g => g.Shortcuts).Count());
            search.Text = "ctrl + alt + g";
            Equal(BoardShortcutCatalog.Arrange, window.ShortcutGroups.SelectMany(g => g.Shortcuts).Single().Id);
            search.Text = "框选";
            Equal(0, window.ShortcutGroups.Count);
            Equal(Visibility.Visible, ((Border)window.FindName("GlobalHotkeyCard")).Visibility);
            search.Text = "没有这个功能";
            Equal(Visibility.Visible, ((TextBlock)window.FindName("NoShortcutResults")).Visibility);
            search.Text = "";
            Equal(20, window.ShortcutGroups.SelectMany(g => g.Shortcuts).Count());
            Equal(Visibility.Collapsed, ((TextBlock)window.FindName("NoShortcutResults")).Visibility);
            window.Width = 700;
            window.Height = 520;
            window.Measure(new Size(700, 520));
            window.Arrange(new Rect(0, 0, 700, 520));
            window.UpdateLayout();
            True(VisualChildren<TextBox>(scroll).Where(t => t.Tag is string).All(t => t.ActualWidth >= 125),
                "最小窗口下组合键输入区域被裁切");
            SaveSettingsSnapshot(window, "compact-shortcuts-minimum.png");
            ((TabControl)window.FindName("SettingsCategories")).SelectedIndex = 0;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            SaveSettingsSnapshot(window, "settings-history-limit.png");
            ((TabControl)window.FindName("SettingsCategories")).SelectedItem =
                window.FindName("AboutSettingsTab");
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            SaveSettingsSnapshot(window, "settings-about-icon.png");
        }
        finally { window.Close(); }
    }

    private static void FilteredShortcutEditing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "collector-shortcut-test-" + Guid.NewGuid().ToString("N"));
        var window = new SettingsWindow(new AppSettings { HotkeyEnabled = false, BoardStoragePath = directory });
        try
        {
            var allRows = window.ShortcutGroups.SelectMany(g => g.Shortcuts).ToArray();
            var search = (TextBox)window.FindName("ShortcutSearchBox");
            search.Text = "撤回";
            var row = window.ShortcutGroups.SelectMany(g => g.Shortcuts).Single();
            True(ReferenceEquals(row, allRows.Single(r => r.Id == BoardShortcutCatalog.Undo)), "搜索结果复制了快捷键而非原始绑定");
            var clear = typeof(SettingsWindow).GetMethod("OnClearBoardShortcutClick", PrivateInstance)!;
            clear.Invoke(window, new object[] { new Button { Tag = row.Id }, new RoutedEventArgs() });
            Equal("", row.Gesture);
            row.Gesture = "Ctrl+Alt+Z";
            search.Text = "图片";
            Equal(3, window.ShortcutGroups.SelectMany(g => g.Shortcuts).Count());
            var save = typeof(SettingsWindow).GetMethod("OnSaveClick", PrivateInstance)!;
            // Conflict detection must still see rows currently hidden by the search.
            var paste = allRows.Single(r => r.Id == BoardShortcutCatalog.Paste);
            var originalPaste = paste.Gesture;
            paste.Gesture = row.Gesture;
            save.Invoke(window, new object[] { window, new RoutedEventArgs() });
            True(window.ResultSettings is null, "筛选后未检测隐藏项的快捷键冲突");
            paste.Gesture = originalPaste;
            ((TextBox)window.FindName("UndoStepLimitInput")).Text = "42";
            ((Button)window.FindName("DisableAllShortcutsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try { save.Invoke(window, new object[] { window, new RoutedEventArgs() }); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException && window.ResultSettings is not null)
            { /* DialogResult cannot be assigned on a non-modal test window. */ }
            Equal(20, window.ResultSettings!.BoardShortcuts.Count);
            Equal("Ctrl+Alt+Z", window.ResultSettings.BoardShortcuts[BoardShortcutCatalog.Undo]);
            Equal(originalPaste, window.ResultSettings.BoardShortcuts[BoardShortcutCatalog.Paste]);
            Equal(42, window.ResultSettings.UndoStepLimit);
            True(!window.ResultSettings.BoardShortcutsEnabled, "禁用全部状态没有保存");
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
