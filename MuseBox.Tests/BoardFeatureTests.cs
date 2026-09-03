using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static List<BoardItem> LiveImages(BoardWindow window) =>
        (List<BoardItem>)typeof(BoardWindow).GetField("_items", PrivateInstance)!.GetValue(window)!;

    private static void SeedImages(BoardWindow window, BoardRepository repository)
    {
        repository.UpsertAssetAsync(new AssetRecord("feature-asset", "feature-hash", ".png", "feature-missing.png", 200, 100, DateTime.UtcNow))
            .GetAwaiter().GetResult();
        repository.AddItemsAsync(Enumerable.Range(0, 4).Select(i => new BoardItem
        { Id = "image-" + i, DrawerId = "A", AssetId = "feature-asset", X = 100 + i * 240, Y = 180 + i * 90,
            Width = 180, Height = 100, ZIndex = i }).ToArray()).GetAwaiter().GetResult();
        window.ReloadAsync().GetAwaiter().GetResult();
        ArrangeBoardSurface(window);
    }

    private static void ChooseImages(BoardWindow window, params int[] indices)
    {
        BoardSelection(window).Clear();
        foreach (var index in indices) BoardSelection(window).Add("image-" + index);
        CallDrawing(window, "UpdateSelectionVisuals");
    }

    private static void ImageGroupingLifecycle() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        ChooseImages(window, 0);
        AwaitDrawing(window, "GroupImagesAsync");
        Equal(0, UndoCount(window));
        ChooseImages(window, 0, 1);
        AwaitDrawing(window, "GroupImagesAsync");
        var group = LiveImages(window).Single(x => x.Id == "image-0").GroupId;
        True(group.Length > 0, "没有创建组标识");
        Equal(group, LiveImages(window).Single(x => x.Id == "image-1").GroupId);
        Equal("", LiveImages(window).Single(x => x.Id == "image-2").GroupId);
        LiveImages(window).Single(x => x.Id == "image-0").Rotation = 30;
        LiveImages(window).Single(x => x.Id == "image-1").Rotation = 30;
        ChooseImages(window, 0);
        Equal(2, BoardSelection(window).Count);
        var groupFrame = (System.Windows.Shapes.Rectangle)window.FindName("GroupSelectionRectangle");
        Equal(30d, ((RotateTransform)groupFrame.RenderTransform).Angle);
        AwaitDrawing(window, "UndoAsync");
        True(LiveImages(window).All(x => x.GroupId.Length == 0), "撤回未移除分组");
        True(((MenuItem)window.FindName("RedoMenuItem")).IsEnabled, "重做菜单未启用");
        AwaitDrawing(window, "RedoAsync");
        window.ReloadAsync().GetAwaiter().GetResult();
        Equal(group, LiveImages(window).Single(x => x.Id == "image-0").GroupId);
        ChooseImages(window, 1);
        var positions = LiveImages(window).ToDictionary(x => x.Id, x => (x.X, x.Y));
        AwaitDrawing(window, "UngroupImagesAsync");
        True(LiveImages(window).All(x => x.GroupId.Length == 0), "未解散全部成员");
        foreach (var image in LiveImages(window)) Equal(positions[image.Id], (image.X, image.Y));
        AwaitDrawing(window, "UndoAsync");
        Equal(group, LiveImages(window).Single(x => x.Id == "image-0").GroupId);
        var persisted = repository.GetItemsAsync("A").GetAwaiter().GetResult();
        Equal(group, persisted.Single(x => x.Id == "image-1").GroupId);
        ChooseImages(window, 0);
        AwaitDrawing(window, "DeleteSelectedAsync");
        Equal(2, LiveImages(window).Count);
        AwaitDrawing(window, "UndoAsync");
        Equal(4, LiveImages(window).Count);
        Equal(2, LiveImages(window).Count(x => x.GroupId == group));
    });

    private static void ImageGroupPresentationAndMembership() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        ChooseImages(window, 0, 1);
        AwaitDrawing(window, "GroupImagesAsync");
        var members = LiveImages(window).Where(x => x.GroupId.Length > 0).ToArray();
        var groupId = members[0].GroupId;
        True(members.All(x => x.GroupLocked && x.GroupBackgroundVisible), "组合没有默认锁定并显示背景");
        Equal("#52FFFFFF", members[0].GroupBackgroundColor);
        Equal("#807A7A7A", members[0].GroupBorderColor);
        Equal(1.2, members[0].GroupBorderThickness);
        var backgrounds = (Dictionary<string, Border>)typeof(BoardWindow)
            .GetField("_groupVisuals", PrivateInstance)!.GetValue(window)!;
        True(backgrounds.TryGetValue(groupId, out var background) && background.Visibility == Visibility.Visible,
            "组合后没有生成背景");
        var groupBackground = background!;
        var palette = (Border)window.FindName("GroupPalette");
        Equal(Visibility.Visible, palette.Visibility);
        Equal(3d, ((Border)window.FindName("GroupBackgroundColorPreview")).Height);
        Equal(3d, ((Border)window.FindName("GroupBorderColorPreview")).Height);
        Equal("锁定后选中组合内任意元素将选择整个组，双击某一个元素可以临时选中单个元素",
            ((Button)window.FindName("GroupLockButton")).ToolTip?.ToString() ?? string.Empty);
        SaveDrawingTestVisual((FrameworkElement)window.FindName("BoardSurface"), "group-toolbar-background.png", false);
        var oldLeft = Canvas.GetLeft(groupBackground);
        var oldWidth = groupBackground.Width;
        members[0].X -= 90;
        CallDrawing(window, "UpdateItemVisual", members[0]);
        True(Canvas.GetLeft(groupBackground) < oldLeft && groupBackground.Width > oldWidth,
            "移动单个成员时组合背景没有实时扩展");

        ChooseImages(window, 0);
        Equal(2, BoardSelection(window).Count);
        ClickBoardItem(window, LiveImages(window).Single(x => x.Id == "image-0"), 2);
        Equal(1, BoardSelection(window).Count);
        True(BoardSelection(window).Contains("image-0"), "双击没有无视锁定选中单个成员");
        var beforeFocus = ViewportValues(window);
        ClickBoardItem(window, LiveImages(window).Single(x => x.Id == "image-0"), 2);
        Equal("image-0", typeof(BoardWindow).GetField("_focusedImageId", PrivateInstance)!.GetValue(window)!.ToString()!);
        True(ViewportValues(window) != beforeFocus, "锁定组合内再次双击没有聚焦元素");
        ClickBoardItem(window, LiveImages(window).Single(x => x.Id == "image-0"), 2);
        Equal(beforeFocus, ViewportValues(window));
        True(typeof(BoardWindow).GetField("_focusedImageId", PrivateInstance)!.GetValue(window) is null,
            "再次双击没有退出聚焦");

        var editableGroup = ((List<BoardGroup>)typeof(BoardWindow).GetField("_groups", PrivateInstance)!
            .GetValue(window)!).Single(group => group.Id == groupId);
        editableGroup.Locked = false;
        BoardLayerTreeService.SyncLegacyPresentation(new[] { editableGroup }, members);
        repository.UpdateItemsAsync(members).GetAwaiter().GetResult();
        ChooseImages(window, 0);
        Equal(1, BoardSelection(window).Count);
        BoardSelection(window).UnionWith(members.Select(x => x.Id));
        CallDrawing(window, "UpdateSelectionVisuals");
        Equal(Visibility.Visible, palette.Visibility);
        members = LiveImages(window).Where(x => x.GroupId == groupId).ToArray();
        var liveGroup = editableGroup;
        liveGroup.BackgroundColor = "#80445566";
        liveGroup.BorderColor = "#FF12A0E0";
        liveGroup.AutoMembership = true;
        CallDrawing(window, "BeginGroupBorderThicknessEdit");
        CallDrawing(window, "PreviewGroupBorderThickness", 6.5d);
        AwaitDrawing(window, "CompleteGroupBorderThicknessEditAsync");
        repository.UpdateItemsAsync(members).GetAwaiter().GetResult();
        CallDrawing(window, "UpdateGroupVisuals");
        Equal(6.5, groupBackground.BorderThickness.Left);

        var outsider = LiveImages(window).Single(x => x.Id == "image-2");
        var targetBounds = (Rect)CallDrawing(window, "GroupBounds", groupId, null!, true)!;
        outsider.X = targetBounds.Left + 20;
        outsider.Y = targetBounds.Top + 20;
        BoardSelection(window).Clear();
        BoardSelection(window).Add(outsider.Id);
        CallDrawing(window, "UpdateItemVisual", outsider);
        CallDrawing(window, "EvaluateGroupMembershipDrop");
        Equal(groupId, (string)typeof(BoardWindow).GetField("_pendingMembershipGroupId", PrivateInstance)!.GetValue(window)!);
        Equal("松手加入组合", ((TextBlock)window.FindName("GroupDropHintText")).Text);
        True((bool)CallDrawing(window, "ApplyPendingGroupMembership")!, "松手没有加入组合");
        Equal(groupId, outsider.GroupId);
        Equal("#80445566", outsider.GroupBackgroundColor);
        Equal("#FF12A0E0", outsider.GroupBorderColor);
        Equal(6.5, outsider.GroupBorderThickness);

        BoardSelection(window).Clear();
        BoardSelection(window).Add(outsider.Id);
        CallDrawing(window, "PrepareGroupMembershipDrag");
        outsider.X = targetBounds.Right + 180;
        outsider.Y = targetBounds.Bottom + 180;
        CallDrawing(window, "UpdateItemVisual", outsider);
        CallDrawing(window, "EvaluateGroupMembershipDrop");
        Equal(groupId, (string)typeof(BoardWindow).GetField("_pendingRemovalGroupId", PrivateInstance)!.GetValue(window)!);
        Equal("松手移出组合", ((TextBlock)window.FindName("GroupDropHintText")).Text);
        True((bool)CallDrawing(window, "ApplyPendingGroupMembership")!, "松手没有移出组合");
        Equal(string.Empty, outsider.GroupId);
        repository.UpdateItemsAsync(new[] { outsider }).GetAwaiter().GetResult();
        var saved = repository.GetItemsAsync("A").GetAwaiter().GetResult();
        Equal(string.Empty, saved.Single(x => x.Id == outsider.Id).GroupId);
        True(saved.Where(x => x.GroupId == groupId).All(x => x.GroupBackgroundColor == "#80445566" &&
            x.GroupBorderColor == "#FF12A0E0" && x.GroupBorderThickness == 6.5 &&
            x.GroupAutoMembership && !x.GroupLocked),
            "组合外观或收纳设置没有持久化");
    });

    private static void MixedElementGroupingAndBorderMetrics() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        var text = new BoardTextItem
        {
            Id = "mixed-text", DrawerId = "A", X = 430, Y = 70, Width = 210, Height = 90,
            DocumentData = RichTextDocumentService.Save(RichTextDocumentService.CreateDefault())
        };
        var drawing = SampleDrawing(BoardDrawingKind.Rectangle);
        drawing.Id = "mixed-drawing";
        drawing.X = 60;
        drawing.Y = 390;
        repository.AddTextItemsAsync(new[] { text }).GetAwaiter().GetResult();
        repository.AddDrawingItemsAsync(new[] { drawing }).GetAwaiter().GetResult();
        window.ReloadAsync().GetAwaiter().GetResult();
        ArrangeBoardSurface(window);

        var liveText = ((List<BoardTextItem>)typeof(BoardWindow).GetField("_textItems", PrivateInstance)!
            .GetValue(window)!).Single(x => x.Id == text.Id);
        var liveDrawing = ((List<BoardDrawingItem>)typeof(BoardWindow).GetField("_drawingItems", PrivateInstance)!
            .GetValue(window)!).Single(x => x.Id == drawing.Id);
        var image = LiveImages(window)[0];
        BoardSelection(window).Clear();
        BoardSelection(window).UnionWith(new[] { image.Id, liveText.Id, liveDrawing.Id });
        CallDrawing(window, "UpdateSelectionVisuals");
        AwaitDrawing(window, "GroupImagesAsync");

        var groupId = image.GroupId;
        True(groupId.Length > 0 && liveText.GroupId == groupId && liveDrawing.GroupId == groupId,
            "图片、文字和绘制对象没有进入同一组合");
        var backgrounds = (Dictionary<string, Border>)typeof(BoardWindow)
            .GetField("_groupVisuals", PrivateInstance)!.GetValue(window)!;
        True(backgrounds.ContainsKey(groupId), "混合组合没有生成背景");
        True(window.FindName("GroupBorderColorButton") is Button && window.FindName("GroupBorderOptionsButton") is Button,
            "组合边框没有拆分为选色主按钮和参数小按钮");
        var groupFillIcon = ((Grid)((Button)window.FindName("GroupBackgroundColorButton")).Content)
            .Children.OfType<System.Windows.Shapes.Path>().Single().Data.ToString();
        var drawingFillIcon = ((Grid)((Button)window.FindName("DrawingFillColorButton")).Content)
            .Children.OfType<System.Windows.Shapes.Path>().Single().Data.ToString();
        Equal(drawingFillIcon, groupFillIcon);

        CallDrawing(window, "BeginGroupBorderThicknessEdit");
        CallDrawing(window, "PreviewGroupBorderThickness", 0d);
        CallDrawing(window, "PreviewGroupFramePadding", 0d);
        Equal(0d, backgrounds[groupId].BorderThickness.Left);
        var zeroRaw = (Rect)CallDrawing(window, "GroupBounds", groupId, null!, false)!;
        var zeroPadded = (Rect)CallDrawing(window, "GroupBounds", groupId, null!, true)!;
        Equal(zeroRaw, zeroPadded);
        CallDrawing(window, "PreviewGroupBorderThickness", 40d);
        CallDrawing(window, "PreviewGroupFramePadding", 180d);
        AwaitDrawing(window, "CompleteGroupBorderThicknessEditAsync");
        Equal(24d, ((Slider)window.FindName("GroupBorderThicknessSlider")).Value);
        Equal(120d, ((Slider)window.FindName("GroupFramePaddingSlider")).Value);
        Equal("40", ((TextBox)window.FindName("GroupBorderThicknessText")).Text);
        Equal("180", ((TextBox)window.FindName("GroupFramePaddingText")).Text);
        Equal(40d, backgrounds[groupId].BorderThickness.Left);
        var raw = (Rect)CallDrawing(window, "GroupBounds", groupId, null!, false)!;
        var padded = (Rect)CallDrawing(window, "GroupBounds", groupId, null!, true)!;
        Equal(raw.Width + 360, padded.Width, .001);
        Equal(raw.Height + 360, padded.Height, .001);

        var savedImage = repository.GetItemsAsync("A").GetAwaiter().GetResult().Single(x => x.Id == image.Id);
        var savedText = repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single(x => x.Id == text.Id);
        var savedDrawing = repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single(x => x.Id == drawing.Id);
        foreach (var element in new BoardElement[] { savedImage, savedText, savedDrawing })
            True(element.GroupId == groupId && element.GroupBorderThickness == 40 && element.GroupFramePadding == 180,
                "混合组合参数没有写入全部元素类型");

        BoardSelection(window).Clear();
        ClickBoardItem(window, liveDrawing, 1);
        Equal(3, BoardSelection(window).Count);
        ClickBoardItem(window, liveText, 2);
        Equal(1, BoardSelection(window).Count);
        True(BoardSelection(window).Contains(liveText.Id), "锁定混合组合没有临时选中文字");

        BoardSelection(window).Clear();
        BoardSelection(window).UnionWith(new[] { image.Id, liveText.Id, liveDrawing.Id });
        CallDrawing(window, "UpdateSelectionVisuals");
        AwaitDrawing(window, "UngroupImagesAsync");
        Equal(string.Empty, repository.GetItemsAsync("A").GetAwaiter().GetResult().Single(x => x.Id == image.Id).GroupId);
        Equal(string.Empty, repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single(x => x.Id == text.Id).GroupId);
        Equal(string.Empty, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single(x => x.Id == drawing.Id).GroupId);
    });

    private static void SelectedImageArrangement() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        var original = LiveImages(window).Select(x => x.Clone()).ToArray();
        ChooseImages(window, 0, 1);
        AwaitDrawing(window, "ArrangeImagesAsync");
        foreach (var id in new[] { "image-2", "image-3" })
        {
            var before = original.Single(x => x.Id == id);
            var after = LiveImages(window).Single(x => x.Id == id);
            Equal((before.X, before.Y, before.Width, before.Height, before.ZIndex),
                (after.X, after.Y, after.Width, after.Height, after.ZIndex));
        }
        True(LiveImages(window).Any(x => x.Id == "image-1" && (x.X != original[1].X || x.Y != original[1].Y)), "选中图片未排列");
        AwaitDrawing(window, "UndoAsync");
        ChooseImages(window, 0, 1);
        AwaitDrawing(window, "GroupImagesAsync");
        var dx = LiveImages(window)[1].X - LiveImages(window)[0].X;
        var dy = LiveImages(window)[1].Y - LiveImages(window)[0].Y;
        ChooseImages(window);
        AwaitDrawing(window, "ArrangeImagesAsync");
        Equal(dx, LiveImages(window)[1].X - LiveImages(window)[0].X);
        Equal(dy, LiveImages(window)[1].Y - LiveImages(window)[0].Y);
        True(LiveImages(window).All(x => x.Width == 180 && x.Height == 100), "自动排列改变了图片尺寸");
    });

    private static void ImageRotationSnapping() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        ChooseImages(window, 0);
        var item = LiveImages(window)[0];
        item.Rotation = 7;
        SetDrawingField(window, "_rotateItem", item);
        SetDrawingField(window, "_rotateSnapshot", item.Clone());
        CallDrawing(window, "ApplyRotationDelta", 11d, true);
        Equal(20d, item.Rotation);
        CallDrawing(window, "ApplyRotationDelta", 11d, false);
        Equal(18d, item.Rotation);
        CallDrawing(window, "ApplyRotationDelta", -11d, true);
        Equal(355d, item.Rotation);
        item.Rotation = 0;
        ChooseImages(window, 0, 1);
        AwaitDrawing(window, "GroupImagesAsync");
        var handles = (Dictionary<BoardRotationCorner, Thumb>)typeof(BoardWindow).GetField("_rotationHandles", PrivateInstance)!.GetValue(window)!;
        True(handles.Values.All(x => x.Visibility == Visibility.Visible), "组没有旋转控制点");
        CallDrawing(window, "OnRotateStarted", handles.Values.First(), new DragStartedEventArgs(0, 0));
        var first = LiveImages(window)[0];
        var second = LiveImages(window)[1];
        var distance = (new Point(first.X, first.Y) - new Point(second.X, second.Y)).Length;
        CallDrawing(window, "ApplyRotationDelta", 12d, true);
        Equal(10d, first.Rotation);
        Equal(10d, second.Rotation);
        Equal(distance, (new Point(first.X, first.Y) - new Point(second.X, second.Y)).Length, .001);
    });

    private static void ImageDoubleClickFocus() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        var image = LiveImages(window)[2];
        image.Rotation = 43;
        var original = image.Clone();
        var initialView = ViewportValues(window);
        ClickBoardItem(window, image, 2);
        var zoom = (double)typeof(BoardWindow).GetField("_viewZoom", PrivateInstance)!.GetValue(window)!;
        var panX = (double)typeof(BoardWindow).GetField("_viewPanX", PrivateInstance)!.GetValue(window)!;
        var panY = (double)typeof(BoardWindow).GetField("_viewPanY", PrivateInstance)!.GetValue(window)!;
        var surface = (Grid)window.FindName("BoardSurface");
        Equal(surface.ActualWidth / 2, (image.X + image.Width / 2) * zoom + panX, .001);
        Equal(surface.ActualHeight / 2, (image.Y + image.Height / 2) * zoom + panY, .001);
        var bounds = (Rect)typeof(BoardWindow).GetMethod("RotatedImageBounds", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { image })!;
        True(bounds.Width * zoom <= surface.ActualWidth && bounds.Height * zoom <= surface.ActualHeight, "旋转后图片没有完整适配窗口");
        True(zoom > 1, "双击没有放大图片");
        Equal((original.X, original.Y, original.Width, original.Height, original.Rotation),
            (image.X, image.Y, image.Width, image.Height, image.Rotation));
        ClickBoardItem(window, image, 2);
        Equal(initialView, ViewportValues(window));
        True(typeof(BoardWindow).GetField("_focusedImageId", PrivateInstance)!.GetValue(window) is null,
            "再次双击没有恢复聚焦前的视图");
        Equal(0, UndoCount(window));
    });

    private static (double Zoom, double PanX, double PanY) ViewportValues(BoardWindow window) =>
        ((double)typeof(BoardWindow).GetField("_viewZoom", PrivateInstance)!.GetValue(window)!,
         (double)typeof(BoardWindow).GetField("_viewPanX", PrivateInstance)!.GetValue(window)!,
         (double)typeof(BoardWindow).GetField("_viewPanY", PrivateInstance)!.GetValue(window)!);

    private static void BoundedUndoHistory() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        CallDrawing(window, "ApplyUndoLimit", 3);
        for (var i = 1; i <= 7; i++)
        {
            typeof(BoardWindow).GetMethod("PushUndoSnapshot", PrivateInstance, null, Type.EmptyTypes, null)!.Invoke(window, null);
            LiveImages(window)[0].X = i * 10;
        }
        Equal(3, UndoCount(window));
        AwaitDrawing(window, "UndoAsync");
        Equal(60d, LiveImages(window)[0].X);
        AwaitDrawing(window, "UndoAsync");
        Equal(50d, LiveImages(window)[0].X);
        AwaitDrawing(window, "RedoAsync");
        Equal(60d, LiveImages(window)[0].X);
        typeof(BoardWindow).GetMethod("PushUndoSnapshot", PrivateInstance, null, Type.EmptyTypes, null)!.Invoke(window, null);
        LiveImages(window)[0].X = 999;
        True(!((MenuItem)window.FindName("RedoMenuItem")).IsEnabled, "新操作没有清空重做分支");
        CallDrawing(window, "ApplyUndoLimit", 1);
        Equal(1, UndoCount(window));
        AwaitDrawing(window, "UndoAsync");
        Equal(60d, LiveImages(window)[0].X);
        Equal(0, UndoCount(window));
        var copied = new AppSettings { UndoStepLimit = 37, BoardShortcutsEnabled = false }.Copy();
        Equal(37, copied.UndoStepLimit);
        True(!copied.BoardShortcutsEnabled, "设置复制丢失总开关");
    });

    private static void TextStyleCopyPaste() => WithDrawingBoard((window, repository) =>
    {
        var sourceDoc = RichTextDocumentService.CreateDefault();
        sourceDoc.Blocks.Clear();
        sourceDoc.Blocks.Add(new Paragraph(new Run("源样式") { FontSize = 30, FontWeight = FontWeights.Bold,
            FontStyle = FontStyles.Italic, Foreground = Brushes.Coral, TextDecorations = TextDecorations.Underline }));
        var targetDoc = RichTextDocumentService.CreateDefault();
        targetDoc.Blocks.Clear();
        targetDoc.Blocks.Add(new Paragraph(new Run("目标内容必须保留")));
        repository.AddTextItemsAsync(new[]
        {
            new BoardTextItem { Id = "style-source", DrawerId = "A", DocumentData = RichTextDocumentService.Save(sourceDoc), BackgroundColor = "#8055AACC" },
            new BoardTextItem { Id = "style-target", DrawerId = "A", DocumentData = RichTextDocumentService.Save(targetDoc) }
        }).GetAwaiter().GetResult();
        window.ReloadAsync().GetAwaiter().GetResult();
        BoardSelection(window).Clear(); BoardSelection(window).Add("style-source");
        CallDrawing(window, "UpdateSelectionVisuals");
        CallDrawing(window, "OnCopyTextStyleClick", window, new RoutedEventArgs());
        Equal(0, UndoCount(window));
        BoardSelection(window).Clear(); BoardSelection(window).Add("style-target");
        CallDrawing(window, "UpdateSelectionVisuals");
        AwaitDrawing(window, "PasteTextStyleAsync");
        var saved = repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single(x => x.Id == "style-target");
        Equal("#8055AACC", saved.BackgroundColor);
        var document = RichTextDocumentService.Load(saved.DocumentData);
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        Equal("目标内容必须保留", range.Text.Trim());
        var run = document.Blocks.OfType<Paragraph>().First().Inlines.OfType<Run>().First();
        Equal(30d, run.FontSize);
        Equal(FontWeights.Bold, run.FontWeight);
        Equal(FontStyles.Italic, run.FontStyle);
        Equal(Colors.Coral, ((SolidColorBrush)run.Foreground).Color);
        Equal(1, UndoCount(window));
        AwaitDrawing(window, "UndoAsync");
        Equal("#00FFFFFF", repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single(x => x.Id == "style-target").BackgroundColor);
        AwaitDrawing(window, "RedoAsync");
        Equal("#8055AACC", repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single(x => x.Id == "style-target").BackgroundColor);
    });

    private static void ShortcutConflictControls()
    {
        var window = new SettingsWindow(new AppSettings());
        try
        {
            var rows = window.ShortcutGroups.SelectMany(x => x.Shortcuts).ToArray();
            var undo = rows.Single(x => x.Id == BoardShortcutCatalog.Undo);
            var redo = rows.Single(x => x.Id == BoardShortcutCatalog.Redo);
            redo.Gesture = undo.Gesture;
            True(undo.HasConflict && redo.HasConflict, "重复按键没有即时高亮");
            True(((TextBlock)window.FindName("ShortcutConflictText")).Text.Contains("冲突"), "缺少即时冲突描述");
            redo.Gesture = "Ctrl+Y";
            True(!undo.HasConflict && !redo.HasConflict, "冲突修复后提示没有消失");
            redo.Gesture = "F11";
            True(redo.HasConflict, "保留的全屏按键未参与冲突检查");
            redo.Gesture = "Ctrl+Shift+A";
            True(redo.HasConflict, "没有检测截图快捷键与画板快捷键的冲突");
            ((Button)window.FindName("DisableAllShortcutsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            True(!redo.HasConflict, "全部禁用后仍标记活动冲突");
            Equal("Ctrl+Shift+A", redo.Gesture);
            Equal(false, ((ToggleButton)window.FindName("HotkeyToggle")).IsChecked!.Value);
            ((Button)window.FindName("DisableAllShortcutsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            True(redo.HasConflict, "重新启用后冲突未恢复");
            ((Button)window.FindName("RestoreShortcutDefaultsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            True(rows.All(x => !x.HasConflict), "默认设置仍然冲突");
            Equal("Ctrl+Y", redo.Gesture);
            True(((Image)window.FindName("AboutAppIcon")).Source is BitmapSource { PixelWidth: >= 128 }, "关于图标未使用清晰的应用图标帧");
            Equal("100", ((TextBox)window.FindName("UndoStepLimitInput")).Text);
            ((TextBox)window.FindName("UndoStepLimitInput")).Text = "501";
            typeof(SettingsWindow).GetMethod("OnSaveClick", PrivateInstance)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            True(window.ResultSettings is null, "允许了超出范围的撤回上限");
        }
        finally { window.Close(); }
    }

    private static void BoardShortcutMasterSwitch() => WithDrawingBoard((window, _) =>
    {
        foreach (var definition in BoardShortcutCatalog.Definitions)
            True(BoardShortcutCatalog.TryParse(definition.DefaultGesture, out var parsed), $"默认按键 {definition.DefaultGesture} 无法解析");
        Equal("B", BoardShortcutCatalog.Format(System.Windows.Input.Key.B, System.Windows.Input.ModifierKeys.None));
        CallDrawing(window, "ApplyBoardShortcuts", BoardShortcutCatalog.CreateDefaults());
        var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle)!;
        var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.B);
        // This tests the enable switch, not the user's current physical modifiers.
        // Match the desktop state without sending keys or changing that state.
        var bindings = BoardShortcutCatalog.CreateDefaults();
        bindings[BoardShortcutCatalog.Draw] = BoardShortcutCatalog.Format(System.Windows.Input.Key.B, System.Windows.Input.Keyboard.Modifiers);
        CallDrawing(window, "ApplyBoardShortcuts", bindings);
        SetDrawingField(window, "_boardShortcutsEnabled", false);
        Equal(false, (bool)CallDrawing(window, "TryExecuteBoardShortcut", key)!);
        SetDrawingField(window, "_boardShortcutsEnabled", true);
        Equal(true, (bool)CallDrawing(window, "TryExecuteBoardShortcut", key)!);
        Equal(BoardToolMode.Pen, (BoardToolMode)typeof(BoardWindow).GetField("_toolMode", PrivateInstance)!.GetValue(window)!);
    });

    private static void DrawingConstraintsAndManualValues() => WithDrawingBoard((window, _) =>
    {
        var constrain = typeof(BoardWindow).GetMethod("ConstrainDrawingPoint",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Point Apply(BoardToolMode mode, Point current) => (Point)constrain.Invoke(null,
            new object[] { mode, new Point(0, 0), current, true })!;
        var line = Apply(BoardToolMode.Line, new Point(10, 3));
        Equal(0d, line.Y, .00001);
        var arrow = Apply(BoardToolMode.Arrow, new Point(10, 8));
        Equal(arrow.X, arrow.Y, .00001);
        var rectangle = Apply(BoardToolMode.Rectangle, new Point(10, -4));
        Equal(Math.Abs(rectangle.X), Math.Abs(rectangle.Y), .00001);
        var ellipse = Apply(BoardToolMode.Ellipse, new Point(-2, 7));
        Equal(Math.Abs(ellipse.X), Math.Abs(ellipse.Y), .00001);

        var thicknessInput = (TextBox)window.FindName("DrawingThicknessText");
        thicknessInput.Text = "10.8 px";
        CallDrawing(window, "CommitDrawingNumericInput", thicknessInput);
        Equal(10.8, ((Slider)window.FindName("DrawingThicknessSlider")).Value, .00001);
        var opacityInput = (TextBox)window.FindName("DrawingOpacityText");
        opacityInput.Text = "72%";
        CallDrawing(window, "CommitDrawingNumericInput", opacityInput);
        Equal(.72, ((Slider)window.FindName("DrawingOpacitySlider")).Value, .00001);
        var eraserInput = (TextBox)window.FindName("EraserDiameterText");
        eraserInput.Text = "44 px";
        CallDrawing(window, "CommitDrawingNumericInput", eraserInput);
        Equal(44d, ((Slider)window.FindName("EraserDiameterSlider")).Value);

        var rightDrag = typeof(BoardWindow).GetMethod("ShouldStartRightWindowDrag",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Equal(false, (bool)rightDrag.Invoke(null, new object[] { true, false, System.Windows.Input.ModifierKeys.None })!);
        Equal(true, (bool)rightDrag.Invoke(null, new object[] { true, false, System.Windows.Input.ModifierKeys.Shift })!);
    });

    private static void ImportRefreshStability() => WithDrawingBoard((window, repository) =>
    {
        SeedImages(window, repository);
        var item = LiveImages(window)[0];
        var pixels = new byte[] { 0, 0, 255, 255 };
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        source.Freeze();
        // A decoded image is reused across reloads without another asynchronous blank frame.
        var border = DrawingBorder(window, item.Id);
        ((Grid)border.Child).Children.Clear();
        ((Grid)border.Child).Children.Add(new Image { Source = source });
        var path = Path.Combine(Path.GetTempPath(), "collector-reload-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using (var file = File.Create(path)) encoder.Save(file);
            item.AssetPath = path;
            ((Grid)border.Child).Children.OfType<Image>().Single().Tag = path;
            CallDrawing(window, "RenderItems");
            var refreshed = ((Grid)DrawingBorder(window, item.Id).Child).Children.OfType<Image>().Single();
            True(ReferenceEquals(source, refreshed.Source), "刷新时丢弃了已解码图片");
        }
        finally { File.Delete(path); }
        var main = new MainWindow();
        try
        {
            var list = (ItemsControl)main.FindName("DrawerList");
            var setBusy = typeof(MainWindow).GetMethod("SetBusy", PrivateInstance)!;
            setBusy.Invoke(main, new object[] { true });
            True(list.IsEnabled, "导入时整个抽屉列表仍切换成禁用外观");
            True((bool)typeof(MainWindow).GetField("_isBusy", PrivateInstance)!.GetValue(main)!, "防重复导入状态丢失");
            setBusy.Invoke(main, new object[] { false });
            True(list.IsEnabled, "导入完成没有恢复可用状态");
        }
        finally { main.Close(); }
    });
}
