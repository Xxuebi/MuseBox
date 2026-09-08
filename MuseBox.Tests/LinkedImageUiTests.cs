using System.Windows;
using System.Windows.Controls;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using TabControl = System.Windows.Controls.TabControl;

namespace ScreenshotCollector.Tests;
internal static partial class Program
{
    private sealed class LinkedMemorySettings : ISettingsService
    {
        public AppSettings Value = new();
        public int Saves;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value.Copy());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        { Value = settings.Copy(); Saves++; return Task.CompletedTask; }
    }
    private static void LinkedSavePreferences() => WithMainDrawerWindow((main, repo) =>
    {
        var root = CreateTempDirectory();
        try
        {
            var imports = (BoardImportService)typeof(MainWindow).GetField("_importService", PrivateInstance)!.GetValue(main)!;
            var dialogs = (TestSceneDialogs)typeof(MainWindow).GetField("_sceneDialogs", PrivateInstance)!.GetValue(main)!;
            var memory = new LinkedMemorySettings();
            typeof(MainWindow).GetField("_settingsService", PrivateInstance)!.SetValue(main, memory);
            var settings = (AppSettings)typeof(MainWindow).GetField("_settings", PrivateInstance)!.GetValue(main)!;
            var original = LinkedTestImage(root);
            imports.ImportFilesAsync("A", new[] { original }, mode: ImageImportMode.Link).GetAwaiter().GetResult();
            var path = System.IO.Path.Combine(root, "preferences.mubo");
            File.Delete(original);
            dialogs.RememberLinkedChoice = true;
            dialogs.SavePaths.Enqueue(path); dialogs.Choices.Enqueue(1);
            True(!PumpSceneTask(() => main.SaveSceneAsync("A", false)), "缺失链接复制保存未失败");
            True(settings.AskBeforeSavingLinks && memory.Saves == 0, "失败保存记住了选择");
            dialogs.RememberLinkedChoice = false;
            dialogs.SavePaths.Enqueue(path); dialogs.Choices.Enqueue(2);
            True(PumpSceneTask(() => main.SaveSceneAsync("A", false)), "保持缺失链接失败");
            settings.AutoSaveEnabled = true;
            settings.LinkedImageSaveMode = ExternalImageSaveMode.Copy;
            repo.UpdateDrawerNameAsync("A", "自动保持链接").GetAwaiter().GetResult();
            dialogs.Choices.Enqueue(0);
            AwaitMainTask(main, "AutoSaveDirtyScenesAsync");
            Equal(1, dialogs.Choices.Count);
            True(!repo.GetDrawersAsync().GetAwaiter().GetResult().Single(d => d.Id == "A").HasUnsavedScene, "未记住选择时自动保存未保持链接");
            dialogs.Choices.Clear();
            LinkedTestImage(root);
            dialogs.RememberLinkedChoice = true; dialogs.Choices.Enqueue(2);
            True(PumpSceneTask(() => main.SaveSceneAsync("A", false)), "记忆保持链接保存失败");
            True(!settings.AskBeforeSavingLinks && memory.Value.LinkedImageSaveMode == ExternalImageSaveMode.KeepLinks, "未记住成功选择");
            settings.LinkedImageSaveMode = ExternalImageSaveMode.Copy;
            repo.UpdateDrawerNameAsync("A", "自动复制").GetAwaiter().GetResult();
            AwaitMainTask(main, "AutoSaveDirtyScenesAsync");
            True(repo.GetItemsAsync("A").GetAwaiter().GetResult().Single().AssetPath != original, "自动保存未遵循记住的复制选项");
            var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
            True(old.AskBeforeSavingLinks && old.LinkedImageSaveMode == ExternalImageSaveMode.KeepLinks, "旧设置默认值错误");
        }
        finally { Directory.Delete(root, true); }
    });
    private static void LinkedGifVisualRefresh() => WithDrawingBoard((board, repo) =>
    {
        var root = CreateTempDirectory();
        try
        {
            var imports = (BoardImportService)typeof(BoardWindow).GetField("_importService", PrivateInstance)!.GetValue(board)!;
            PumpSceneTask(async () =>
            {
                var path = WriteTestGif(root);
                await imports.ImportFilesAsync("A", new[] { path }, mode: ImageImportMode.Link);
                await board.ReloadAsync();
                async Task<System.Windows.Media.ImageSource?> Source()
                {
                    System.Windows.Media.ImageSource? source = null;
                    for (var n = 0; n < 30 && source is null; n++)
                    {
                        await Task.Delay(40);
                        source = VisualChildren<System.Windows.Controls.Image>((DependencyObject)board.FindName("WorldCanvas"))
                            .FirstOrDefault(i => Equals(i.Tag, path))?.Source;
                    }
                    return source;
                }
                var gif = await Source(); True(gif is not null, "链接GIF未加载");
                File.Delete(path);
                File.Copy(LinkedTestImage(root), path);
                ImageFileFormatService.Invalidate(path);
                board.RefreshLinkedImagePaths(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path });
                var png = await Source(); True(png is not null && !ReferenceEquals(gif, png), "更新后仍使用GIF旧帧");
                File.Delete(path);
                board.RefreshLinkedImagePaths(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path });
                True(VisualChildren<TextBlock>((DependencyObject)board.FindName("WorldCanvas")).Any(t => t.Text.Contains(path)), "缺失占位未显示原路径");
                WriteTestGif(root); ImageFileFormatService.Invalidate(path);
                board.RefreshLinkedImagePaths(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path });
                True(await Source() is not null, "恢复后GIF未重新显示");
                Equal(path, (await repo.GetItemsAsync("A")).Single().AssetPath);
                return true;
            });
        }
        finally { Directory.Delete(root, true); }
    });

    private static void LinkedDialogsLayout()
    {
        var original = ThemeService.CurrentMode;
        try
        {
            foreach (var theme in new[] { AppAppearanceMode.Light, AppAppearanceMode.Dark })
            {
                ThemeService.Apply(Application.Current, theme);
                foreach (var width in new[] { 540d, 360d })
                {
                    var prompt = new PromptWindow("保存外部链接",
                        "此画板包含外部链接。\n\n复制进画板：将当前图片收进资料库并打包到场景，不再依赖原文件。\n保持链接：仅保存原文件绝对路径。",
                        "复制进画板") { Width = width };
                    try
                    {
                        ((Button)prompt.FindName("PromptAlternative")).Content = "保持链接";
                        ((Button)prompt.FindName("PromptAlternative")).Visibility = Visibility.Visible;
                        ((CheckBox)prompt.FindName("RememberChoice")).Visibility = Visibility.Visible;
                        var content = (FrameworkElement)prompt.Content;
                        content.Measure(new System.Windows.Size(width, 600));
                        content.Arrange(new Rect(0, 0, width, content.DesiredSize.Height));
                        content.UpdateLayout();
                        True(content.ActualHeight < 600, "提示框溢出");
                        SaveSettingsSnapshot(prompt, $"linked-save-{theme}-{width}.png");
                    }
                    finally { prompt.Close(); }
                }
                var settings = new SettingsWindow(new AppSettings { AskBeforeSavingLinks = true }) { Opacity = 0, ShowActivated = false, Width = 700, Height = 520 };
                try
                {
                    settings.Show();
                    ((TabControl)settings.FindName("SettingsCategories")).SelectedIndex = 3;
                    settings.UpdateLayout();
                    True(settings.FindName("AskLinkedSaveCheck") is null, "小窗仍显示外部链接保存方式");
                    SaveSettingsSnapshot(settings, $"linked-settings-{theme}.png");
                }
                finally { settings.Close(); }
            }
        }
        finally { ThemeService.Apply(Application.Current, original); }
    }
}

