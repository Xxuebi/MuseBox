using System.Windows;
using System.Windows.Controls;
using RichTextBox = System.Windows.Controls.RichTextBox;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static BoardElement[] LayoutElements(BoardWindow window) =>
        LiveImages(window).Cast<BoardElement>().Concat(LiveTexts(window)).Concat(LiveDrawings(window)).ToArray();

    private static void SeedLayoutElements(BoardWindow window, BoardRepository repository)
    {
        SeedImages(window, repository);
        LiveImages(window)[1].Rotation = 37;
        repository.UpdateItemsAsync(new[] { LiveImages(window)[1] }).GetAwaiter().GetResult();
        repository.AddTextItemsAsync(new[] { new BoardTextItem {
            Id = "layout-text", X = 440, Y = 70, Width = 170, Height = 75, Rotation = 25, ZIndex = 4,
            DocumentData = RichTextDocumentService.Save(RichTextDocumentService.CreateDefault()) } }).GetAwaiter().GetResult();
        var drawing = SampleDrawing(BoardDrawingKind.Rectangle);
        drawing.Id = "layout-drawing"; drawing.X = 60; drawing.Y = 520; drawing.ZIndex = 5; drawing.Rotation = 40;
        repository.AddDrawingItemsAsync(new[] { drawing }).GetAwaiter().GetResult();
        window.ReloadAsync().GetAwaiter().GetResult();
        ArrangeBoardSurface(window);
    }

    private static Rect LayoutBounds(BoardWindow window, BoardElement element) =>
        (Rect)typeof(BoardWindow).GetMethod("RotatedImageBounds",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { element })!;

    private static void LayoutGeometry()
    {
        var units = new[] {
            new BoardLayoutUnit("a", new Rect(-40, 20, 20, 30), 0),
            new BoardLayoutUnit("b", new Rect(30, 70, 40, 20), 1),
            new BoardLayoutUnit("c", new Rect(120, 160, 60, 50), 2) };
        foreach (var operation in new[] { BoardLayoutOperation.AlignLeft, BoardLayoutOperation.AlignRight,
                     BoardLayoutOperation.AlignTop, BoardLayoutOperation.AlignBottom })
        {
            var deltas = BoardLayoutService.Calculate(units, operation);
            foreach (var unit in units)
            {
                var bounds = unit.Bounds;
                bounds.Offset(deltas[unit.Id]);
                if (operation == BoardLayoutOperation.AlignLeft) { Equal(-40d, bounds.Left); Equal(unit.Bounds.Y, bounds.Y); }
                if (operation == BoardLayoutOperation.AlignRight) { Equal(180d, bounds.Right); Equal(unit.Bounds.Y, bounds.Y); }
                if (operation == BoardLayoutOperation.AlignTop) { Equal(20d, bounds.Top); Equal(unit.Bounds.X, bounds.X); }
                if (operation == BoardLayoutOperation.AlignBottom) { Equal(210d, bounds.Bottom); Equal(unit.Bounds.X, bounds.X); }
            }
        }
        foreach (var operation in new[] { BoardLayoutOperation.DistributeHorizontal, BoardLayoutOperation.DistributeVertical,
                     BoardLayoutOperation.ArrangeLeft, BoardLayoutOperation.ArrangeRight,
                     BoardLayoutOperation.ArrangeTop, BoardLayoutOperation.ArrangeBottom })
        {
            var horizontal = operation is BoardLayoutOperation.DistributeHorizontal
                or BoardLayoutOperation.ArrangeTop or BoardLayoutOperation.ArrangeBottom;
            var deltas = BoardLayoutService.Calculate(units, operation);
            var boxes = units.Select(unit => { var box = unit.Bounds; box.Offset(deltas[unit.Id]); return box; }).ToArray();
            Equal(18d, horizontal ? boxes[1].Left - boxes[0].Right : boxes[1].Top - boxes[0].Bottom, .001);
            Equal(18d, horizontal ? boxes[2].Left - boxes[1].Right : boxes[2].Top - boxes[1].Bottom, .001);
            Equal(horizontal ? units[0].Bounds.Left : units[0].Bounds.Top, horizontal ? boxes[0].Left : boxes[0].Top);
            foreach (var box in boxes)
            {
                if (operation == BoardLayoutOperation.DistributeHorizontal) Equal(115d, box.Top + box.Height / 2);
                if (operation == BoardLayoutOperation.DistributeVertical) Equal(70d, box.Left + box.Width / 2);
                if (operation == BoardLayoutOperation.ArrangeLeft) Equal(-40d, box.Left);
                if (operation == BoardLayoutOperation.ArrangeRight) Equal(180d, box.Right);
                if (operation == BoardLayoutOperation.ArrangeTop) Equal(20d, box.Top);
                if (operation == BoardLayoutOperation.ArrangeBottom) Equal(210d, box.Bottom);
            }
            var repeat = BoardLayoutService.Calculate(units.Select((unit, i) => unit with { Bounds = boxes[i] }).ToArray(), operation);
            True(repeat.Values.All(delta => delta.Length < .001), "重复等距排列产生了位移");
            var shuffled = BoardLayoutService.Calculate(units.Reverse().ToArray(), operation);
            foreach (var unit in units) Equal(deltas[unit.Id], shuffled[unit.Id]);
            var overlapping = units.Select(unit => unit with { Bounds = new Rect(0, 0, 100, 100) }).ToArray();
            deltas = BoardLayoutService.Calculate(overlapping.Reverse().ToArray(), operation);
            Equal(0d, horizontal ? deltas["a"].X : deltas["a"].Y);
            Equal(118d, horizontal ? deltas["b"].X : deltas["b"].Y);
            Equal(236d, horizontal ? deltas["c"].X : deltas["c"].Y);
            overlapping = overlapping.Select(unit => unit with { ZIndex = 0 }).ToArray();
            deltas = BoardLayoutService.Calculate(overlapping.Reverse().ToArray(), operation);
            Equal(0d, horizontal ? deltas["a"].X : deltas["a"].Y);
            Equal(118d, horizontal ? deltas["b"].X : deltas["b"].Y);
            Equal(236d, horizontal ? deltas["c"].X : deltas["c"].Y);
            Equal(2, BoardLayoutService.Calculate(units.Take(2).ToArray(), operation).Count);
            Equal(0, BoardLayoutService.Calculate(units.Take(1).ToArray(), operation).Count);
        }
        Equal(0, BoardLayoutService.Calculate(units.Take(1).ToArray(), BoardLayoutOperation.AlignLeft).Count);
        var auto = BoardLayoutService.Calculate(units, BoardLayoutOperation.AutoArrange);
        var arranged = units.Select(unit => { var box = unit.Bounds; box.Offset(auto[unit.Id]); return box; }).ToArray();
        Equal(-40d, arranged.Min(box => box.Left));
        Equal(20d, arranged.Min(box => box.Top));
        for (var i = 0; i < arranged.Length; i++)
            for (var j = i + 1; j < arranged.Length; j++)
                True(!arranged[i].IntersectsWith(arranged[j]), "自动排列存在重叠");
    }

    private static void LayoutMixedSelection()
    {
        foreach (var operation in Enum.GetValues<BoardLayoutOperation>())
            WithDrawingBoard((window, repository) =>
            {
                SeedLayoutElements(window, repository);
                var selected = new[] { "image-1", "layout-text", "layout-drawing" };
                BoardSelection(window).UnionWith(selected);
                var before = LayoutElements(window).ToDictionary(element => element.Id, element => element.CloneElement());
                var units = LayoutElements(window).Where(element => selected.Contains(element.Id))
                    .Select(element => new BoardLayoutUnit("element:" + element.Id, LayoutBounds(window, element), element.ZIndex)).ToArray();
                var expected = BoardLayoutService.Calculate(units, operation);
                var pan = GetViewportState(window);
                var history = UndoCount(window);
                var revision = repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision;
                AwaitDrawing(window, "ApplyLayoutAsync", operation);
                Equal(history + 1, UndoCount(window));
                Equal(pan, GetViewportState(window));
                var arrangedRevision = repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision;
                AwaitDrawing(window, "ApplyLayoutAsync", operation);
                Equal(history + 1, UndoCount(window));
                Equal(arrangedRevision, repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision);
                var after = LayoutElements(window).ToDictionary(element => element.Id, element => (element.X, element.Y));
                foreach (var element in LayoutElements(window))
                {
                    var original = before[element.Id];
                    var offset = selected.Contains(element.Id) ? expected["element:" + element.Id] : new Vector();
                    Equal(original.X + offset.X, element.X, .001); Equal(original.Y + offset.Y, element.Y, .001);
                    Equal((original.Width, original.Height, original.Rotation, original.ZIndex, original.GroupId),
                        (element.Width, element.Height, element.Rotation, element.ZIndex, element.GroupId));
                }
                True(repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision > revision, "排列未标记场景变化");
                AwaitDrawing(window, "UndoAsync");
                foreach (var element in LayoutElements(window))
                    Equal((before[element.Id].X, before[element.Id].Y), (element.X, element.Y));
                AwaitDrawing(window, "RedoAsync");
                window.ReloadAsync().GetAwaiter().GetResult();
                foreach (var element in LayoutElements(window)) Equal(after[element.Id], (element.X, element.Y));
            });
    }

    private static (double Zoom, double X, double Y) GetViewportState(BoardWindow window) =>
        ((double)typeof(BoardWindow).GetField("_viewZoom", PrivateInstance)!.GetValue(window)!,
         (double)typeof(BoardWindow).GetField("_viewPanX", PrivateInstance)!.GetValue(window)!,
         (double)typeof(BoardWindow).GetField("_viewPanY", PrivateInstance)!.GetValue(window)!);

    private static void LayoutWholeBoardAndNoOp() => WithDrawingBoard((window, repository) =>
    {
        SeedLayoutElements(window, repository);
        repository.DeleteItemsAsync(LiveImages(window).Select(image => image.Id).ToArray()).GetAwaiter().GetResult();
        window.ReloadAsync().GetAwaiter().GetResult();
        AwaitDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignLeft);
        var bounds = LayoutElements(window).Select(element => LayoutBounds(window, element)).ToArray();
        Equal(bounds[0].Left, bounds[1].Left, .001);
        var history = UndoCount(window);
        var revision = repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision;
        AwaitDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignLeft);
        Equal(history, UndoCount(window));
        Equal(revision, repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision);
        BoardSelection(window).Add("layout-text");
        AwaitDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignRight);
        Equal(history, UndoCount(window));
        BoardSelection(window).Clear();
        AwaitDrawing(window, "ArrangeImagesAsync");
        bounds = LayoutElements(window).Select(element => LayoutBounds(window, element)).ToArray();
        True(!bounds[0].IntersectsWith(bounds[1]), "无图片画板没有自动排列文字与绘制");
    });

    private static void LayoutNestedGroups() => WithDrawingBoard((window, repository) =>
    {
        SeedLayoutElements(window, repository);
        var elements = LayoutElements(window);
        var groups = new[] {
            new BoardGroup { Id = "outer", FramePadding = 25 },
            new BoardGroup { Id = "inner", ParentGroupId = "outer", FramePadding = 10 } };
        foreach (var element in elements)
            element.GroupId = element.Id is "image-0" or "image-1" ? "inner" :
                element.Id is "layout-text" or "layout-drawing" ? "outer" : "";
        BoardLayerTreeService.NormalizeZIndices(groups, elements);
        repository.ApplyLayerTreeAsync("A", groups, elements).GetAwaiter().GetResult();
        window.ReloadAsync().GetAwaiter().GetResult();
        var units = (Array)CallDrawing(window, "GetLayoutTargets")!;
        Equal(3, units.Length);
        foreach (var operation in Enum.GetValues<BoardLayoutOperation>())
        {
            var members = LayoutElements(window).Where(element => element.GroupId.Length > 0).ToArray();
            var before = members.ToDictionary(element => element.Id, element => (element.X, element.Y));
            AwaitDrawing(window, "ApplyLayoutAsync", operation);
            var delta = new Vector(members[0].X - before[members[0].Id].X, members[0].Y - before[members[0].Id].Y);
            foreach (var element in members)
            {
                Equal(before[element.Id].X + delta.X, element.X, .001);
                Equal(before[element.Id].Y + delta.Y, element.Y, .001);
            }
            AwaitDrawing(window, "UndoAsync");
        }
        var liveGroups = (List<BoardGroup>)typeof(BoardWindow).GetField("_groups", PrivateInstance)!.GetValue(window)!;
        var visible = (Rect)CallDrawing(window, "LayoutGroupBounds", "inner")!;
        liveGroups.Single(group => group.Id == "inner").BackgroundVisible = false;
        var hidden = (Rect)CallDrawing(window, "LayoutGroupBounds", "inner")!;
        Equal(20d, visible.Width - hidden.Width, .001);
        SetDrawingField(window, "_layerDirectSelectionActive", true);
        BoardSelection(window).Clear();
        BoardSelection(window).UnionWith(new[] { "image-0", "image-2" });
        var untouched = LayoutElements(window).Where(element => !BoardSelection(window).Contains(element.Id))
            .ToDictionary(element => element.Id, element => (element.X, element.Y));
        Equal(2, ((Array)CallDrawing(window, "GetLayoutTargets")!).Length);
        AwaitDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignTop);
        foreach (var element in LayoutElements(window).Where(element => untouched.ContainsKey(element.Id)))
            Equal(untouched[element.Id], (element.X, element.Y));
        CallDrawing(window, "SelectNestedGroupDirectly", "inner");
        Equal(1, ((Array)CallDrawing(window, "GetLayoutTargets")!).Length);
    });

    private static void LayoutAtomicFailure() => WithDrawingBoard((window, repository) =>
    {
        SeedLayoutElements(window, repository);
        AwaitDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignRight);
        AwaitDrawing(window, "UndoAsync");
        var before = LayoutElements(window).ToDictionary(element => element.Id, element => (element.X, element.Y));
        var revision = repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision;
        var connectionString = (string)typeof(BoardRepository).GetField("_connectionString", PrivateInstance)!.GetValue(repository)!;
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER layout_test_abort BEFORE UPDATE OF x,y ON text_items BEGIN SELECT RAISE(ABORT,'layout failure'); END;";
        command.ExecuteNonQuery();
        var rejected = false;
        try
        {
            repository.ApplyElementPositionsAsync("A", new[] {
                new BoardElementPosition("image-0", BoardElementKind.Image, 12, 34),
                new BoardElementPosition("layout-drawing", BoardElementKind.Drawing, 56, 78),
                new BoardElementPosition("layout-text", BoardElementKind.Text, 90, 12)
            }).GetAwaiter().GetResult();
        }
        catch (SqliteException) { rejected = true; }
        True(rejected, "未触发混合坐标事务中途失败");
        var history = UndoCount(window);
        var redo = (System.Collections.ICollection)typeof(BoardWindow).GetField("_redo", PrivateInstance)!.GetValue(window)!;
        Equal(1, redo.Count);
        AwaitDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignLeft);
        Equal(history, UndoCount(window));
        Equal(1, redo.Count);
        True(window.IsEnabled, "保存失败后画板仍被禁用");
        True(((TextBlock)window.FindName("BoardStatus")).Text.StartsWith("排列失败"), "未显示排列失败信息");
        foreach (var element in LayoutElements(window)) Equal(before[element.Id], (element.X, element.Y));
        Equal(revision, repository.CaptureSceneAsync("A").GetAwaiter().GetResult().Revision);
        window.ReloadAsync().GetAwaiter().GetResult();
        foreach (var element in LayoutElements(window)) Equal(before[element.Id], (element.X, element.Y));
    });

    private static void LayoutPendingEdits() => WithDrawingBoard((window, repository) =>
    {
        SeedLayoutElements(window, repository);
        var text = LiveTexts(window).Single();
        BoardSelection(window).UnionWith(new[] { text.Id, "image-0" });
        CallDrawing(window, "BeginTextEditing", text);
        var editor = (RichTextBox)typeof(BoardWindow).GetField("_activeTextEditor", PrivateInstance)!.GetValue(window)!;
        editor.Document.Blocks.Clear();
        editor.Document.Blocks.Add(new System.Windows.Documents.Paragraph(
            new System.Windows.Documents.Run("排列前必须提交的长文字，用于检查实际边界")));
        AwaitDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignRight);
        Equal(LayoutBounds(window, text).Right, LayoutBounds(window, LiveImages(window)[0]).Right, .001);
        var saved = repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single();
        True(RichTextDocumentService.PlainText(RichTextDocumentService.Load(saved.DocumentData)).Contains("排列前必须提交"),
            "排列没有提交编辑中的文字");
        CallDrawing(window, "SetToolMode", BoardToolMode.Pen);
        CallDrawing(window, "StartDrawing", new Point(-80, -90), .5d);
        CallDrawing(window, "UpdateDrawing", new Point(150, -50), .9d);
        AwaitDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignTop);
        True(typeof(BoardWindow).GetField("_previewDrawing", PrivateInstance)!.GetValue(window) is null,
            "排列遗漏最后一笔绘制");
        Equal(2, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Count);
        var top = LayoutElements(window).Min(element => LayoutBounds(window, element).Top);
        foreach (var element in LayoutElements(window)) Equal(top, LayoutBounds(window, element).Top, .001);
    });

    private static void LayoutBusyGuard() => WithDrawingBoard((window, repository) =>
    {
        SeedLayoutElements(window, repository);
        var gate = (SemaphoreSlim)typeof(BoardRepository).GetField("_gate", PrivateInstance)!.GetValue(repository)!;
        var before = UndoCount(window);
        PumpSceneTask(async () =>
        {
            gate.Wait();
            Task first;
            try
            {
                first = (Task)CallDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignLeft)!;
                True(!first.IsCompleted && !window.IsEnabled, "等待保存时未禁用编辑");
                await (Task)CallDrawing(window, "ApplyLayoutAsync", BoardLayoutOperation.AlignRight)!;
                Equal(before, UndoCount(window));
            }
            finally { gate.Release(); }
            await first;
            return true;
        });
        Equal(before + 1, UndoCount(window));
        True(window.IsEnabled, "排列结束未恢复交互");
    });

    private static void LayoutMenuAvailability() => WithDrawingBoard((window, repository) =>
    {
        var menu = (MenuItem)window.FindName("ArrangeMenuItem");
        CallDrawing(window, "UpdateLayoutMenu");
        True(!menu.IsEnabled, "空画板排列未禁用");
        Equal("排列", menu.Header);
        Equal(string.Join("|", new[] { "自动排列", "左对齐", "右对齐", "上对齐", "下对齐", "水平分布", "垂直分布" }),
            string.Join("|", menu.Items.OfType<MenuItem>().Select(item => item.Header)));
        SeedImages(window, repository);
        ChooseImages(window, 0, 1);
        CallDrawing(window, "UpdateLayoutMenu");
        True(menu.IsEnabled, "两元素无法排列");
        True(menu.Items.OfType<MenuItem>().Take(5).All(item => item.IsEnabled), "两元素对齐被禁用");
        True(menu.Items.OfType<MenuItem>().Skip(5).All(item => item.IsEnabled), "两元素胶片排列被禁用");
        var directions = new[] { ("左", "Left"), ("右", "Right"), ("上", "Top"), ("下", "Bottom") };
        foreach (var (direction, name) in directions)
        {
            var parent = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, direction + "对齐"));
            True(parent.Tag is null, "对齐父菜单不应执行操作");
            var children = parent.Items.OfType<MenuItem>().ToArray();
            Equal(2, children.Length);
            Equal(direction + "对齐", children[0].Header);
            Equal("Align" + name, children[0].Tag);
            Equal(direction + "等距排列", children[1].Header);
            Equal("Arrange" + name, children[1].Tag);
            True(children.All(item => item.IsEnabled), "两元素等距子菜单被禁用");
        }
        ChooseImages(window, 0);
        CallDrawing(window, "UpdateLayoutMenu");
        True(!menu.IsEnabled, "单选错误回退到全画板");
        True(menu.Items.OfType<MenuItem>().SelectMany(item => item.Items.OfType<MenuItem>())
            .All(item => !item.IsEnabled), "单选嵌套排列项未禁用");
        ChooseImages(window);
        CallDrawing(window, "UpdateLayoutMenu");
        True(menu.Items.OfType<MenuItem>().All(item => item.IsEnabled), "全画板菜单没有按单位计数");
    });
}
