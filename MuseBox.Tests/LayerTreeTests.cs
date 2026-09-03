using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.IO.Compression;
using System.Text.Json;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using MediaTextOptions = System.Windows.Media.TextOptions;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static void LayerTreeMoveAndCycleGuards()
    {
        var outer = new BoardGroup { Id = "outer", DrawerId = "A", LayerName = "外层组合" };
        var inner = new BoardGroup { Id = "inner", DrawerId = "A", ParentGroupId = outer.Id, LayerName = "内层组合" };
        var groups = new List<BoardGroup> { outer, inner };
        var front = new BoardDrawingItem { Id = "front", DrawerId = "A", ZIndex = 30, LayerName = "前景" };
        var first = new BoardDrawingItem { Id = "first", DrawerId = "A", GroupId = inner.Id, ZIndex = 20, LayerName = "组内前景" };
        var second = new BoardDrawingItem { Id = "second", DrawerId = "A", GroupId = inner.Id, ZIndex = 10, LayerName = "组内背景" };
        var elements = new BoardElement[] { front, first, second };

        var roots = BoardLayerTreeService.BuildTree(groups, elements);
        Equal("front", roots[0].Id);
        True(BoardLayerTreeService.MoveNode(groups, elements, front.Id, false, inner.Id, first.Id),
            "根图层没有拖入嵌套组合");
        Equal(inner.Id, front.GroupId);
        var nested = BoardLayerTreeService.BuildTree(groups, elements).Single().Children.Single().Children;
        Equal("front", nested[0].Id);
        True(front.ZIndex > first.ZIndex && first.ZIndex > second.ZIndex, "同级拖放没有展平为连续层级");
        True(!BoardLayerTreeService.MoveNode(groups, elements, outer.Id, true, inner.Id, null),
            "组合被允许拖入自身后代并形成循环");
        Equal(string.Empty, outer.ParentGroupId);
    }

    private static void LayerTreeDepthLimit()
    {
        var groups = Enumerable.Range(1, BoardLayerTreeService.MaxDepth).Select(index => new BoardGroup
        {
            Id = $"g{index}", DrawerId = "A", ParentGroupId = index == 1 ? string.Empty : $"g{index - 1}",
            LayerName = $"组合 {index}"
        }).ToList();
        var element = new BoardDrawingItem { Id = "leaf", DrawerId = "A", GroupId = $"g{BoardLayerTreeService.MaxDepth}", LayerName = "叶节点" };
        BoardLayerTreeService.Validate(groups, new[] { element });
        groups.Add(new BoardGroup { Id = "too-deep", DrawerId = "A", ParentGroupId = $"g{BoardLayerTreeService.MaxDepth}", LayerName = "超深" });
        element.GroupId = "too-deep";
        var rejected = false;
        try { BoardLayerTreeService.Validate(groups, new[] { element }); }
        catch (InvalidDataException) { rejected = true; }
        True(rejected, "超过 32 层的组合没有被拒绝");
    }

    private static void LayerRepositoryPersistsNestedHierarchy()
    {
        var directory = CreateTempDirectory();
        try
        {
            var repository = new BoardRepository(new AppDataPaths(directory));
            repository.InitializeAsync().GetAwaiter().GetResult();
            var text = new BoardTextItem { Id = "layer-text", DrawerId = "A", LayerName = "文字说明", ZIndex = 1 };
            var drawing = new BoardDrawingItem { Id = "layer-drawing", DrawerId = "A", LayerName = "绘制箭头",
                Kind = BoardDrawingKind.Arrow, PointsJson = "[]", ZIndex = 2 };
            repository.AddTextItemsAsync(new[] { text }).GetAwaiter().GetResult();
            repository.AddDrawingItemsAsync(new[] { drawing }).GetAwaiter().GetResult();
            var outer = new BoardGroup { Id = "persist-outer", DrawerId = "A", LayerName = "项目",
                BackgroundColor = "#80445566", Locked = false };
            var inner = new BoardGroup { Id = "persist-inner", DrawerId = "A", ParentGroupId = outer.Id,
                LayerName = "注释", BorderThickness = 8 };
            text.GroupId = inner.Id;
            drawing.GroupId = outer.Id;
            BoardLayerTreeService.NormalizeZIndices(new[] { outer, inner }, new BoardElement[] { text, drawing });
            repository.ApplyLayerTreeAsync("A", new[] { outer, inner }, new BoardElement[] { text, drawing })
                .GetAwaiter().GetResult();

            var savedGroups = repository.GetGroupsAsync("A").GetAwaiter().GetResult();
            Equal(2, savedGroups.Count);
            Equal(outer.Id, savedGroups.Single(group => group.Id == inner.Id).ParentGroupId);
            var savedText = repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single();
            var savedDrawing = repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single();
            Equal("文字说明", savedText.LayerName);
            Equal(inner.Id, savedText.GroupId);
            Equal(outer.Id, savedDrawing.GroupId);
            Equal(inner.BorderThickness, savedText.GroupBorderThickness);
            Equal(outer.BackgroundColor, savedDrawing.GroupBackgroundColor);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void SceneV1AndV2LayerMigration()
    {
        var legacy = new SceneDocument { Version = 1, Name = "旧场景" };
        legacy.Drawings.Add(new BoardDrawingItem
        {
            Id = "legacy-drawing", DrawerId = "A", GroupId = "legacy-group", LayerName = string.Empty,
            Kind = BoardDrawingKind.Rectangle, PointsJson = "[]", GroupBackgroundColor = "#80445566",
            GroupBorderColor = "#FF12A0E0", GroupLocked = false
        });
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "legacy-v1.mubo");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var stream = archive.CreateEntry("scene.json").Open())
                JsonSerializer.Serialize(stream, legacy);
            using var prepared = SceneFileService.ReadAsync(path).GetAwaiter().GetResult();
            Equal(2, prepared.Document.Version);
            Equal("legacy-group", prepared.Document.Groups.Single().Id);
        }
        finally { Directory.Delete(directory, true); }
        SceneValidation.Validate(legacy);
        Equal(2, legacy.Version);
        Equal(1, legacy.Groups.Count);
        Equal("legacy-group", legacy.Groups[0].Id);
        Equal("#80445566", legacy.Groups[0].BackgroundColor);
        True(legacy.Drawings[0].LayerName.Length > 0, "旧内容没有生成默认图层名");

        var outer = new BoardGroup { Id = "scene-outer", LayerName = "外层" };
        legacy.Groups[0].ParentGroupId = outer.Id;
        legacy.Groups.Add(outer);
        SceneValidation.Validate(legacy);
        Equal(outer.Id, legacy.Groups.Single(group => group.Id == "legacy-group").ParentGroupId);
        outer.ParentGroupId = "legacy-group";
        var rejected = false;
        try { SceneValidation.Validate(legacy); }
        catch (InvalidDataException) { rejected = true; }
        True(rejected, "场景 v2 的组合循环没有被拒绝");

        outer.ParentGroupId = string.Empty;
        outer.Id = legacy.Drawings[0].Id;
        legacy.Groups[0].ParentGroupId = outer.Id;
        rejected = false;
        try { SceneValidation.Validate(legacy); }
        catch (InvalidDataException) { rejected = true; }
        True(rejected, "组合与元素之间的重复 ID 没有被拒绝");
    }

    private static void LayerPanelOverlayAndSelectionState() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        var before = ViewportValues(window);
        var button = (ToggleButton)window.FindName("LayersButton");
        var panel = (Border)window.FindName("LayersPanel");
        var list = (ListBox)window.FindName("LayersList");
        True(button.IsChecked != true && panel.Visibility == Visibility.Collapsed, "图层面板默认状态错误");
        button.IsChecked = true;
        window.UpdateLayout();
        True(panel.Visibility == Visibility.Visible && panel.IsHitTestVisible, "图层面板没有作为画板内覆盖层打开");
        True(panel.Width is >= 260 and <= 320, "图层面板没有遵守自适应宽度限制");
        True(panel.Effect is null && panel.UseLayoutRounding && panel.SnapsToDevicePixels &&
            MediaTextOptions.GetTextFormattingMode(panel) == System.Windows.Media.TextFormattingMode.Display,
            "图层文字仍会跟随父级阴影离屏栅格化或落在半像素上");
        Equal(4, list.Items.Count);
        Equal(before, ViewportValues(window));
        var oldWidth = panel.Width;
        CallDrawing(window, "OnLayersResizeDragDelta", null!, new DragDeltaEventArgs(-80, 0));
        True(panel.Width > oldWidth, "图层面板左边缘无法拖动增加宽度");
        CallDrawing(window, "OnLayersResizeDoubleClick", null!, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        True(panel.Width <= 320, "双击图层宽度手柄没有恢复默认宽度");
        button.IsChecked = false;
        True(!panel.IsHitTestVisible, "图层面板关闭动画期间仍会拦截画板输入");
    });

    private static void LayerPanelShiftChildAndFocusSelection() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        ((ToggleButton)window.FindName("LayersButton")).IsChecked = true;
        window.UpdateLayout();
        var rows = (System.Collections.IList)typeof(BoardWindow).GetField("_layerRows", PrivateInstance)!.GetValue(window)!;
        var first = rows[0]!;
        var second = rows[1]!;
        CallDrawing(window, "SelectLayerRow", first, false, false, false);
        CallDrawing(window, "SelectLayerRow", second, false, false, true);
        Equal(2, BoardSelection(window).Count);
        var firstCorners = (CornerRadius)first.GetType().GetProperty("SelectionCornerRadius")!.GetValue(first)!;
        var secondCorners = (CornerRadius)second.GetType().GetProperty("SelectionCornerRadius")!.GetValue(second)!;
        Equal(0d, firstCorners.BottomLeft);
        Equal(0d, secondCorners.TopLeft);

        var before = ViewportValues(window);
        CallDrawing(window, "SelectLayerRow", first, true, false, false);
        var focused = ViewportValues(window);
        Equal(before.Zoom, focused.Zoom);
        True(before.PanX != focused.PanX || before.PanY != focused.PanY,
            "双击图片图层没有在保持缩放的情况下居中图片");
        CallDrawing(window, "SelectLayerRow", first, true, false, false);
        Equal(before, ViewportValues(window));

        ChooseImages(window, 0, 1);
        AwaitDrawing(window, "GroupImagesAsync");
        rows = (System.Collections.IList)typeof(BoardWindow).GetField("_layerRows", PrivateInstance)!.GetValue(window)!;
        var groupRow = rows.Cast<object>().Single(row => (bool)row.GetType().GetProperty("IsGroup")!.GetValue(row)!);
        var childRow = rows.Cast<object>().First(row => !(bool)row.GetType().GetProperty("IsGroup")!.GetValue(row)! &&
            (string)row.GetType().GetProperty("ParentGroupId")!.GetValue(row)! != string.Empty);
        CallDrawing(window, "SelectLayerRow", childRow, false, false, false);
        Equal(1, BoardSelection(window).Count);
        CallDrawing(window, "SelectLayerRow", groupRow, false, false, false);
        Equal(2, BoardSelection(window).Count);
        CallDrawing(window, "SelectLayerRow", childRow, false, true, false);
        Equal(1, BoardSelection(window).Count);
        CallDrawing(window, "SelectLayerRow", groupRow, false, false, false);
        Equal(2, BoardSelection(window).Count);

        var expandedCount = rows.Count;
        var expandButton = new Button { Name = "ExpandButton", Tag = groupRow };
        CallDrawing(window, "OnLayerExpandClick", expandButton, new RoutedEventArgs(Button.ClickEvent));
        rows = (System.Collections.IList)typeof(BoardWindow).GetField("_layerRows", PrivateInstance)!.GetValue(window)!;
        Equal(expandedCount - 2, rows.Count);
    });

    private static void NestedGroupBackgroundsStayBehindElements() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        ChooseImages(window, 0, 1);
        AwaitDrawing(window, "GroupImagesAsync");
        BoardSelection(window).Add("image-2");
        CallDrawing(window, "UpdateSelectionVisuals");
        AwaitDrawing(window, "GroupImagesAsync");
        var groups = (List<BoardGroup>)typeof(BoardWindow).GetField("_groups", PrivateInstance)!.GetValue(window)!;
        Equal(2, groups.Count);
        var outer = groups.Single(group => group.ParentGroupId.Length == 0);
        var inner = groups.Single(group => group.ParentGroupId == outer.Id);
        CallDrawing(window, "RefreshLayersPanel");
        var rows = (System.Collections.IList)typeof(BoardWindow).GetField("_layerRows", PrivateInstance)!.GetValue(window)!;
        var innerRow = rows.Cast<object>().Single(row =>
            (bool)row.GetType().GetProperty("IsGroup")!.GetValue(row)! &&
            (string)row.GetType().GetProperty("Id")!.GetValue(row)! == inner.Id);
        CallDrawing(window, "SelectLayerRow", innerRow, false, false, false);
        Equal(2, BoardSelection(window).Count);
        Equal(inner.Id, (string)typeof(BoardWindow).GetField("_explicitSelectedGroupId", PrivateInstance)!.GetValue(window)!);
        var visuals = (Dictionary<string, Border>)typeof(BoardWindow).GetField("_groupVisuals", PrivateInstance)!.GetValue(window)!;
        var outerZ = Panel.GetZIndex(visuals[outer.Id]);
        var innerZ = Panel.GetZIndex(visuals[inner.Id]);
        True(outerZ < innerZ && innerZ < 0, "嵌套组合背景层级没有保持外层在后、内层在前");
        True(visuals.Values.All(background => Panel.GetZIndex(background) < 0),
            "组合背景仍可能覆盖图片、文字或绘制元素");
    });

    private static void NestedGroupDrillDownAndDragSelection() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        ChooseImages(window, 0, 1);
        AwaitDrawing(window, "GroupImagesAsync");
        BoardSelection(window).Add("image-2");
        CallDrawing(window, "UpdateSelectionVisuals");
        AwaitDrawing(window, "GroupImagesAsync");

        var groups = (List<BoardGroup>)typeof(BoardWindow).GetField("_groups", PrivateInstance)!.GetValue(window)!;
        var outer = groups.Single(group => group.ParentGroupId.Length == 0);
        var inner = groups.Single(group => group.ParentGroupId == outer.Id);
        CallDrawing(window, "RefreshLayersPanel");
        var rows = (System.Collections.IList)typeof(BoardWindow).GetField("_layerRows", PrivateInstance)!.GetValue(window)!;
        object GroupRow(string id) => rows.Cast<object>().Single(row =>
            (bool)row.GetType().GetProperty("IsGroup")!.GetValue(row)! &&
            (string)row.GetType().GetProperty("Id")!.GetValue(row)! == id);

        CallDrawing(window, "SelectLayerRow", GroupRow(inner.Id), false, false, false);
        var viewBeforeGroupFocus = ViewportValues(window);
        CallDrawing(window, "SelectLayerRow", GroupRow(inner.Id), true, false, false);
        var groupFocusedView = ViewportValues(window);
        var groupBounds = (Rect)CallDrawing(window, "GroupBounds", inner.Id, null!, true)!;
        var boardSurface = (Grid)window.FindName("BoardSurface");
        Equal(viewBeforeGroupFocus.Zoom, groupFocusedView.Zoom);
        Equal(boardSurface.ActualWidth / 2, (groupBounds.X + groupBounds.Width / 2) * groupFocusedView.Zoom + groupFocusedView.PanX, .001);
        Equal(boardSurface.ActualHeight / 2, (groupBounds.Y + groupBounds.Height / 2) * groupFocusedView.Zoom + groupFocusedView.PanY, .001);
        CallDrawing(window, "SelectLayerRow", GroupRow(inner.Id), true, false, false);
        Equal(viewBeforeGroupFocus, ViewportValues(window));
        var member = LiveImages(window).First(image => image.GroupId == inner.Id);
        var dragUnit = ((IEnumerable<BoardElement>)CallDrawing(window, "ImageSelectionUnit", member)!).ToArray();
        Equal(2, dragUnit.Length);
        True(dragUnit.All(element => element.GroupId == inner.Id), "内层组合拖动仍被提升到了最外层组合");

        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        CallDrawing(window, "OnGroupBackgroundMouseDown", inner.Id, down);
        Equal(2, BoardSelection(window).Count);
        Equal(inner.Id, (string)typeof(BoardWindow).GetField("_explicitSelectedGroupId", PrivateInstance)!.GetValue(window)!);
        CallDrawing(window, "OnSurfaceMouseUp", window.FindName("BoardSurface"),
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseUpEvent });

        CallDrawing(window, "SelectLayerRow", GroupRow(outer.Id), false, false, false);
        ClickBoardItem(window, member, 2);
        Equal(inner.Id, (string)typeof(BoardWindow).GetField("_explicitSelectedGroupId", PrivateInstance)!.GetValue(window)!);
        Equal(2, BoardSelection(window).Count);
        ClickBoardItem(window, member, 2);
        Equal(1, BoardSelection(window).Count);
        True(BoardSelection(window).Contains(member.Id), "第二次双击没有进入内层组合的具体元素");
    });

    private static void NonImageLayerRowsCenterWithoutZoom() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        var text = new BoardTextItem
        {
            Id = "layer-focus-text", DrawerId = "A", X = 520, Y = 330, Width = 190, Height = 80,
            DocumentData = RichTextDocumentService.Save(RichTextDocumentService.CreateDefault())
        };
        var drawing = SampleDrawing(BoardDrawingKind.Rectangle);
        drawing.Id = "layer-focus-drawing";
        drawing.X = 610;
        drawing.Y = 430;
        repository.AddTextItemsAsync(new[] { text }).GetAwaiter().GetResult();
        repository.AddDrawingItemsAsync(new[] { drawing }).GetAwaiter().GetResult();
        window.ReloadAsync().GetAwaiter().GetResult();
        var surface = ArrangeBoardSurface(window);
        CallDrawing(window, "RefreshLayersPanel");
        var rows = (System.Collections.IList)typeof(BoardWindow).GetField("_layerRows", PrivateInstance)!.GetValue(window)!;

        foreach (var element in new BoardElement[]
                 {
                     ((List<BoardTextItem>)typeof(BoardWindow).GetField("_textItems", PrivateInstance)!.GetValue(window)!).Single(item => item.Id == text.Id),
                     LiveDrawings(window).Single(item => item.Id == drawing.Id)
                 })
        {
            var row = rows.Cast<object>().Single(candidate =>
                (string)candidate.GetType().GetProperty("Id")!.GetValue(candidate)! == element.Id);
            var before = ViewportValues(window);
            CallDrawing(window, "SelectLayerRow", row, true, false, false);
            var focused = ViewportValues(window);
            Equal(before.Zoom, focused.Zoom);
            Equal(surface.ActualWidth / 2, (element.X + element.Width / 2) * focused.Zoom + focused.PanX, .001);
            Equal(surface.ActualHeight / 2, (element.Y + element.Height / 2) * focused.Zoom + focused.PanY, .001);
            CallDrawing(window, "SelectLayerRow", row, true, false, false);
            Equal(before, ViewportValues(window));
        }
    });

    private static void ClipboardLayerNamesIncludeDates()
    {
        var created = new DateTime(2026, 9, 3, 4, 5, 6, DateTimeKind.Local).ToUniversalTime();
        var legacy = new BoardItem { LayerName = "剪贴板图片", CreatedUtc = created };
        BoardLayerNameService.EnsureNames(new[] { legacy }, Array.Empty<BoardGroup>());
        Equal("剪贴板图片 2026-09-03 04-05-06", legacy.LayerName);
        True(BoardLayerNameService.ClipboardName("网页复制图片", created).EndsWith("2026-09-03 04-05-06"),
            "剪贴板来源名称没有追加创建日期");
    }
}
