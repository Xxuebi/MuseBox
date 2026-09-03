using System.Drawing;
using System.Windows;
using Application = System.Windows.Application;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--scene-send-test")
        {
            SceneActivationService.SendAsync(new[] { args[2] }, args[1]).GetAwaiter().GetResult();
            return 0;
        }
        var application = new TestApplication();
        var resourceSource = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ApplicationResources.xaml"));
        System.Xml.Linq.XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new System.Xml.Linq.XElement(presentation + "ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "services", "clr-namespace:ScreenshotCollector.Services;assembly=MuseBox"),
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "shell", "clr-namespace:System.Windows.Shell;assembly=PresentationFramework"),
            resourceSource.Root!.Element(presentation + "Application.Resources")!.Nodes());
        application.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString()
            .Replace("clr-namespace:ScreenshotCollector.Services\"", "clr-namespace:ScreenshotCollector.Services;assembly=MuseBox\""));
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var tests = new (string Name, Action Run)[]
        {
            ("正向拖拽归一化", NormalizeForward),
            ("反向拖拽归一化", NormalizeReverse),
            ("DPI 比例转换", PixelScaling),
            ("PNG 保存不覆盖", StorageCreatesUniquePngFiles),
            ("快捷键格式化", HotkeyFormatting),
            ("缩放保持指针位置", ZoomKeepsPointer),
            ("500 项自动排布", ArrangeFiveHundredItems),
            ("八向拉伸几何", EightDirectionResize),
            ("多选范围缩放", MultiSelectionScaling),
            ("逐层和顶底层排序", LayerOrdering),
            ("SQLite 抽屉和画板恢复", RepositoryPersistsBoard),
            ("注释和绘制持久化", AnnotationAndDrawingPersist),
            ("绘制分组兼容旧数据与变换", DrawingGroupTransforms),
            ("绘制会话新建续画撤回持久化", DrawingSessionLifecycle),
            ("绘制分组局部擦除", DrawingGroupErasing),
            ("绘制工具栏圆角菜单与颜色预览", DrawingToolbarLayout),
            ("绘制分组矢量缓存", DrawingGroupRendering),
            ("笔迹边框仅在主动选择时出现", DrawingSelectionFrames),
            ("橡皮擦独立大小与范围指示", EraserSizeAndCursor),
            ("文字工具栏定位稳定且贴近选区", TextToolbarPositioning),
            ("小菜单重复点击开关与外部关闭", ToolPopupToggling),
            ("小菜单尖角间距与文字菜单翻转", ToolPopupSpacing),
            ("简洁控制框与原生旋转光标", ModernSelectionControls),
            ("橡皮擦实时预览与单次撤回保存", RealtimeEraserPreview),
            ("实时擦除删除与末帧保存", RealtimeEraserCompletion),
            ("全屏覆盖当前显示器并恢复窗口", FullscreenRoundTrip),
            ("文字字号使用标准点数", TextTypographyUsesPoints),
            ("旧数据库自动迁移", LegacyDatabaseMigrates),
            ("画板资料库路径迁移", BoardStorageMigrates),
            ("资源哈希去重", AssetLibraryDeduplicates),
            ("设置窗口可构造", SettingsWindowConstructs),
            ("快捷键直接嵌入设置页", EmbeddedShortcutSettings),
            ("快捷键紧凑布局与搜索", CompactShortcutLayout),
            ("快捷键筛选后编辑与完整保存", FilteredShortcutEditing),
            ("图片打组解组撤回重做与持久化", ImageGroupingLifecycle),
            ("组合背景工具栏锁定与拖入移出", ImageGroupPresentationAndMembership),
            ("图片文字绘制混合组合与边框超限输入", MixedElementGroupingAndBorderMetrics),
            ("图层树拖放排序与循环拒绝", LayerTreeMoveAndCycleGuards),
            ("图层树最大三十二层限制", LayerTreeDepthLimit),
            ("嵌套图层名称与组合事务持久化", LayerRepositoryPersistsNestedHierarchy),
            ("场景版本一迁移与版本二层级校验", SceneV1AndV2LayerMigration),
            ("图层面板覆盖动画与视图参数保持", LayerPanelOverlayAndSelectionState),
            ("图层Shift连选子元素单选与保持缩放聚焦", LayerPanelShiftChildAndFocusSelection),
            ("嵌套组合逐层双击与内层拖动选择", NestedGroupDrillDownAndDragSelection),
            ("文字和绘制图层双击保持缩放居中", NonImageLayerRowsCenterWithoutZoom),
            ("嵌套组合背景始终位于所有元素之后", NestedGroupBackgroundsStayBehindElements),
            ("剪贴板图层名称包含创建日期", ClipboardLayerNamesIncludeDates),
            ("选区排列不影响其他图片及组内结构", SelectedImageArrangement),
            ("单图与分组五度旋转吸附", ImageRotationSnapping),
            ("双击旋转图片聚焦窗口", ImageDoubleClickFocus),
            ("撤回上限与重做分支", BoundedUndoHistory),
            ("文字样式复制粘贴不修改内容", TextStyleCopyPaste),
            ("快捷键即时冲突与批量开关", ShortcutConflictControls),
            ("禁用画板快捷键后不触发工具", BoardShortcutMasterSwitch),
            ("图片刷新复用与小窗忙碌状态不闪烁", ImportRefreshStability),
            ("绘图吸附手动数值与Shift右键拖窗", DrawingConstraintsAndManualValues),
            ("数值输入隐藏边框与外置单位", NumericInputsAreDiscreet),
            ("图片工具栏选择聚焦与跟随", ImageToolbarSpotlight),
            ("图片裁切调色与透明通道", ImagePixelEditing),
            ("图片编辑独立资源撤回重做", ImageEditHistoryAndAssets),
            ("图片双链接持久化与显隐", ImageLinksPersistAndUndo),
            ("注释双链接保存清除撤回重做", TextLinksPersistAndUndo),
            ("注释链接外侧定位与选择跟随", TextLinksPositioning),
            ("注释编辑与链接历史互相独立", TextLinksEditingHistory),
            ("注释链接菜单与统一设置窗口", TextLinksDialogAndMenu),
            ("图片编辑器布局裁切旋转预览", ImageEditorLayoutAndCrop),
            ("图片色相与无损透明预览", ImageHueAndRawPreview),
            ("编辑器数字输入与连续调色撤回", ImageEditorAdjustmentUndo),
            ("编辑器正反旋转裁切和重置撤回", ImageEditorStructuralUndo),
            ("另存新增图片保留原图并可撤回", ImageSaveAsPreservesOriginal),
            ("大图实时预览缓存与原尺寸输出", ImageEditorLargePreview),
            ("GIF局部帧透明度处置与原尺寸提取", GifCompositionAndExtraction),
            ("GIF倍速计时暂停和逐帧切换", GifPlaybackTiming),
            ("GIF工具栏所有帧选择和重载状态", GifBoardToolbarAndFrames),
            ("GIF帧另存保留动图并可撤回", GifFrameSaveAndUndo),
            ("图片编辑器图标输入收起与链接清除", ImageEditorPolishAndLinks),
            ("GIF原始剪贴板格式优先于静态预览", GifEncodedClipboardPreservesAnimation),
            ("GIF网页内嵌数据及原图来源保留", GifHtmlClipboardPreservesAnimation),
            ("GIF按内容识别与历史错误后缀兼容", GifContentDetectionAndLegacyAssets),
            ("GIF剪贴板收集到播放工具栏全链路", GifClipboardToBoardToolbar),
            ("GIF网页原图响应真实性检查", GifOriginalDownloadValidation),
            ("主窗口为抽屉布局", MainWindowState),
            ("新增抽屉跨越Z且保持原资料", UnlimitedDrawerPersistence),
            ("抽屉上下分区自适应换列和滚动", ResponsiveDrawerLayout),
            ("新抽屉收集重命名与防重复操作", NewDrawerCollectAndRename),
            ("小窗八向拉伸与尺寸记忆", MainResizeAndSizeSettings),
            ("小窗启动恢复屏幕外位置仍然可见", MainWindowOffscreenStartup),
            ("小窗首次显示及托盘唤起保持可见", MainWindowStartupAndWakeVisibility),
            ("抽屉字母开关与设置菜单", DrawerLettersAndSettings),
            ("抽屉排序保存取消和失败回滚", DrawerReorderPersistenceAndCancel),
            ("收集预览向下淡出", DrawerDownwardFade),
            ("封面抽屉点击即时移入反馈且不替换封面", FixedCoverClickFeedback),
            ("沉浸收集上下区域切换自适应与状态记忆", CollectionCompactLayoutAndPersistence),
            ("沉浸收集下侧收集剪贴板与文件并播放反馈", CollectionFooterImportsAndFeedback),
            ("沉浸收集双向动画快速反转隐藏及恢复", CollectionReversibleAnimation),
            ("沉浸收集窗口自动缩短手动最小化及退出恢复", CollectionWindowHeightRoundTrip),
            ("新建抽屉展开淡入位移与平滑滚动", NewDrawerEntranceAndScroll),
            ("全局圆角提示和托盘菜单", RoundedTooltipsAndTrayMenu),
            ("抽屉与系统截图设置保存", DrawerAndSystemCaptureSettings),
            ("全局明暗主题语言路径与开关动画", AppearanceLanguageStorageAndSwitchAnimation),
            ("系统截图按键与取消完成状态", SystemCaptureShortcutAndSession),
            ("抽屉菜单内嵌与图标布局", DrawerEmbeddedMenu),
            ("抽屉跟手动画让位取消与稳定命中", DrawerSmoothReorder),
            ("旋转图片八向拉伸锚点与比例", RotatedResizeAnchors),
            ("旋转图片拉伸最小尺寸与中心锚点", RotatedResizeMinimum),
            ("Shift旋转图片拉伸持久化撤回重做", RotatedResizeInteraction),
            ("文字菜单排序分隔和动画覆盖", TextMenuGroupsAndTransitions),
            ("抽屉菜单原位淡出、重复开关与无鼠标捕获", DrawerNativeToggle),
            ("上侧抽屉菜单反向淡出和快速开关", DrawerMenuUpwardPlacement),
            ("固定封面裁切边界及旋转镜像透明渲染", CoverCropBoundsAndRendering),
            ("抽屉封面持久化、共享资源和恢复自动预览", DrawerCoverPersistence),
            ("封面编辑器布局、工具操作和重新编辑", DrawerCoverEditorLayoutAndRoundTrip),
            ("封面编辑器确认取消和系统关闭动画", DrawerCoverDialogResults),
            ("场景跨资料库全部内容往返与继续编辑", ScenePortableRoundTrip),
            ("场景未保存版本及原子写入失败保护", SceneRevisionAndAtomicSave),
            ("场景恶意路径富文本与损坏资源拒绝", SceneRejectsUnsafeFiles),
            ("场景单实例传递Unicode路径", SceneActivationRoundTrip),
            ("场景菜单保存另存替换取消及重复打开", SceneMenuWorkflow),
            ("场景导入事务中途失败完整回滚", SceneImportTransactionRollback),
            ("场景保存提交实时GIF和未完成文字", SceneLiveGifAndTextSnapshot),
            ("场景三选项提示取消保存及不保存", ScenePromptChoices),
            ("场景缺失字体保留原名称并提示", SceneMissingFonts),
            ("场景失效图片注释链接提示清除及重设", SceneUnavailableLinks),
            ("菜单进出动画和点击穿透清理", PopupAnimationLifecycle),
            ("圆角确认弹窗布局与安全默认", RoundedPromptLayout),
            ("图片透明度原始Alpha及输入撤回", ImageOpacityWorkflow),
            ("旋转光标原生多DPI尺寸", RotationCursorNativeDpi),
            ("曲线箭头方向渲染保存撤回擦除", CurveArrowWorkflow),
            ("菜单四向反向淡出与首帧无跳变", ReversePopupAnimations),
            ("放大淡入和缩小淡出可中断", ReverseScaleAnimations),
            ("文字子菜单和字号字体下拉随缩放移动", ToolMenusFollowZoom),
            ("图片编辑按钮白色背景阴影", ImageEditButtonShadow),
            ("确认弹窗反向关闭保持确认取消结果", PromptCloseAnimationResult),
            ("画板窗口可构造", BoardWindowConstructs),
            ("画板设置窗口可构造", BoardSettingsWindowConstructs),
            ("自定义色盘窗口可构造", CustomColorPickerConstructs),
            ("屏幕吸色覆盖层可构造", EyedropperOverlayConstructs),
            ("剪贴板读取不会抛错", ClipboardReadDoesNotThrow),
            ("场景缩略图处理器读取与COM元数据", SceneThumbnailProviderRoundTrip),
            ("应用图标多尺寸资源", ApplicationIconAssets)
        };

        if (args.Length > 0) tests = tests.Where(t => t.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToArray();
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {name}: {exception}");
            }
        }
        Console.WriteLine($"\n{tests.Length - failures}/{tests.Length} tests passed.");
        application.Shutdown();
        return failures == 0 ? 0 : 1;
    }

    private sealed class TestApplication : App
    {
        // Pumping WPF render frames must not start the production single-instance
        // application, access its user database, or wake an already running copy.
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static void NormalizeForward() =>
        Equal(new Rect(10, 20, 100, 70), RegionMath.Normalize(new Point(10, 20), new Point(110, 90), 200, 100));

    private static void NormalizeReverse() =>
        Equal(new Rect(10, 20, 100, 70), RegionMath.Normalize(new Point(110, 90), new Point(10, 20), 200, 100));

    private static void PixelScaling() =>
        Equal(Rectangle.FromLTRB(30, 15, 180, 90),
            RegionMath.ToPixelRectangle(new Rect(20, 10, 100, 50), 200, 100, 300, 150));

    private static void StorageCreatesUniquePngFiles()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var bitmap = CreateBitmap();
            var storage = new ScreenshotStorage();
            var first = storage.SavePngAsync(bitmap, directory).GetAwaiter().GetResult();
            var second = storage.SavePngAsync(bitmap, directory).GetAwaiter().GetResult();
            True(File.Exists(first), "第一个 PNG 未生成");
            True(File.Exists(second), "第二个 PNG 未生成");
            True(first != second, "连续保存覆盖了已有文件");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void HotkeyFormatting()
    {
        Equal("Ctrl + Shift + A", HotkeyFormatter.Format(
            HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x41));
        Equal("Alt + F12", HotkeyFormatter.Format(HotkeyModifiers.Alt, 0x7B));
    }

    private static void ZoomKeepsPointer()
    {
        var pointer = new Point(320, 180);
        const double oldZoom = 0.75;
        const double oldPanX = 41;
        const double oldPanY = -23;
        var worldX = (pointer.X - oldPanX) / oldZoom;
        var worldY = (pointer.Y - oldPanY) / oldZoom;
        var pan = BoardMath.ZoomAt(pointer, oldZoom, 1.5, oldPanX, oldPanY);
        Equal(pointer.X, worldX * 1.5 + pan.PanX, .0001);
        Equal(pointer.Y, worldY * 1.5 + pan.PanY, .0001);
    }

    private static void ArrangeFiveHundredItems()
    {
        var items = Enumerable.Range(0, 500).Select(index => new BoardItem
        {
            Id = index.ToString(), Width = 100 + index % 7, Height = 80 + index % 5
        }).ToArray();
        var arranged = BoardMath.ArrangeGrid(items);
        Equal(500, arranged.Count);
        Equal(500, arranged.Select(x => (x.X, x.Y)).Distinct().Count());
        Equal(499, arranged.Max(x => x.ZIndex));

        var mixed = new[]
        {
            new BoardItem { Id = "large", Width = 1000, Height = 1000 },
            new BoardItem { Id = "s1", Width = 100, Height = 100 },
            new BoardItem { Id = "s2", Width = 100, Height = 100 },
            new BoardItem { Id = "s3", Width = 100, Height = 100 },
            new BoardItem { Id = "s4", Width = 100, Height = 100 }
        };
        var packed = BoardMath.ArrangeGrid(mixed);
        var small = packed.Where(x => x.Id != "large").ToArray();
        var smallSpan = small.Max(x => x.X + x.Width) - small.Min(x => x.X);
        True(smallSpan < 500, "大图导致小图之间出现了巨大的固定网格间隔");
        for (var first = 0; first < packed.Count; first++)
        for (var second = first + 1; second < packed.Count; second++)
            True(!new Rect(packed[first].X, packed[first].Y, packed[first].Width, packed[first].Height)
                    .IntersectsWith(new Rect(
                        packed[second].X, packed[second].Y,
                        packed[second].Width, packed[second].Height)),
                "紧凑排布中的图片发生重叠");
    }

    private static void EightDirectionResize()
    {
        var northWest = new BoardItem { X = 100, Y = 100, Width = 200, Height = 120 };
        BoardMath.ResizeItem(northWest, BoardResizeDirection.NorthWest, -20, -10);
        Equal(80d, northWest.X);
        Equal(90d, northWest.Y);
        Equal(220d, northWest.Width);
        Equal(130d, northWest.Height);

        var east = new BoardItem { X = 10, Y = 20, Width = 100, Height = 80 };
        BoardMath.ResizeItem(east, BoardResizeDirection.East, 35, 0);
        Equal(135d, east.Width);
        Equal(80d, east.Height);

        var westMinimum = new BoardItem { X = 0, Width = 60, Height = 60 };
        BoardMath.ResizeItem(westMinimum, BoardResizeDirection.West, 100, 0);
        Equal(20d, westMinimum.X);
        Equal(40d, westMinimum.Width);

        var snapshot = new BoardItem { X = 100, Y = 100, Width = 200, Height = 100 };
        var proportional = BoardMath.ResizeFromSnapshot(
            snapshot, BoardResizeDirection.SouthEast, 100, 10, true, false);
        Equal(300d, proportional.Width);
        Equal(150d, proportional.Height);
        Equal(100d, proportional.X);
        Equal(100d, proportional.Y);

        var free = BoardMath.ResizeFromSnapshot(
            snapshot, BoardResizeDirection.SouthEast, 100, 10, false, false);
        Equal(300d, free.Width);
        Equal(110d, free.Height);

        var centered = BoardMath.ResizeFromSnapshot(
            snapshot, BoardResizeDirection.East, 50, 0, false, true);
        Equal(50d, centered.X);
        Equal(300d, centered.Width);
        Equal(200d, centered.X + centered.Width / 2);

        var sideOnly = BoardMath.ResizeFromSnapshot(
            snapshot, BoardResizeDirection.East, 50, 90, false, false);
        Equal(250d, sideOnly.Width);
        Equal(100d, sideOnly.Height);

        var lockedSide = BoardMath.ResizeFromSnapshot(
            snapshot, BoardResizeDirection.East, 50, 90, true, false);
        Equal(250d, lockedSide.Width);
        Equal(125d, lockedSide.Height);
        Equal(87.5d, lockedSide.Y);

        var rotated = BoardMath.RotatePoint(new Point(10, 0), new Point(0, 0), 90);
        Equal(0d, rotated.X, .0001);
        Equal(10d, rotated.Y, .0001);
        Equal(350d, BoardMath.NormalizeAngle(-10), .0001);
        Equal(-20d, BoardMath.NormalizeAngleDelta(340), .0001);
    }

    private static void MultiSelectionScaling()
    {
        var items = new[]
        {
            new BoardItem { Id = "A", X = 10, Y = 20, Width = 100, Height = 80 },
            new BoardItem { Id = "B", X = 160, Y = 120, Width = 50, Height = 40 }
        };
        var bounds = BoardMath.GetBounds(items);
        Equal(new Rect(10, 20, 200, 140), bounds);
        var scaled = BoardMath.ScaleGroup(items, bounds, new Rect(0, 0, 400, 280));
        Equal(0d, scaled[0].X, .0001);
        Equal(200d, scaled[0].Width, .0001);
        Equal(300d, scaled[1].X, .0001);
        Equal(100d, scaled[1].Width, .0001);
    }

    private static void LayerOrdering()
    {
        static BoardItem Item(string id, int z) => new() { Id = id, ZIndex = z };
        var source = new[] { Item("A", 0), Item("B", 1), Item("C", 2), Item("D", 3) };
        var selected = new HashSet<string> { "B" };
        var up = BoardMath.ShiftLayer(source, selected, 1);
        Equal("A,C,B,D", string.Join(',', up.OrderBy(x => x.ZIndex).Select(x => x.Id)));
        var front = BoardMath.MoveToExtreme(source, selected, true);
        Equal("A,C,D,B", string.Join(',', front.OrderBy(x => x.ZIndex).Select(x => x.Id)));
        var back = BoardMath.MoveToExtreme(source, selected, false);
        Equal("B,A,C,D", string.Join(',', back.OrderBy(x => x.ZIndex).Select(x => x.Id)));
    }

    private static void RepositoryPersistsBoard()
    {
        var directory = CreateTempDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            var repository = new BoardRepository(paths);
            repository.InitializeAsync().GetAwaiter().GetResult();
            var a = repository.GetDrawersAsync().GetAwaiter().GetResult();
            Equal("A,B,C,D", string.Join(',', a.Select(x => x.Id)));
            repository.UpdateDrawerNameAsync("B", "角色参考").GetAwaiter().GetResult();
            var asset = new AssetRecord("asset", "hash", ".png", "hash.png", 100, 50, DateTime.UtcNow);
            repository.UpsertAssetAsync(asset).GetAwaiter().GetResult();
            repository.AddItemsAsync(new[]
            {
                new BoardItem
                {
                    Id = "item", DrawerId = "B", AssetId = asset.Id,
                    X = 12, Y = 34, Rotation = 37
                }
            }).GetAwaiter().GetResult();
            repository.SaveViewportAsync(new BoardViewport
            {
                DrawerId = "B", BackgroundColor = "#334455", WindowOpacity = .72,
                OpacityAffectsImages = true, ShowWindowFrame = false
            }).GetAwaiter().GetResult();
            var restored = new BoardRepository(paths);
            restored.InitializeAsync().GetAwaiter().GetResult();
            var item = restored.GetItemsAsync("B").GetAwaiter().GetResult().Single();
            Equal(12d, item.X);
            Equal(34d, item.Y);
            Equal(37d, item.Rotation);
            Equal("角色参考", restored.GetDrawersAsync().GetAwaiter().GetResult().Single(x => x.Id == "B").DisplayName);
            var viewport = restored.GetViewportAsync("B").GetAwaiter().GetResult();
            Equal("#334455", viewport.BackgroundColor);
            Equal(.72, viewport.WindowOpacity, .0001);
            True(viewport.OpacityAffectsImages, "图片透明度联动设置未恢复");
            True(!viewport.ShowWindowFrame, "画板边框和阴影设置未恢复");

            restored.DeleteDrawerAsync("D").GetAwaiter().GetResult();
            restored.InitializeAsync().GetAwaiter().GetResult();
            Equal("A,B,C,D", string.Join(',',
                restored.GetDrawersAsync().GetAwaiter().GetResult().Select(x => x.Id)));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void LegacyDatabaseMigrates()
    {
        var directory = CreateTempDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            using (var connection = new SqliteConnection($"Data Source={paths.Database};Pooling=False"))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE drawers(id TEXT PRIMARY KEY, sort_order INTEGER NOT NULL, created_utc TEXT NOT NULL);
                    CREATE TABLE viewports(
                        drawer_id TEXT PRIMARY KEY, pan_x REAL NOT NULL DEFAULT 0, pan_y REAL NOT NULL DEFAULT 0,
                        zoom REAL NOT NULL DEFAULT 1, window_left REAL NULL, window_top REAL NULL,
                        window_width REAL NOT NULL DEFAULT 1100, window_height REAL NOT NULL DEFAULT 760,
                        topmost INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO drawers VALUES('A',0,'2026-01-01T00:00:00.0000000Z');
                    INSERT INTO viewports(drawer_id) VALUES('A');
                    CREATE TABLE text_items(
                        id TEXT PRIMARY KEY, drawer_id TEXT NOT NULL,
                        x REAL NOT NULL, y REAL NOT NULL, width REAL NOT NULL, height REAL NOT NULL,
                        rotation REAL NOT NULL DEFAULT 0, z_index INTEGER NOT NULL,
                        document_data TEXT NOT NULL, background_color TEXT NOT NULL DEFAULT '#00FFFFFF',
                        created_utc TEXT NOT NULL);
                    INSERT INTO text_items VALUES('legacy-note','A',10,20,200,40,0,1,'legacy-document','#00FFFFFF','2026-01-01T00:00:00.0000000Z');
                    """;
                command.ExecuteNonQuery();
            }
            var repository = new BoardRepository(paths);
            repository.InitializeAsync().GetAwaiter().GetResult();
            repository.InitializeAsync().GetAwaiter().GetResult();
            var oldNote = repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single();
            Equal("legacy-document", oldNote.DocumentData);
            Equal("", oldNote.WebLink); Equal("", oldNote.FileLink);
            oldNote.WebLink = "https://example.com/note";
            oldNote.FileLink = directory;
            repository.UpdateTextItemsAsync(new[] { oldNote }).GetAwaiter().GetResult();
            var reopened = new BoardRepository(paths).GetTextItemsAsync("A").GetAwaiter().GetResult().Single();
            Equal(oldNote.WebLink, reopened.WebLink); Equal(directory, reopened.FileLink);
            var drawers = repository.GetDrawersAsync().GetAwaiter().GetResult();
            Equal("A,B,C,D", string.Join(',', drawers.Select(x => x.Id)));
            Equal("未命名", drawers.Single(x => x.Id == "A").DisplayName);
            var viewport = repository.GetViewportAsync("A").GetAwaiter().GetResult();
            Equal("#7A7A7A", viewport.BackgroundColor);
            Equal(1d, viewport.WindowOpacity);
            True(!viewport.OpacityAffectsImages, "旧数据库迁移后的图片透明度联动默认值错误");
            True(viewport.ShowWindowFrame, "旧数据库迁移后的画板边框和阴影没有默认开启");
            using var verify = new SqliteConnection($"Data Source={paths.Database};Pooling=False");
            verify.Open();
            var pragma = verify.CreateCommand();
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('items') WHERE name='rotation'";
            Equal(1L, (long)pragma.ExecuteScalar()!);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void AnnotationAndDrawingPersist()
    {
        var directory = CreateTempDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            var repository = new BoardRepository(paths);
            repository.InitializeAsync().GetAwaiter().GetResult();
            var document = RichTextDocumentService.CreateDefault();
            document.Blocks.Clear();
            document.Blocks.Add(new System.Windows.Documents.Paragraph(
                new System.Windows.Documents.Run("中文注释")));
            var text = new BoardTextItem
            {
                Id = "note", DrawerId = "A", X = 15, Y = 25,
                Width = 280, Height = 120, Rotation = 12, ZIndex = 3,
                DocumentData = RichTextDocumentService.Save(document),
                BackgroundColor = "#33FFFFFF"
            };
            var drawing = new BoardDrawingItem
            {
                Id = "stroke", DrawerId = "A", X = 8, Y = 9,
                Width = 90, Height = 40, Rotation = 21, ZIndex = 4,
                Kind = BoardDrawingKind.Arrow,
                PointsJson = "[{\"X\":0.1,\"Y\":0.2,\"Pressure\":0.5},{\"X\":0.9,\"Y\":0.8,\"Pressure\":1.0}]",
                StrokeColor = "#FF336699", FillColor = "#22336699",
                StrokeThickness = 7, StrokeOpacity = .7, Dashed = true
            };
            repository.AddTextItemsAsync(new[] { text }).GetAwaiter().GetResult();
            repository.AddDrawingItemsAsync(new[] { drawing }).GetAwaiter().GetResult();

            var restoredText = repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single();
            Equal("中文注释", RichTextDocumentService.PlainText(
                RichTextDocumentService.Load(restoredText.DocumentData)));
            Equal(12d, restoredText.Rotation);
            var restoredDrawing = repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single();
            Equal(BoardDrawingKind.Arrow, restoredDrawing.Kind);
            Equal(7d, restoredDrawing.StrokeThickness);
            True(restoredDrawing.Dashed, "绘制虚线样式没有恢复");
            Equal(2, repository.GetItemCountAsync("A").GetAwaiter().GetResult());

            restoredText.Width = 360;
            restoredDrawing.Rotation = 47;
            repository.UpdateTextItemsAsync(new[] { restoredText }).GetAwaiter().GetResult();
            repository.UpdateDrawingItemsAsync(new[] { restoredDrawing }).GetAwaiter().GetResult();
            Equal(360d, repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single().Width);
            Equal(47d, repository.GetDrawingItemsAsync("A").GetAwaiter().GetResult().Single().Rotation);

            repository.DeleteTextItemsAsync(new[] { text.Id }).GetAwaiter().GetResult();
            repository.DeleteDrawingItemsAsync(new[] { drawing.Id }).GetAwaiter().GetResult();
            Equal(0, repository.GetItemCountAsync("A").GetAwaiter().GetResult());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TextTypographyUsesPoints()
    {
        Equal(16d, RichTextDocumentService.ToPoints(RichTextDocumentService.ToDip(16)), .0001);
        var document = RichTextDocumentService.CreateDefault();
        Equal(16d, RichTextDocumentService.ToPoints(document.FontSize), .0001);
    }

    private static void BoardStorageMigrates()
    {
        var directory = CreateTempDirectory();
        try
        {
            var sourcePaths = new AppDataPaths(Path.Combine(directory, "source"));
            var sourceRepository = new BoardRepository(sourcePaths);
            sourceRepository.InitializeAsync().GetAwaiter().GetResult();
            var sourceImage = Path.Combine(directory, "source.png");
            using (var bitmap = CreateBitmap()) bitmap.Save(sourceImage);
            var library = new AssetLibraryService(sourcePaths, sourceRepository);
            var imported = library.ImportFileAsync(sourceImage).GetAwaiter().GetResult();
            sourceRepository.AddItemsAsync(new[]
            {
                new BoardItem
                {
                    Id = "migrated", DrawerId = "C", AssetId = imported.Asset.Id,
                    Rotation = 45
                }
            }).GetAwaiter().GetResult();

            var destinationRoot = Path.Combine(directory, "destination");
            BoardStorageMigrationService.MigrateAsync(sourcePaths.Root, destinationRoot)
                .GetAwaiter().GetResult();
            var destinationPaths = new AppDataPaths(destinationRoot);
            var destinationRepository = new BoardRepository(destinationPaths);
            destinationRepository.InitializeAsync().GetAwaiter().GetResult();
            var item = destinationRepository.GetItemsAsync("C").GetAwaiter().GetResult().Single();
            Equal(45d, item.Rotation);
            True(File.Exists(item.AssetPath), "迁移后的图片文件缺失");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void AssetLibraryDeduplicates()
    {
        var directory = CreateTempDirectory();
        try
        {
            var source = Path.Combine(directory, "source.png");
            using (var bitmap = CreateBitmap()) bitmap.Save(source);
            var paths = new AppDataPaths(Path.Combine(directory, "data"));
            var repository = new BoardRepository(paths);
            repository.InitializeAsync().GetAwaiter().GetResult();
            var library = new AssetLibraryService(paths, repository);
            var first = library.ImportFileAsync(source).GetAwaiter().GetResult();
            var second = library.ImportFileAsync(source).GetAwaiter().GetResult();
            Equal(first.Asset.Id, second.Asset.Id);
            True(File.Exists(first.FullPath), "资料库文件未生成");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void SettingsWindowConstructs()
    {
        var window = new SettingsWindow(new AppSettings());
        window.Measure(new System.Windows.Size(520, 610));
        window.Arrange(new Rect(0, 0, 520, 610));
        window.UpdateLayout();
        True(window.FindName("StoragePathTextBox") is System.Windows.Controls.TextBox, "画板保存路径输入框缺失");
        True(window.FindName("BoardShortcutsButton") is null, "仍然存在二级快捷键窗口入口");
        True(window.FindName("ShortcutGroupList") is System.Windows.Controls.ItemsControl,
            "快捷键列表没有直接嵌入设置页");
        True(window.FindName("HotkeyInputTextBox") is System.Windows.Controls.TextBox,
            "全局框选快捷键没有迁入键盘快捷键页");
        var categories = (System.Windows.Controls.TabControl)window.FindName("SettingsCategories");
        Equal(6, categories.Items.Count);
        Equal("MuseBox 设置", window.Title);
        True(((System.Windows.Controls.TextBlock)window.FindName("AboutProductName")).Text == "MuseBox" &&
            ((System.Windows.Controls.TextBlock)window.FindName("AboutChineseSubtitle")).Text == "灵感收集器",
            "关于页没有以 MuseBox 为主标题并保留中文副标题");
        var versionText = (System.Windows.Controls.TextBlock)window.FindName("AppVersionText");
        True(versionText.Text.Contains("1.1.15", StringComparison.Ordinal),
            "关于栏目没有显示当前程序集版本");
        window.ApplyTemplate();
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
        Equal(0d, chrome.CaptionHeight);
        True(window.AllowsTransparency, "主题窗口仍在使用会产生黑色外框的系统非透明边框");
        var windowChrome = (System.Windows.Controls.Border)window.Template.FindName("ThemedWindowChrome", window);
        True(windowChrome.CornerRadius.TopLeft >= 12,
            "设置窗口没有使用与小窗一致的圆润外框");
        foreach (var captionName in new[] { "MinimizeCaptionButton", "MaximizeCaptionButton", "CloseCaptionButton" })
        {
            var caption = window.Template.FindName(captionName, window) as System.Windows.Controls.Button;
            True(caption is { IsHitTestVisible: true } &&
                 System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(caption),
                $"标题栏按钮 {captionName} 无法接收点击");
        }
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        foreach (var captionName in new[] { "MinimizeCaptionButton", "MaximizeCaptionButton", "CloseCaptionButton" })
        {
            var caption = (System.Windows.Controls.Button)window.Template.FindName(captionName, window);
            True(caption.IsEnabled, $"标题栏按钮 {captionName} 在窗口显示后仍被禁用");
            var center = caption.PointToScreen(new Point(caption.ActualWidth / 2, caption.ActualHeight / 2));
            var packedPoint = new IntPtr(
                ((int)Math.Round(center.X) & 0xFFFF) |
                (((int)Math.Round(center.Y) & 0xFFFF) << 16));
            Equal(new IntPtr(1), SendMessage(
                new System.Windows.Interop.WindowInteropHelper(window).Handle,
                0x0084, IntPtr.Zero, packedPoint));
        }
        var edge = window.PointToScreen(new Point(2, window.ActualHeight / 2));
        var packedEdge = new IntPtr(
            ((int)Math.Round(edge.X) & 0xFFFF) |
            (((int)Math.Round(edge.Y) & 0xFFFF) << 16));
        Equal(new IntPtr(10), SendMessage(
            new System.Windows.Interop.WindowInteropHelper(window).Handle,
            0x0084, IntPtr.Zero, packedEdge));
        var minimizeButton = (System.Windows.Controls.Button)window.Template.FindName("MinimizeCaptionButton", window);
        True(ReferenceEquals(ThemedWindowCommands.Minimize, minimizeButton.Command) &&
             ReferenceEquals(window, minimizeButton.CommandParameter),
            "最小化按钮没有连接到当前窗口命令");
        var minimizePeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(minimizeButton);
        ((System.Windows.Automation.Provider.IInvokeProvider)minimizePeer.GetPattern(
            System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Equal(WindowState.Minimized, window.WindowState);
        window.WindowState = WindowState.Normal;
        var compatible = (System.Windows.Controls.Primitives.ToggleButton)
            window.FindName("CompatibleRenderingToggle");
        True(compatible.IsChecked == true, "全局兼容渲染没有默认启用");
        True(ReferenceEquals(compatible.Style, Application.Current.FindResource("SwitchStyle")),
            "小窗没有使用统一开关样式");
        True(!new AppSettings { CompatibleRendering = false }.Copy().CompatibleRendering,
            "全局兼容渲染设置复制失败");
        True(window.FindName("RepairFileAssociationButton") is System.Windows.Controls.Button &&
            window.FindName("UninstallFileAssociationButton") is System.Windows.Controls.Button &&
            window.FindName("RepairSceneThumbnailButton") is System.Windows.Controls.Button &&
            window.FindName("UninstallSceneThumbnailButton") is System.Windows.Controls.Button,
            "文件关联与缩略图修复/卸载按钮缺失");
        Equal("#000000,#FFFFFF", string.Join(',', new AppSettings().SavedColors));
        Equal("#123456", new AppSettings { SavedColors = ["#123456"] }.Copy().SavedColors.Single());
        Equal(6, window.ShortcutGroups.Count);
        Equal(19, window.ShortcutGroups.Sum(x => x.Shortcuts.Count));
        window.Close();
    }

    private static void EmbeddedShortcutSettings()
    {
        var defaults = BoardShortcutCatalog.CreateDefaults();
        Equal(19, defaults.Count);
        True(BoardShortcutCatalog.TryParse(defaults[BoardShortcutCatalog.Arrange], out var arrange) &&
             arrange is not null, "默认自动排布快捷键无法解析");
        var custom = BoardShortcutCatalog.Merge(defaults);
        custom[BoardShortcutCatalog.FitAll] = "Ctrl+F";
        var settings = new AppSettings { BoardShortcuts = custom };
        Equal("Ctrl+F", settings.Copy().BoardShortcuts[BoardShortcutCatalog.FitAll]);

        var window = new SettingsWindow(settings);
        window.Measure(new System.Windows.Size(780, 590));
        window.Arrange(new Rect(0, 0, 780, 590));
        window.UpdateLayout();
        Equal("Ctrl+F", window.ShortcutGroups.SelectMany(x => x.Shortcuts)
            .Single(x => x.Id == BoardShortcutCatalog.FitAll).Gesture);
        True(BoardShortcutCatalog.Definitions.Any(x => x.Id == BoardShortcutCatalog.ResetImage),
            "图片重置快捷键定义缺失");
        True(BoardShortcutCatalog.Definitions.Any(x => x.Id == BoardShortcutCatalog.AddText),
            "注释快捷键定义缺失");
        True(BoardShortcutCatalog.Definitions.Any(x => x.Id == BoardShortcutCatalog.Draw),
            "绘制快捷键定义缺失");
        window.Close();
    }

    private static void MainWindowState()
    {
        var window = new MainWindow();
        window.Measure(new System.Windows.Size(292, 250));
        window.Arrange(new Rect(0, 0, 292, 250));
        window.UpdateLayout();
        Equal("MuseBox", window.Title);
        True(window.FindName("DrawerList") is System.Windows.Controls.ItemsControl, "未找到抽屉列表");
        True(window.AllowsTransparency, "小窗没有启用透明圆角窗口");
        var chrome = (System.Windows.Controls.Border)window.FindName("MainChrome");
        True(chrome.CornerRadius.TopLeft >= 16, "主窗口圆角未应用");
        True(window.FindName("ScreenshotButton") is System.Windows.Controls.Button, "标题下方没有截图按钮");
        True(window.FindName("ClearClipboardButton") is System.Windows.Controls.Button, "剪贴板清除按钮缺失");
        True(window.FindName("MainMinimizeButton") is System.Windows.Controls.Button, "主窗口最小化按钮缺失");
        var pin = (System.Windows.Controls.Button)window.FindName("PinButton");
        True(pin.Content is System.Windows.Controls.TextBlock, "置顶按钮没有使用图钉图标");
        window.Topmost = true;
        typeof(MainWindow).GetMethod("UpdatePinVisual", PrivateInstance)!.Invoke(window, null);
        Equal(((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color,
            ((System.Windows.Media.SolidColorBrush)pin.Foreground).Color);
        Equal(((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("AccentSubtleBrush")).Color,
            ((System.Windows.Media.SolidColorBrush)pin.Background).Color);
        True(string.Equals("取消窗口置顶", pin.ToolTip?.ToString(), StringComparison.Ordinal),
            "置顶按钮激活提示没有更新");
        window.Topmost = false;
        typeof(MainWindow).GetMethod("UpdatePinVisual", PrivateInstance)!.Invoke(window, null);
        var minimize = (System.Windows.Controls.Button)window.FindName("MainMinimizeButton");
        var close = (System.Windows.Controls.Button)window.FindName("MainCloseButton");
        Equal(
            ((System.Windows.Controls.TextBlock)pin.Content).FontFamily.Source,
            ((System.Windows.Controls.TextBlock)minimize.Content).FontFamily.Source);
        Equal(
            ((System.Windows.Controls.TextBlock)minimize.Content).FontFamily.Source,
            ((System.Windows.Controls.TextBlock)close.Content).FontFamily.Source);
        var settingsButton = (System.Windows.Controls.Button)window.FindName("SettingsButton");
        Equal("\uE713", ((System.Windows.Controls.TextBlock)settingsButton.Content).Text);

        var settingsField = typeof(MainWindow).GetField(
            "_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var applyHotkey = typeof(MainWindow).GetMethod(
            "ApplyHotkeyRegistration",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        settingsField!.SetValue(window, new AppSettings { HotkeyEnabled = false });
        applyHotkey!.Invoke(window, null);
        Equal("截图快捷键已关闭", ((System.Windows.Controls.TextBlock)window.FindName("HotkeyText")).Text);

        var a = new DrawerCardModel("A", "未命名", null);
        var b = new DrawerCardModel("B", "未命名", null);
        Equal(Visibility.Collapsed, a.DeleteVisibility);
        Equal(Visibility.Visible, b.DeleteVisibility);
        window.Hide();
    }

    private static void BoardWindowConstructs()
    {
        var directory = CreateTempDirectory();
        BoardWindow? window = null;
        try
        {
            var paths = new AppDataPaths(directory);
            var repository = new BoardRepository(paths);
            repository.InitializeAsync().GetAwaiter().GetResult();
            var imports = new BoardImportService(new AssetLibraryService(paths, repository), repository);
            window = new BoardWindow("A", repository, imports);
            Equal(0d, window.Opacity);
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
                typeof(BoardWindow).GetMethod("OnLoaded", PrivateInstance | System.Reflection.BindingFlags.DeclaredOnly)!);
            window.Measure(new System.Windows.Size(800, 600));
            window.Arrange(new Rect(0, 0, 800, 600));
            window.UpdateLayout();
            Equal("画板 A", window.Title);
            var frame = (System.Windows.Controls.Border)window.FindName("BoardWindowFrame");
            Equal(9d, frame.Margin.Left);
            Equal(1d, frame.BorderThickness.Left);
            True(frame.Effect is System.Windows.Media.Effects.DropShadowEffect,
                "画板默认没有细边框和阴影");
            var applyWindowFrame = typeof(BoardWindow).GetMethod(
                "ApplyWindowFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            applyWindowFrame.Invoke(window, new object[] { false });
            Equal(0d, frame.Margin.Left);
            True(frame.Effect is null, "关闭画板边框和阴影后视觉效果仍残留");
            applyWindowFrame.Invoke(window, new object[] { true });
            True(frame.Effect is System.Windows.Media.Effects.DropShadowEffect,
                "重新开启画板边框和阴影后没有恢复");
            True(window.FindName("FitButton") is null, "适应按钮仍留在画板顶栏");
            True(window.FindName("ArrangeButton") is null, "排布按钮仍留在画板顶栏");
            True(window.FindName("LayerTopButton") is null, "层级按钮仍留在画板顶栏");
            True(window.FindName("UndoButton") is null, "撤回按钮仍留在画板顶栏");
            var boardSurface = (System.Windows.Controls.Grid)window.FindName("BoardSurface");
            var undoMenu = boardSurface.ContextMenu!.Items
                .OfType<System.Windows.Controls.MenuItem>()
                .Single(x => Equals(x.Header, "撤回"));
            Equal("Ctrl+Z", undoMenu.InputGestureText);
            True(boardSurface.ContextMenu.Template is not null &&
                 boardSurface.ContextMenu.Background == System.Windows.Media.Brushes.Transparent,
                "画板右键菜单没有使用圆角自定义模板");
            AssertRoundedMenuShadow(boardSurface.ContextMenu);
            True(window.FindName("SelectToolButton") is System.Windows.Controls.Button,
                "画板选择工具按钮缺失");
            True(window.FindName("TextToolButton") is System.Windows.Controls.Button,
                "画板注释工具按钮缺失");
            True(window.FindName("DrawToolButton") is System.Windows.Controls.Button,
                "画板绘制工具按钮缺失");
            Equal(Visibility.Collapsed,
                ((System.Windows.Controls.Border)window.FindName("TextPalette")).Visibility);
            Equal(Visibility.Collapsed,
                ((System.Windows.Controls.Border)window.FindName("DrawingPalette")).Visibility);
            var setToolMode = typeof(BoardWindow).GetMethod(
                "SetToolMode",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            setToolMode.Invoke(window, new object[] { BoardToolMode.Text });
            Equal(Visibility.Collapsed,
                ((System.Windows.Controls.Border)window.FindName("TextPalette")).Visibility);
            var textSizes = (System.Windows.Controls.ComboBox)window.FindName("TextSizeCombo");
            var textFonts = (System.Windows.Controls.ComboBox)window.FindName("TextFontCombo");
            True(textSizes.Items.Count >= 15, "字号选择仍然过少");
            True(textFonts.Items.Count > 4, "字体列表没有读取系统字体");
            True(window.FindName("TextColorPreview") is System.Windows.Controls.Border,
                "文字颜色预览条缺失");
            True(window.FindName("TextHighlightPreview") is null,
                "高亮颜色按钮仍然存在");
            True(window.FindName("TextBackgroundPreview") is System.Windows.Controls.Border,
                "文本框背景颜色预览条缺失");
            True(!textSizes.IsEditable && !textFonts.IsEditable,
                "字号或字体下拉框仍处于可能拦截下拉按钮的编辑模式");
            textSizes.ApplyTemplate();
            var sizeToggle = (System.Windows.Controls.Primitives.ToggleButton?)
                textSizes.Template.FindName("Toggle", textSizes);
            True(sizeToggle is not null, "字号下拉按钮模板缺失");
            True(System.Windows.Data.BindingOperations.GetBindingExpression(
                    sizeToggle!, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty) is not null,
                "字号下拉按钮没有绑定弹出列表状态");
            var moreButton = (System.Windows.Controls.Button)window.FindName("TextMoreButton");
            True(moreButton.ContextMenu is null, "更多功能仍使用旧式系统右键菜单");
            True(window.FindName("TextMorePopup") is System.Windows.Controls.Primitives.Popup,
                "更多功能浮动面板缺失");
            True(window.FindName("ClearTextFormattingButton") is System.Windows.Controls.Button,
                "清除文字格式图标按钮缺失");
            setToolMode.Invoke(window, new object[] { BoardToolMode.Pen });
            var drawingPalette = (System.Windows.Controls.Border)window.FindName("DrawingPalette");
            Equal(Visibility.Visible, drawingPalette.Visibility);
            window.UpdateLayout();
            True(drawingPalette.ActualWidth <= 800, "绘制工具栏超出测试窗口宽度");
            setToolMode.Invoke(window, new object[] { BoardToolMode.Select });
            Equal(Visibility.Collapsed, drawingPalette.Visibility);
            var boardPin = (System.Windows.Controls.Button)window.FindName("PinButton");
            Equal("\uE718", ((System.Windows.Controls.TextBlock)boardPin.Content).Text);
            True(window.FindName("BoardMinimizeButton") is System.Windows.Controls.Button, "画板最小化按钮缺失");
            True(window.FindName("BoardCloseButton") is System.Windows.Controls.Button, "画板关闭按钮缺失");
            True(window.FindName("ToolbarHotZone") is null,
                "画板顶栏仍有透明热区拦截框选");
            var toolbar = (System.Windows.Controls.Border)window.FindName("Toolbar");
            True(toolbar.IsHitTestVisible && toolbar.Opacity == 1,
                "画板顶栏首次打开时没有保持展开");
            var toolbarToggle = window.FindName("ToolbarToggleButton") as System.Windows.Controls.Button
                ?? throw new InvalidOperationException("画板顶栏收展按钮缺失");
            Equal(34d * .6, toolbarToggle.Width, .001);
            Equal(17d * .6, toolbarToggle.Height, .001);
            var toolbarArrow = (System.Windows.Shapes.Path)window.FindName("ToolbarToggleArrow");
            Equal(9d * .6, toolbarArrow.Width, .001);
            Equal(5d * .6, toolbarArrow.Height, .001);
            toolbarToggle.ApplyTemplate();
            var toggleChrome = (System.Windows.Controls.Border)toolbarToggle.Template.FindName(
                "ToggleChrome", toolbarToggle);
            Equal(0d, toggleChrome.BorderThickness.Top);
            var arrowRotation = (System.Windows.Media.RotateTransform)window.FindName(
                "ToolbarToggleArrowRotate");
            Equal(180d, arrowRotation.Angle);
            True(window.FindName("ToolbarTranslate") is System.Windows.Media.TranslateTransform,
                "画板顶栏缺少滑动动画变换");
            True(window.FindName("ToolbarToggleTranslate") is System.Windows.Media.TranslateTransform,
                "画板顶栏收展按钮缺少跟随动画");
            var showToolbar = typeof(BoardWindow).GetMethod(
                "ShowToolbar",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            showToolbar.Invoke(window, new object[] { false });
            True(!toolbar.IsHitTestVisible,
                "收起后的画板顶栏仍然拦截画板输入");
            showToolbar.Invoke(window, new object[] { true });
            True(toolbar.IsHitTestVisible, "重新展开后的画板顶栏无法交互");
            typeof(BoardWindow).GetMethod(
                "ApplyBoardShortcuts",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, new object[] { BoardShortcutCatalog.CreateDefaults() });
            var arrangeMenu = boardSurface.ContextMenu.Items
                .OfType<System.Windows.Controls.MenuItem>()
                .Single(x => Equals(x.Header, "自动排布"));
            Equal("Ctrl+Alt+G", arrangeMenu.InputGestureText);
            var textMenu = boardSurface.ContextMenu.Items
                .OfType<System.Windows.Controls.MenuItem>()
                .Single(x => Equals(x.Header, "添加注释"));
            Equal("T", textMenu.InputGestureText);
            var settingsMenus = boardSurface.ContextMenu.Items
                .OfType<System.Windows.Controls.MenuItem>()
                .Where(x => Equals(x.Header, "设置…")).ToArray();
            Equal(1, settingsMenus.Length);
            True(window.FindName("ResetRotationMenuItem") is System.Windows.Controls.MenuItem,
                "重置旋转菜单缺失");
            True(window.FindName("ResetSizeMenuItem") is System.Windows.Controls.MenuItem,
                "重置大小菜单缺失");
            True(window.FindName("ResetImageMenuItem") is System.Windows.Controls.MenuItem,
                "完整重置菜单缺失");
            True(window.FindName("GroupSelectionRectangle") is System.Windows.Shapes.Rectangle,
                "多选范围框缺失");
            var rightDragHandler = typeof(BoardWindow).GetMethod(
                "OnBoardRightMouseMove",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            True(rightDragHandler is not null, "右键拖动画板窗口处理缺失");
            window.ReloadAsync().GetAwaiter().GetResult();
            Equal("画板 未命名 A", window.Title);
            repository.UpdateDrawerNameAsync("A", "机械").GetAwaiter().GetResult();
            window.ReloadAsync().GetAwaiter().GetResult();
            Equal("画板 机械 A", window.Title);

            var assetPath = Path.Combine(paths.Assets, "visual.png");
            using (var bitmap = CreateBitmap()) bitmap.Save(assetPath);
            var asset = new AssetRecord(
                "visual", "visual-hash", ".png", "visual.png", 16, 12, DateTime.UtcNow);
            repository.UpsertAssetAsync(asset).GetAwaiter().GetResult();
            repository.AddItemsAsync(new[]
            {
                new BoardItem
                {
                    Id = "visual-item", DrawerId = "A", AssetId = asset.Id,
                    Width = 200, Height = 80, Rotation = 23
                }
            }).GetAwaiter().GetResult();
            window.ReloadAsync().GetAwaiter().GetResult();
            var world = (System.Windows.Controls.Canvas)window.FindName("WorldCanvas");
            var itemBorder = (System.Windows.Controls.Border)world.Children[0];
            var itemImage = ((System.Windows.Controls.Grid)itemBorder.Child).Children
                .OfType<System.Windows.Controls.Image>().Single();
            Equal(System.Windows.Media.Stretch.Fill, itemImage.Stretch);
            True(itemImage.CacheMode is null,
                "画板图片仍启用了可能重复占用显存的视觉缓存");
            Equal(23d, ((System.Windows.Media.RotateTransform)itemBorder.RenderTransform).Angle);
            var interactiveQuality = typeof(BoardWindow).GetMethod(
                "BeginContinuousInteraction",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            True(interactiveQuality is not null, "画板缺少交互渲染质量切换");
            interactiveQuality!.Invoke(window, null);
            Equal(System.Windows.Media.BitmapScalingMode.LowQuality,
                System.Windows.Media.RenderOptions.GetBitmapScalingMode(world));
            True(window.FindName("ViewTransform") is System.Windows.Media.MatrixTransform,
                "画板视口没有使用单矩阵变换");
            True(typeof(BoardWindow).GetMethod(
                    "RequestViewportRender",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) is not null,
                "画板缺少逐帧视口合并");

            var rotationHandles = typeof(BoardWindow).GetField(
                "_rotationHandles",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window);
            Equal(4, ((System.Collections.IDictionary)rotationHandles!).Count);

            var selected = (HashSet<string>)typeof(BoardWindow).GetField(
                "_selected",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            selected.Add("visual-item");
            typeof(BoardWindow).GetMethod(
                "UpdateSelectionVisuals",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            var resizeHandles = (System.Collections.IDictionary)typeof(BoardWindow).GetField(
                "_resizeHandles",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            foreach (System.Windows.Controls.Primitives.Thumb handle in resizeHandles.Values)
                Equal(23d, ((System.Windows.Media.RotateTransform)handle.RenderTransform).Angle);

            var noteDocument = RichTextDocumentService.CreateDefault();
            noteDocument.Blocks.Clear();
            noteDocument.Blocks.Add(new System.Windows.Documents.Paragraph(
                new System.Windows.Documents.Run("画板注释")));
            repository.AddTextItemsAsync(new[]
            {
                new BoardTextItem
                {
                    Id = "visual-note", DrawerId = "A", X = 240, Width = 180, Height = 90,
                    ZIndex = 1, DocumentData = RichTextDocumentService.Save(noteDocument)
                }
            }).GetAwaiter().GetResult();
            repository.AddDrawingItemsAsync(new[]
            {
                new BoardDrawingItem
                {
                    Id = "visual-drawing", DrawerId = "A", X = 40, Y = 130,
                    Width = 160, Height = 80, ZIndex = 2, Kind = BoardDrawingKind.Arrow,
                    PointsJson = "[{\"X\":0.05,\"Y\":0.5,\"Pressure\":1},{\"X\":0.95,\"Y\":0.5,\"Pressure\":1}]"
                }
            }).GetAwaiter().GetResult();
            window.ReloadAsync().GetAwaiter().GetResult();
            Equal(3, world.Children.Count);
            True(world.Children.OfType<System.Windows.Controls.Border>()
                .Any(x => x.Child is System.Windows.Controls.RichTextBox),
                "富文本注释没有渲染到画板");
            True(world.Children.OfType<System.Windows.Controls.Border>()
                .Any(x => x.Child?.GetType().Name == "BoardDrawingVisual"),
                "绘制元素没有渲染到画板");
            selected.Clear();
            selected.Add("visual-item");
            selected.Add("visual-note");
            selected.Add("visual-drawing");
            typeof(BoardWindow).GetMethod(
                "UpdateSelectionVisuals",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            Equal(Visibility.Visible,
                ((System.Windows.Shapes.Rectangle)window.FindName("GroupSelectionRectangle")).Visibility);
        }
        finally
        {
            if (window is not null)
            {
                SetDrawingField(window, "_closeAfterDrawingSave", true);
                window.Close();
            }
            // SQLite/native image finalizers can briefly outlive a constructor test.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(directory, true); break; }
                catch (IOException) when (attempt < 40)
                {
                    if (attempt == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
                    Thread.Sleep(50);
                }
            }
        }
    }

    private static void BoardSettingsWindowConstructs()
    {
        var window = new BoardSettingsWindow("#202124", .8, true, true);
        window.Measure(new System.Windows.Size(500, 570));
        window.Arrange(new Rect(0, 0, 500, 570));
        window.UpdateLayout();
        Equal("画板设置", window.Title);
        Equal(WindowStyle.None, window.WindowStyle);
        var pastel = (System.Windows.Controls.WrapPanel)window.FindName("PastelPalettePanel");
        Equal(5, pastel.Children.Count);
        var grayscale = (System.Windows.Controls.WrapPanel)window.FindName("GrayscalePanel");
        Equal(3, grayscale.Children.Count);
        Equal(8, grayscale.Children.Count + pastel.Children.Count);
        var opacity = (System.Windows.Controls.Slider)window.FindName("OpacitySlider");
        True(opacity.IsMoveToPointEnabled, "背景透明度滑轨没有启用点击跳转");
        True(!opacity.IsHitTestVisible, "背景透明度应由整条可点击轨道统一处理");
        var opacityTrack = (System.Windows.FrameworkElement)window.FindName("BackgroundOpacityTrack");
        opacityTrack.Width = 200;
        opacityTrack.Measure(new System.Windows.Size(200, 18));
        opacityTrack.Arrange(new Rect(0, 0, 200, 18));
        True(opacityTrack.ActualWidth > 0, "背景透明度轨道没有参与布局");
        var updateOpacity = typeof(BoardSettingsWindow).GetMethod(
            "UpdateOpacity",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        True(updateOpacity is not null, "背景透明度缺少整条轨道拖动逻辑");
        updateOpacity!.Invoke(window, [opacityTrack.ActualWidth / 2]);
        Equal(55d, opacity.Value, .6);
        var opacityColorTrack = (System.Windows.Controls.Border)window.FindName("BackgroundOpacityColorTrack");
        True(opacityColorTrack.Background is System.Windows.Media.LinearGradientBrush,
            "背景透明度轨道没有显示当前背景颜色渐变");
        var previewCount = 0;
        window.PreviewChanged += (_, _, _) => previewCount++;
        opacity.Value = 64;
        True(previewCount > 0, "画板外观没有即时预览颜色和透明度变化");
        True(window.FindName("ApplyButton") is System.Windows.Controls.Button, "调色板应用按钮缺失");
        True(window.FindName("CustomColorButton") is System.Windows.Controls.Button, "自定义色盘入口缺失");
        var affectsImages = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("AffectImagesToggle");
        True(affectsImages.IsChecked == true, "透明度影响图片选项没有加载");
        var affectsImagesRow = (System.Windows.Controls.Grid)System.Windows.Media.VisualTreeHelper.GetParent(affectsImages);
        True(affectsImagesRow.Children.OfType<System.Windows.Controls.TextBlock>()
                 .Any(text => text.Text == "透明度影响图片" &&
                              System.Windows.Controls.Grid.GetRow(text) == System.Windows.Controls.Grid.GetRow(affectsImages)),
            "透明度影响图片文字与开关没有位于同一水平行");
        True(ReferenceEquals(affectsImages.Style, Application.Current.FindResource("SwitchStyle")),
            "图片透明度开关没有复用小窗开关样式");
        Equal(42d, affectsImages.Width);
        Equal(23d, affectsImages.Height);
        affectsImages.ApplyTemplate();
        var switchTranslate = (System.Windows.Media.TranslateTransform)
            affectsImages.Template.FindName("ThumbTranslate", affectsImages);
        Equal(19d, switchTranslate.X, .01);
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        True(!switchTranslate.HasAnimatedProperties,
            "开关在界面首次打开时错误播放了从关到开的动画");
        bool? lastAffectsImages = null;
        window.PreviewChanged += (_, _, enabled) => lastAffectsImages = enabled;
        affectsImages.IsChecked = false;
        True(lastAffectsImages == false, "关闭图片透明度开关没有即时预览");
        True(switchTranslate.HasAnimatedProperties, "关闭图片透明度开关没有滑动过渡");
        affectsImages.IsChecked = true;
        True(lastAffectsImages == true, "开启图片透明度开关没有即时预览");
        True(switchTranslate.HasAnimatedProperties, "开启图片透明度开关没有滑动过渡");
        var frameToggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("WindowFrameToggle");
        True(frameToggle.IsChecked == true, "画板边框和阴影没有默认开启");
        True(ReferenceEquals(frameToggle.Style, Application.Current.FindResource("SwitchStyle")),
            "画板边框开关没有复用统一动画样式");
        bool? lastFramePreview = null;
        window.WindowFramePreviewChanged += enabled => lastFramePreview = enabled;
        frameToggle.IsChecked = false;
        True(lastFramePreview == false, "关闭画板边框和阴影没有即时预览");
        frameToggle.IsChecked = true;
        True(lastFramePreview == true, "开启画板边框和阴影没有即时预览");
        True(window.FindName("GameCompatibleCheckBox") is null,
            "画板外观中仍残留兼容渲染选项");
        window.Close();
    }

    private static void CustomColorPickerConstructs()
    {
        var window = new CustomColorPickerWindow("#336699");
        window.Measure(new System.Windows.Size(360, 638));
        window.Arrange(new Rect(0, 0, 360, 638));
        window.UpdateLayout();
        Equal("选择颜色", window.Title);
        Equal(WindowStyle.None, window.WindowStyle);
        True(window.AllowsTransparency, "简洁调色器仍使用原生标题栏");
        True(window.FindName("SaturationValueCanvas") is System.Windows.Controls.Canvas,
            "饱和度明度色盘缺失");
        var hueBase = (System.Windows.Controls.Border)window.FindName("HueBase");
        Equal(hueBase.Width, hueBase.Height);
        Equal(280d, hueBase.Width);
        True(window.FindName("HueTrack") is System.Windows.Controls.Grid, "色相滑轨缺失");
        var hueTrack = (System.Windows.Controls.Grid)window.FindName("HueTrack");
        var alphaTrack = (System.Windows.Controls.Grid)window.FindName("AlphaTrack");
        Equal(hueTrack.Width, alphaTrack.Width);
        Equal(280d, hueTrack.Width);
        var dragRegion = (System.Windows.Controls.Grid)window.FindName("TitleDragRegion");
        True(dragRegion.MinHeight >= 46, "窗口顶部可拖动区域仍然过窄");
        True(window.FindName("HexTextBox") is System.Windows.Controls.TextBox, "HEX 输入框缺失");
        True(window.FindName("AlphaSlider") is System.Windows.Controls.Slider,
            "颜色透明度选项缺失");
        var alphaSlider = (System.Windows.Controls.Slider)window.FindName("AlphaSlider");
        True(!alphaSlider.IsHitTestVisible && !alphaSlider.Focusable,
            "透明度轨道仍由默认 Slider 抢占点击或显示焦点虚线");
        typeof(CustomColorPickerWindow).GetMethod("UpdateAlpha",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, new object[] { alphaTrack.Width * .37 });
        Equal(37d, alphaSlider.Value, .01);
        True(window.FindName("AlphaText") is System.Windows.Controls.TextBlock,
            "颜色透明度数值缺失");
        var eyedropper = (System.Windows.Controls.Button)window.FindName("EyedropperButton");
        True(eyedropper is not null, "吸色按钮缺失");
        True(eyedropper!.MinHeight == 0 && eyedropper.Height <= 28,
            "吸色图标按钮仍可能被色相行裁切");
        var eyedropperGlyph = (System.Windows.Controls.Viewbox)window.FindName("EyedropperGlyph");
        True(eyedropperGlyph.Width <= 13 && eyedropperGlyph.Height <= 13,
            "吸色图标内部图形仍然过大");
        True(window.FindName("SavedColorPalette") is System.Windows.Controls.Border,
            "通用收藏色板缺失");
        var savedPalette = (System.Windows.Controls.Border)window.FindName("SavedColorPalette");
        savedPalette.Measure(new System.Windows.Size(316, 70));
        True(savedPalette.DesiredSize.Height >= 64,
            $"通用收藏色板内容高度不足：{savedPalette.DesiredSize.Height:0.##}");
        var savedColors = (System.Windows.Controls.ItemsControl)window.FindName("SavedColorList");
        Equal(3, savedColors.Items.Count);
        Equal(System.Windows.HorizontalAlignment.Center, savedColors.HorizontalAlignment);
        var addEntry = savedColors.Items[^1];
        True((bool)(addEntry.GetType().GetProperty("IsAdd")?.GetValue(addEntry) ?? false),
            "收藏当前颜色的加号没有进入固定网格");
        var palettePanel = (System.Windows.Controls.WrapPanel)savedColors.ItemsPanel.LoadContent();
        Equal(280d, palettePanel.Width);
        Equal(56d, palettePanel.Height);
        Equal(28d, palettePanel.ItemWidth);
        Equal(28d, palettePanel.ItemHeight);
        var swatchButton = (System.Windows.Controls.Button)savedColors.ItemTemplate.LoadContent();
        swatchButton.DataContext = savedColors.Items[0];
        swatchButton.Measure(new System.Windows.Size(28, 28));
        swatchButton.Arrange(new Rect(0, 0, 28, 28));
        var swatchGrid = (System.Windows.Controls.Grid)swatchButton.Content;
        var swatch = (System.Windows.Controls.Border)swatchGrid.Children[0];
        Equal(1d, swatch.BorderThickness.Left);
        Equal(System.Windows.Application.Current.FindResource("ControlBorderBrush"), swatch.BorderBrush);
        Equal(Visibility.Visible, swatch.Visibility);
        var addButton = (System.Windows.Controls.Button)savedColors.ItemTemplate.LoadContent();
        addButton.DataContext = addEntry;
        addButton.Measure(new System.Windows.Size(28, 28));
        addButton.Arrange(new Rect(0, 0, 28, 28));
        var addGrid = (System.Windows.Controls.Grid)addButton.Content;
        var addTile = (System.Windows.Controls.Border)addGrid.Children[1];
        Equal(System.Windows.Application.Current.FindResource("InputBrush"), addTile.Background);
        var deleteMenu = (System.Windows.Controls.MenuItem)swatchButton.ContextMenu.Items[0];
        AssertRoundedMenuShadow(swatchButton.ContextMenu);
        var deleteHeader = (System.Windows.Controls.Grid)deleteMenu.Header;
        True(deleteHeader.Children.OfType<System.Windows.Controls.Viewbox>()
                 .All(x => x.VerticalAlignment == VerticalAlignment.Center) &&
             deleteHeader.Children.OfType<System.Windows.Controls.TextBlock>()
                 .All(x => x.VerticalAlignment == VerticalAlignment.Center),
            "删除色块菜单的图标与文字没有垂直对齐");
        Equal(20, (int)(typeof(CustomColorPickerWindow).GetField(
            "MaxSavedColors",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetRawConstantValue() ?? 0));
        True(window.FindName("ColorApplyButton") is System.Windows.Controls.Button,
            "颜色应用按钮缺失");
        True(window.FindName("ColorCancelButton") is System.Windows.Controls.Button,
            "颜色取消按钮缺失");
        var colorDialogButtons = (System.Windows.Controls.StackPanel)window.FindName("ColorDialogButtons");
        Equal(HorizontalAlignment.Right, colorDialogButtons.HorizontalAlignment);
        True(ReferenceEquals(colorDialogButtons.Children[^1], window.FindName("ColorApplyButton")),
            "颜色应用按钮没有放在操作区最右侧");
        True(window.FindName("CopyColorButton") is System.Windows.Controls.Button,
            "复制颜色值按钮缺失");
        var copyFeedback = (System.Windows.Controls.Primitives.Popup)window.FindName("CopyFeedbackPopup");
        True(!copyFeedback.IsHitTestVisible, "已复制提示会拦截用户操作");
        var formats = (System.Windows.Controls.ComboBox)window.FindName("ColorFormatCombo");
        Equal(5, formats.Items.Count);
        formats.ApplyTemplate();
        True(formats.Template.FindName("Toggle", formats) is System.Windows.Controls.Primitives.ToggleButton,
            "色值格式选择器没有使用圆角自定义按钮");
        True(formats.Template.FindName("PART_Popup", formats) is System.Windows.Controls.Primitives.Popup,
            "色值格式选择器没有使用圆角弹层");
        True(formats.ItemContainerStyle is not null,
            "色值格式下拉项目仍使用系统直角样式");
        formats.SelectedIndex = 1;
        True(((System.Windows.Controls.TextBox)window.FindName("HexTextBox")).Text.Contains(','),
            "RGB 色值格式没有生效");
        formats.SelectedIndex = 4;
        True(((System.Windows.Controls.TextBox)window.FindName("HexTextBox")).Text.StartsWith("rgba(", StringComparison.Ordinal),
            "CSS 色值格式没有生效");
        True(typeof(CustomColorPickerWindow).GetMethod(
                "OnApplyHexClick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) is null,
            "调色器仍保留应用按钮逻辑");
        var changed = 0;
        window.ColorChanged += (_, _) => changed++;
        typeof(CustomColorPickerWindow).GetMethod(
            "RefreshVisuals",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null);
        True(changed > 0, "调色器修改颜色时没有即时通知画板");
        window.Close();
    }

    private static void AssertRoundedMenuShadow(System.Windows.Controls.ContextMenu menu)
    {
        True(!menu.HasDropShadow, "圆角菜单仍启用系统直角阴影");
        menu.ApplyTemplate();
        var chrome = (System.Windows.Controls.Border)menu.Template.FindName("MenuChrome", menu);
        var shadow = (System.Windows.Media.Effects.DropShadowEffect)chrome.Effect;
        var requiredSpace = shadow.BlurRadius / 2 + shadow.ShadowDepth;
        True(chrome.Margin.Left >= requiredSpace && chrome.Margin.Bottom >= requiredSpace,
            "圆角菜单阴影没有预留绘制空间，可能被裁成直角");
        True(chrome.CornerRadius.BottomRight >= 8, "菜单右下角缺少圆角");
    }

    private static void ApplicationIconAssets()
    {
        using var stream = Application.GetResourceStream(
            new Uri("/MuseBox;component/Assets/app-icon.ico", UriKind.Relative))!.Stream;
        using var appCopy = new MemoryStream();
        stream.CopyTo(appCopy);
        var appBytes = appCopy.ToArray();
        appCopy.Position = 0;
        AssertIconFrames(appCopy);
        var scenePath = Path.Combine(AppContext.BaseDirectory, "Assets", "scene-icon.ico");
        True(File.Exists(scenePath), "场景文件图标没有随程序输出");
        using var sceneStream = File.OpenRead(scenePath);
        AssertIconFrames(sceneStream);
        True(!File.ReadAllBytes(scenePath).SequenceEqual(appBytes),
            "场景文件图标错误复用了应用图标");
        using var immersiveStream = Application.GetResourceStream(
            new Uri("/MuseBox;component/Assets/immersive-collection-icon.png", UriKind.Relative))!.Stream;
        using var immersive = new Bitmap(immersiveStream);
        Equal(256, immersive.Width);
        Equal(256, immersive.Height);
        True(immersive.GetPixel(0, 0).A == 0, "沉浸模式图标没有透明背景");
        True(immersive.GetPixel(128, 128).A > 0, "沉浸模式图标中心意外透明");
        var iconPixel = immersive.GetPixel(128, 128);
        True(iconPixel.R == iconPixel.G && iconPixel.G == iconPixel.B,
            "沉浸模式图标仍然包含彩色像素");
        var window = new MainWindow();
        var button = (System.Windows.Controls.Button)window.FindName("CollectionModeButton");
        True(button.Content is System.Windows.Controls.Viewbox { Child: System.Windows.Controls.Grid icon } &&
             icon.Children.OfType<System.Windows.Shapes.Path>().Count() == 2,
            "沉浸模式按钮没有使用简化的星光与收集盒图标");
        window.Close();
    }

    private static void AssertIconFrames(Stream stream)
    {
        using var reader = new BinaryReader(stream);
        Equal((ushort)0, reader.ReadUInt16());
        Equal((ushort)1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        var expectedSizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        Equal(expectedSizes.Length, (int)count);
        var frames = new List<(int Size, uint Length, uint Offset)>();
        for (var index = 0; index < count; index++)
        {
            var encodedWidth = reader.ReadByte();
            var encodedHeight = reader.ReadByte();
            var size = encodedWidth == 0 ? 256 : encodedWidth;
            Equal(expectedSizes[index], size);
            Equal(encodedWidth, encodedHeight);
            reader.ReadUInt16();
            Equal((ushort)1, reader.ReadUInt16());
            Equal((ushort)32, reader.ReadUInt16());
            frames.Add((size, reader.ReadUInt32(), reader.ReadUInt32()));
        }
        foreach (var frame in frames)
        {
            True(frame.Offset + frame.Length <= stream.Length, "图标帧数据不完整");
            stream.Position = frame.Offset;
            True(reader.ReadBytes(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "图标帧不是有效 PNG");
        }
        stream.Position = 0;
        using var icon = new Icon(stream, 32, 32);
        Equal(32, icon.Width);
        using var bitmap = icon.ToBitmap();
        True(bitmap.GetPixel(16, 16).A > 0, "图标中心意外变成透明");
    }

    private static void EyedropperOverlayConstructs()
    {
        var window = new EyedropperOverlayWindow();
        window.Measure(new System.Windows.Size(800, 600));
        window.Arrange(new Rect(0, 0, 800, 600));
        window.UpdateLayout();
        True(window.Topmost, "吸色覆盖层没有置顶");
        True(window.FindName("SampleBadge") is System.Windows.Controls.Border, "吸色色值预览缺失");
        window.Close();
    }

    private static void ClipboardReadDoesNotThrow()
    {
        using var bitmap = new ClipboardImageService().ReadImage().Bitmap;
    }

    private static Bitmap CreateBitmap()
    {
        var bitmap = new Bitmap(16, 12);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.CornflowerBlue);
        return bitmap;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MuseBox.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Equal(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
