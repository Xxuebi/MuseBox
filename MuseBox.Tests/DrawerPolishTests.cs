using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using ScreenshotCollector.Controls;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using RadioButton = System.Windows.Controls.RadioButton;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;
using ToolTip = System.Windows.Controls.ToolTip;
using Size = System.Windows.Size;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static void DrawerLettersAndSettings() => WithMainDrawerWindow((window, repository) =>
    {
        var settings = new AppSettings { HotkeyEnabled = false, ShowDrawerLetters = true };
        typeof(MainWindow).GetField("_settings", PrivateInstance)!.SetValue(window, settings);
        var content = ArrangeMain(window, 360, 500);
        MainDrawers(window)[0].DisplayName = "机械";
        var root = MainDescendants(content).Single(x => x.Name == "DrawerRoot" && x.Tag?.ToString() == "A");
        var letter = MainDescendants(root).Single(x => x.Name == "DrawerLetter");
        var open = MainDescendants(root).Single(x => x.Name == "DrawerOpenButton");
        True(letter.TranslatePoint(new Point(), open).X < 12, "字母没有位于打开区域左侧");
        True(!MainDescendants(root).Any(x => x.Name == "DrawerRenameButton"), "旧的重命名按钮还在底栏");
        True(MainDescendants(root).Any(x => x.Name == "DrawerSettingsButton"), "缺少设置按钮");
        SaveDrawingTestVisual(content, "drawer-footer-settings.png", false);
        settings.ShowDrawerLetters = false;
        MainCall(window, "ApplyDrawerLetterVisibility");
        content.UpdateLayout();
        Equal(Visibility.Collapsed, letter.Visibility);
        AwaitMainTask(window, "AddDrawerAsync");
        True(MainDrawers(window).All(x => x.LetterVisibility == Visibility.Collapsed), "新增抽屉未沿用隐藏字母设置");
        SaveDrawingTestVisual(content, "drawer-no-letters.png", false);
        var menu = (DrawerMenuPopup)MainCall(window, "CreateDrawerMenu", MainDrawers(window).Last())!;
        Equal(7, menu.Actions.Children.Count);
        Equal("打开,保存,另存为,重命名,设置封面,删除抽屉", string.Join(',', menu.Actions.Children.OfType<Button>().Select(System.Windows.Automation.AutomationProperties.GetName)));
        True(menu.Child.Effect is System.Windows.Media.Effects.DropShadowEffect, "菜单阴影丢失");
        SaveDrawingTestVisual((FrameworkElement)menu.Child, "drawer-settings-menu.png");
        ((Button)menu.Actions.Children[4]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(MainDrawers(window).Last().IsEditing, "菜单重命名没有进入编辑状态");
        AwaitMainTask(window, "SaveDrawerNameAsync", MainDrawers(window).Last().Id, "新的名称");
        Equal("新的名称", repository.GetDrawersAsync().GetAwaiter().GetResult().Last().DisplayName);
        var protectedMenu = (DrawerMenuPopup)MainCall(window, "CreateDrawerMenu", MainDrawers(window)[0])!;
        True(!protectedMenu.Actions.Children[6].IsEnabled, "保留抽屉 A 的删除保护丢失");
        Equal(420d, ((DispatcherTimer)typeof(MainWindow).GetField("_drawerHoldTimer", PrivateInstance)!.GetValue(window)!).Interval.TotalMilliseconds);
    });

    private static void DrawerReorderPersistenceAndCancel() => WithMainDrawerWindow((window, repository) =>
    {
        var content = ArrangeMain(window, 360, 500);
        var before = MainDrawers(window).Select(x => x.Id).ToArray();
        repository.UpdateDrawerNameAsync("B", "保持名称").GetAwaiter().GetResult();
        var note = new BoardTextItem { DrawerId = "B", DocumentData = "keep-content", WebLink = "https://example.com" };
        repository.AddTextItemsAsync(new[] { note }).GetAwaiter().GetResult();
        var list = (ItemsControl)window.FindName("DrawerList");
        var target = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(3);
        var point = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2),
            (FrameworkElement)window.FindName("DrawerScroll"));
        typeof(MainWindow).GetField("_draggingDrawerId", PrivateInstance)!.SetValue(window, "A");
        typeof(MainWindow).GetField("_drawerOrderBeforeDrag", PrivateInstance)!.SetValue(window, before);
        MainCall(window, "UpdateDrawerReorderAt", point);
        Equal("B,C,D,A", string.Join(',', MainDrawers(window).Select(x => x.Id)));
        AwaitMainTask(window, "FinishDrawerReorderAsync", false);
        Equal("A,B,C,D", string.Join(',', MainDrawers(window).Select(x => x.Id)));
        Equal("A,B,C,D", string.Join(',', repository.GetDrawersAsync().GetAwaiter().GetResult().Select(x => x.Id)));
        MainDrawers(window).Move(0, 3);
        typeof(MainWindow).GetField("_draggingDrawerId", PrivateInstance)!.SetValue(window, "A");
        typeof(MainWindow).GetField("_drawerOrderBeforeDrag", PrivateInstance)!.SetValue(window, before);
        AwaitMainTask(window, "FinishDrawerReorderAsync", true);
        repository.InitializeAsync().GetAwaiter().GetResult();
        Equal("B,C,D,A", string.Join(',', repository.GetDrawersAsync().GetAwaiter().GetResult().Select(x => x.Id)));
        Equal("保持名称", repository.GetDrawersAsync().GetAwaiter().GetResult().First().DisplayName);
        Equal("keep-content", repository.GetTextItemsAsync("B").GetAwaiter().GetResult().Single().DocumentData);
        Equal("https://example.com", repository.GetTextItemsAsync("B").GetAwaiter().GetResult().Single().WebLink);
        AwaitMainTask(window, "ReloadDrawersAsync");
        Equal("B,C,D,A", string.Join(',', MainDrawers(window).Select(x => x.Id)));
        var newDrawer = repository.AddNextDrawerAsync().GetAwaiter().GetResult();
        Equal("E", newDrawer.Id);
        var preserved = repository.GetDrawersAsync().GetAwaiter().GetResult().Select(x => x.Id).ToArray();
        foreach (var invalid in new[] { new[] { "B", "C", "D", "A" }, new[] { "B", "C", "D", "A", "A" }, new[] { "B", "C", "D", "A", "?" } })
        {
            var rejected = false;
            try { repository.UpdateDrawerOrderAsync(invalid).GetAwaiter().GetResult(); }
            catch (ArgumentException) { rejected = true; }
            True(rejected, "非法顺序没有拒绝");
            True(repository.GetDrawersAsync().GetAwaiter().GetResult().Select(x => x.Id).SequenceEqual(preserved), "失败排序修改了数据库");
        }
        // A concurrently added drawer makes this stale UI order invalid: restore the UI preview.
        before = MainDrawers(window).Select(x => x.Id).ToArray();
        MainDrawers(window).Move(0, 2);
        var rolledBack = false;
        try { AwaitMainTask(window, "SaveDrawerOrderAsync", (object)before); }
        catch (ArgumentException) { rolledBack = true; }
        True(rolledBack && MainDrawers(window).Select(x => x.Id).SequenceEqual(before), "保存失败未回滚拖动预览");
    });

    private static void DrawerDownwardFade() => WithMainDrawerWindow((window, repository) =>
    {
        ArrangeMain(window, 360, 500);
        var directory = Path.GetDirectoryName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            (string)typeof(BoardRepository).GetField("_connectionString", PrivateInstance)!.GetValue(repository)!).DataSource)!;
        var path = Path.Combine(directory, "fade-source.png");
        using (var bitmap = CreateBitmap()) bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        AwaitMainTask(window, "UpdateThumbnailAsync", "A", path);
        AwaitMainTask(window, "UpdateThumbnailAsync", "A", path);
        var layer = MainDescendants((FrameworkElement)window.Content).OfType<Canvas>().First(x => x.Name == "AnimationLayer");
        var ghost = layer.Children.OfType<System.Windows.Controls.Image>().Single();
        var translate = ((TransformGroup)ghost.RenderTransform).Children.OfType<TranslateTransform>().Single();
        // WPF may not have ticked the newly attached animation clock yet.
        var frame = new DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        tick.Tick += (_, _) => { if (translate.Y > 0 || elapsed.Elapsed.TotalSeconds > 2) frame.Continue = false; };
        tick.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { tick.Stop(); }
        True(translate.HasAnimatedProperties && translate.Y > 0, "淡出动画没有向下移动");
        Equal(0d, translate.X);
    });

    private static void RoundedTooltipsAndTrayMenu() => WithMainDrawerWindow((window, repository) =>
    {
        var tip = new ToolTip { Content = "打开画板 A · 机械" };
        tip.SetResourceReference(FrameworkElement.StyleProperty, typeof(ToolTip));
        tip.ApplyTemplate();
        var chrome = (Border)tip.Template.FindName("ToolTipChrome", tip);
        True(chrome.CornerRadius.TopLeft >= 8 && !tip.HasDropShadow, "提示框仍使用系统直角外观");
        SaveDrawingTestVisual(tip, "rounded-tooltip.png");
        var menu = (ContextMenu)typeof(App).GetMethod("CreateTrayMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Application.Current, new object[] { repository.GetDrawersAsync().GetAwaiter().GetResult() })!;
        AssertRoundedMenuShadow(menu);
        Equal(5, menu.Items.Count);
        Equal("显示 MuseBox", ((MenuItem)menu.Items[0]).Header.ToString()!);
        var boards = (MenuItem)menu.Items[1];
        Equal(4, boards.Items.Count);
        Equal("A · 未命名", ((MenuItem)boards.Items[0]).Header.ToString()!);
        Equal(2, ((MenuItem)boards.Items[0]).Items.Count);
        Equal("退出画板模式", ((MenuItem)((MenuItem)boards.Items[0]).Items[1]).Header.ToString()!);
        True(!((MenuItem)((MenuItem)boards.Items[0]).Items[1]).IsEnabled, "未打开画板的模式退出项没有禁用");
        Equal("退出画板模式", ((MenuItem)menu.Items[2]).Header.ToString()!);
        True(!((MenuItem)menu.Items[2]).IsEnabled, "没有活动模式时托盘一级退出项没有禁用");
        Equal("退出", ((MenuItem)menu.Items[4]).Header.ToString()!);
        SaveDrawingTestVisual(menu, "rounded-tray-menu.png");
        // Render the actual submenu children without invoking user-owned board windows.
        var submenu = new ContextMenu { Style = menu.Style };
        foreach (var drawer in repository.GetDrawersAsync().GetAwaiter().GetResult())
        {
            var row = new MenuItem { Header = $"{drawer.Id} · {drawer.DisplayName}" };
            row.SetResourceReference(FrameworkElement.StyleProperty, "RoundedMenuItem");
            submenu.Items.Add(row);
        }
        SaveDrawingTestVisual(submenu, "rounded-tray-drawers.png");
    });

    private static void DrawerAndSystemCaptureSettings()
    {
        var settings = new AppSettings { HotkeyEnabled = false, ShowDrawerLetters = false, UseSystemScreenshot = true };
        var dialog = new SettingsWindow(settings);
        try
        {
            Equal(false, ((ToggleButton)dialog.FindName("ShowDrawerLettersToggle")).IsChecked!.Value);
            Equal(true, ((ToggleButton)dialog.FindName("UseSystemScreenshotToggle")).IsChecked!.Value);
            var copy = settings.Copy();
            True(!copy.ShowDrawerLetters && copy.UseSystemScreenshot, "设置复制遗漏新增开关");
            var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(copy))!;
            True(!restored.ShowDrawerLetters && restored.UseSystemScreenshot, "设置序列化遗漏新增开关");
            var defaults = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
            True(defaults.ShowDrawerLetters && !defaults.UseSystemScreenshot, "旧设置默认值改变原有行为");
            dialog.Opacity = 0;
            dialog.ShowActivated = false;
            dialog.Show();
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            dialog.UpdateLayout();
            SaveSettingsSnapshot(dialog, "settings-drawers-system-capture.png");
            dialog.Width = dialog.MinWidth;
            dialog.Height = dialog.MinHeight;
            dialog.UpdateLayout();
            var general = (ScrollViewer)((TabItem)((TabControl)dialog.FindName("SettingsCategories")).Items[0]).Content;
            True(general.ScrollableHeight > 0, "窄窗口常规设置没有提供滚动");
            foreach (var toggleName in new[] { "ShowDrawerLettersToggle", "UseSystemScreenshotToggle" })
            {
                var toggle = (ToggleButton)dialog.FindName(toggleName);
                True(toggle.TranslatePoint(new Point(toggle.ActualWidth, 0), general).X <= general.ActualWidth,
                    "设置开关超出可用宽度");
            }
            SaveSettingsSnapshot(dialog, "settings-drawers-system-capture-narrow.png");
            ((ToggleButton)dialog.FindName("ShowDrawerLettersToggle")).IsChecked = true;
            ((ToggleButton)dialog.FindName("UseSystemScreenshotToggle")).IsChecked = false;
            try { typeof(SettingsWindow).GetMethod("OnSaveClick", PrivateInstance)!.Invoke(dialog, new object[] { dialog, new RoutedEventArgs() }); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException && dialog.ResultSettings is not null) { }
            True(dialog.ResultSettings is { ShowDrawerLetters: true, UseSystemScreenshot: false }, "保存设置没有读取新开关");
        }
        finally { dialog.Close(); }
    }

    private static void AppearanceLanguageStorageAndSwitchAnimation()
    {
        Equal("MuseBox", new DirectoryInfo(AppDataPaths.DefaultRoot).Name);
        Equal("InspirationCollector", new DirectoryInfo(AppDataPaths.LegacyDefaultRoot).Name);
        Equal(AppAppearanceMode.FollowSystem, new AppSettings().AppearanceMode);
        Equal(AppAppearanceMode.Light, ThemeService.Resolve(AppAppearanceMode.FollowSystem, true));
        Equal(AppAppearanceMode.Dark, ThemeService.Resolve(AppAppearanceMode.FollowSystem, false));
        var settings = new AppSettings
        {
            AppearanceMode = AppAppearanceMode.Dark,
            LanguageCode = LanguageService.SimplifiedChinese
        };
        var copy = settings.Copy();
        Equal(AppAppearanceMode.Dark, copy.AppearanceMode);
        Equal(LanguageService.SimplifiedChinese, copy.LanguageCode);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(copy))!;
        Equal(AppAppearanceMode.Dark, restored.AppearanceMode);
        Equal(LanguageService.SimplifiedChinese, LanguageService.Normalize("not-installed"));

        ThemeService.Apply(Application.Current, AppAppearanceMode.Dark);
        var dialog = new SettingsWindow(settings) { Opacity = 0, ShowActivated = false };
        try
        {
            Equal(true, ((RadioButton)dialog.FindName("DarkAppearanceRadio")).IsChecked!.Value);
            Equal(false, ((RadioButton)dialog.FindName("SystemAppearanceRadio")).IsChecked!.Value);
            Equal("白天", ((RadioButton)dialog.FindName("LightAppearanceRadio")).Content.ToString()!);
            Equal("黑夜", ((RadioButton)dialog.FindName("DarkAppearanceRadio")).Content.ToString()!);
            var systemChoice = (RadioButton)dialog.FindName("SystemAppearanceRadio");
            var systemIsLight = ThemeService.SystemAppearanceMode == AppAppearanceMode.Light;
            Equal(systemIsLight ? "系统 · 白天" : "系统 · 黑夜", systemChoice.Content.ToString()!);
            var systemBackground = (SolidColorBrush)systemChoice.Background;
            Equal(systemIsLight ? Color.FromRgb(0xF8, 0xF8, 0xF8) : Color.FromRgb(0x25, 0x26, 0x2A),
                systemBackground.Color);
            Equal(WindowStyle.None, dialog.WindowStyle);
            Equal("简体中文", ((TextBlock)dialog.FindName("LanguageValueText")).Text);
            True(((TextBox)dialog.FindName("StoragePathTextBox")).Text.EndsWith(
                $"{Path.DirectorySeparatorChar}MuseBox", StringComparison.OrdinalIgnoreCase),
                "默认画板保存路径仍然使用旧应用名");
            dialog.Show();
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            ((TabControl)dialog.FindName("SettingsCategories")).SelectedItem =
                dialog.FindName("AppearanceSettingsTab");
            dialog.UpdateLayout();
            SaveSettingsSnapshot(dialog, "settings-appearance-dark.png");
            var toggle = (ToggleButton)dialog.FindName("ShowDrawerLettersToggle");
            toggle.ApplyTemplate();
            var initialTrack = (Border)toggle.Template.FindName("TrackOn", toggle);
            var initialTranslation = (TranslateTransform)toggle.Template.FindName("ThumbTranslate", toggle);
            True(!initialTrack.HasAnimatedProperties && !initialTranslation.HasAnimatedProperties,
                "设置窗口首次显示时开关错误播放了初始化动画");
            toggle.IsChecked = false;
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            toggle.IsChecked = true;
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var onTrack = (Border)toggle.Template.FindName("TrackOn", toggle);
            var translation = (TranslateTransform)toggle.Template.FindName("ThumbTranslate", toggle);
            True(onTrack.HasAnimatedProperties && translation.HasAnimatedProperties,
                "统一开关没有播放底色与滑块过渡动画");
            var drawerMenu = new DrawerMenuPopup();
            var drawerSurface = (Border)drawerMenu.Child;
            Equal(Color.FromRgb(43, 44, 48), ((SolidColorBrush)drawerSurface.Background).Color);
            Equal(Color.FromArgb(53, 255, 255, 255), ((SolidColorBrush)drawerSurface.BorderBrush).Color);
            var expectedText = ((SolidColorBrush)Application.Current.FindResource("TextBrush")).Color;
            foreach (var inputName in new[] { "StoragePathTextBox", "UndoStepLimitInput", "ShortcutSearchBox", "HotkeyInputTextBox" })
            {
                var input = (TextBox)dialog.FindName(inputName);
                Equal(expectedText, ((SolidColorBrush)input.Foreground).Color);
            }
            var prompt = new PromptWindow("场景尚未保存", "画板有未保存修改。", "保存");
            try
            {
                Equal(expectedText, ((SolidColorBrush)((TextBlock)prompt.FindName("PromptTitle")).Foreground).Color);
                Equal(expectedText, ((SolidColorBrush)((TextBlock)prompt.FindName("PromptMessage")).Foreground).Color);
            }
            finally { prompt.Close(); }
            var picker = new CustomColorPickerWindow("#336699");
            try
            {
                picker.Measure(new Size(360, 638));
                picker.Arrange(new Rect(0, 0, 360, 638));
                picker.UpdateLayout();
                Equal(expectedText, ((SolidColorBrush)((TextBlock)picker.FindName("PickerTitleText")).Foreground).Color);
                Equal(expectedText, ((SolidColorBrush)((TextBox)picker.FindName("HexTextBox")).Foreground).Color);
                Equal(expectedText, ((SolidColorBrush)((System.Windows.Controls.ComboBox)picker.FindName("ColorFormatCombo")).Foreground).Color);
            }
            finally { picker.Close(); }
            var collector = new MainWindow();
            try
            {
                collector.Measure(new Size(500, 650));
                collector.Arrange(new Rect(0, 0, 500, 650));
                collector.UpdateLayout();
                var expectedIcon = ((SolidColorBrush)Application.Current.FindResource("TextBrush")).Color;
                foreach (var buttonName in new[] { "PinButton", "CollectionModeButton", "SettingsButton" })
                {
                    var button = (Button)collector.FindName(buttonName);
                    Equal(expectedIcon, ((SolidColorBrush)button.Foreground).Color);
                }
            }
            finally { collector.Close(); }
        }
        finally
        {
            dialog.Close();
        }

        try
        {
            WithDrawingBoard((board, _) =>
            {
                var toolbar = (Border)board.FindName("Toolbar");
                var textMenu = (Border)board.FindName("TextPalette");
                Equal(Color.FromArgb(242, 27, 29, 33), ((SolidColorBrush)toolbar.Background).Color);
                Equal(Color.FromRgb(43, 44, 48), ((SolidColorBrush)textMenu.Background).Color);
                var themedText = ((SolidColorBrush)Application.Current.FindResource("TextBrush")).Color;
                Equal(themedText, ((SolidColorBrush)((TextBox)board.FindName("DrawingThicknessText")).Foreground).Color);
                var sizeCombo = (System.Windows.Controls.ComboBox)board.FindName("TextSizeCombo");
                sizeCombo.ApplyTemplate();
                var sizeToggle = (ToggleButton)sizeCombo.Template.FindName("Toggle", sizeCombo);
                Equal(themedText, ((SolidColorBrush)sizeToggle.Foreground).Color);
                ThemeService.Apply(Application.Current, AppAppearanceMode.Light);
                board.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Equal(Color.FromRgb(255, 255, 255), ((SolidColorBrush)textMenu.Background).Color);
                Equal(Color.FromArgb(247, 255, 255, 255), ((SolidColorBrush)toolbar.Background).Color);
            });
        }
        finally
        {
            ThemeService.Apply(Application.Current, AppAppearanceMode.Light);
            LanguageService.Apply(LanguageService.SimplifiedChinese);
        }
    }

    private static void SystemCaptureShortcutAndSession()
    {
        var assembly = typeof(MainWindow).Assembly;
        var capture = assembly.GetType("ScreenshotCollector.Services.SystemScreenshotService")!;
        var inputs = (Array)capture.GetMethod("ScreenshotShortcut", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        Equal(6, inputs.Length);
        Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(inputs.GetType().GetElementType()!));
        var keys = new List<ushort>(); var flags = new List<uint>();
        foreach (var input in inputs)
        {
            Equal(1u, (uint)input!.GetType().GetField("Type")!.GetValue(input)!);
            var data = input.GetType().GetField("Data")!.GetValue(input)!;
            var key = data.GetType().GetField("Keyboard")!.GetValue(data)!;
            keys.Add((ushort)key.GetType().GetField("VirtualKey")!.GetValue(key)!);
            flags.Add((uint)key.GetType().GetField("Flags")!.GetValue(key)!);
        }
        True(keys.SequenceEqual(new ushort[] { 0x5B, 0x10, 0x53, 0x53, 0x10, 0x5B }), "系统截图不是 Win+Shift+S");
        True(flags.SequenceEqual(new uint[] { 0, 0, 0, 2, 2, 2 }), "截图快捷键未完整释放按键");
        var sessionType = assembly.GetType("ScreenshotCollector.Services.SystemSnipSession")!;
        object NewSession() => Activator.CreateInstance(sessionType)!;
        string? Observe(object session, double seconds, bool overlay = false, bool image = false, bool escape = false) =>
            sessionType.GetMethod("Observe")!.Invoke(session, new object[] { TimeSpan.FromSeconds(seconds), overlay, image, escape })?.ToString();
        var session = NewSession();
        True(Observe(session, .1, true) is null, "截图工具打开后过早结束");
        True(Observe(session, 1, false) is null, "未等待剪贴板尾帧");
        Equal("Captured", Observe(session, 1.5, false, true)!);
        session = NewSession(); Observe(session, .1, true); Observe(session, 1);
        Equal("Cancelled", Observe(session, 2.3)!);
        Equal("Cancelled", Observe(NewSession(), .1, escape: true)!);
        Equal("Unavailable", Observe(NewSession(), 12)!);
        Equal("TimedOut", Observe(NewSession(), 301, true)!);
        // Do not send real system keys or alter the user's clipboard in regression tests.
    }
}
