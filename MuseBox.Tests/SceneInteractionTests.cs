using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Application = System.Windows.Application;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private sealed class TestSceneDialogs : ISceneDialogs
    {
        public Queue<string?> OpenPaths { get; } = new();
        public int OpenPickerCount { get; private set; }
        public List<(string Title, string Message, string Primary, string Alternative)> Prompts { get; } = new();
        public string? OpenFile(Window owner)
        {
            OpenPickerCount++;
            return OpenPaths.Count > 0 ? OpenPaths.Dequeue() : null;
        }
        public Queue<string?> SavePaths { get; } = new();
        public Queue<string?> ExportPngPaths { get; } = new();
        public List<string> SuggestedNames { get; } = new();
        public Queue<int> Choices { get; } = new();
        public int FallbackChoice { get; init; }
        public bool RememberLinkedChoice { get; set; }
        public bool RememberImportChoice { get; set; }
        public int ImportPromptCount { get; private set; }
        public (int Choice, bool Remember) ChooseImageImport(Window owner, string drawer, int count)
        {
            ImportPromptCount++;
            return (Choose(owner, "导入图像", SceneDialogs.ImageImportMessage(drawer,count), "复制进画板", "链接原文件"), RememberImportChoice);
        }
        public (int Choice, bool Remember) ChooseLinkedSave(Window owner) =>
            (Choose(owner, "外部链接", "", "复制进画板", "保持链接"), RememberLinkedChoice);
        public List<string> Errors { get; } = new();
        public string? SaveFile(Window owner, string name, bool saveAs)
        {
            SuggestedNames.Add(name);
            return SavePaths.Dequeue();
        }
        public string? SaveExportPng(Window owner, string filename) => ExportPngPaths.Count > 0 ? ExportPngPaths.Dequeue() : null;
        public int Choose(Window owner, string title, string message, string primary, string alternative)
        {
            Prompts.Add((title, message, primary, alternative));
            return Choices.Count > 0 ? Choices.Dequeue() : FallbackChoice;
        }
        public void Inform(Window owner, string title, string message) => Errors.Add(title + ": " + message);
    }
    private static T PumpSceneTask<T>(Func<Task<T>> operation)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var task = operation();
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                Dispatcher.PushFrame(frame);
            }
            return task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void SceneMenuWorkflow()
    {
        var root = CreateTempDirectory();
        var (repository, imports) = SceneRepository(Path.Combine(root, "library"));
        var dialogs = new TestSceneDialogs();
        var app = (App)Application.Current;
        var oldRepository = app.Repository; var oldImports = app.ImportService;
        var oldCollector = app.CollectorWindow;
        var style = (Style)app.Resources[typeof(Window)];
        var hidden = new Style(typeof(Window), style);
        hidden.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));
        hidden.Setters.Add(new Setter(Window.ShowActivatedProperty, false));
        hidden.Setters.Add(new Setter(Window.ShowInTaskbarProperty, false));
        app.Resources[typeof(Window)] = hidden;
        typeof(App).GetProperty("Repository")!.SetValue(app, repository);
        typeof(App).GetProperty("ImportService")!.SetValue(app, imports);
        var main = new MainWindow(repository, imports, dialogs);
        typeof(App).GetProperty("CollectorWindow")!.SetValue(app, main);
        main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, typeof(MainWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        main.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), main, typeof(MainWindow).GetMethod("OnClosing", PrivateInstance | BindingFlags.DeclaredOnly)!);
        try
        {
            var snapshot = PopulateScene(repository, imports, root);
            AwaitMainTask(main, "ReloadDrawersAsync");
            var first = Path.Combine(root, "first.mubo");
            dialogs.SavePaths.Enqueue(first);
            True(PumpSceneTask(() => main.SaveSceneAsync("A", false)), "首次保存失败");
            True(dialogs.SuggestedNames.Single().EndsWith(".mubo", StringComparison.OrdinalIgnoreCase),
                "首次保存没有建议 .mubo 文件名");
            Equal(first, repository.GetSceneBindingAsync("A").GetAwaiter().GetResult()!.FilePath);
            True(!MainDrawers(main)[0].SceneDirty, "保存后未清除标记");
            repository.UpdateDrawerNameAsync("A", "修改后的场景").GetAwaiter().GetResult();
            AwaitMainTask(main, "RefreshSceneStatusAsync");
            True(MainDrawers(main)[0].SceneDirty, "修改后没有标记");
            var second = Path.Combine(root, "second.mubo");
            var originalHash = SceneFileService.HashFileAsync(first).GetAwaiter().GetResult();
            dialogs.SavePaths.Enqueue(second);
            True(PumpSceneTask(() => main.SaveSceneAsync("A", true)), "另存为失败");
            Equal(originalHash, SceneFileService.HashFileAsync(first).GetAwaiter().GetResult());
            Equal(second, repository.GetSceneBindingAsync("A").GetAwaiter().GetResult()!.FilePath);
            repository.UpdateDrawerNameAsync("A", "不应丢失").GetAwaiter().GetResult();
            dialogs.Choices.Enqueue(0);
            True(!PumpSceneTask(() => main.OpenSceneFileAsync(first, "A")), "取消打开仍替换画板");
            Equal("不应丢失", repository.GetDrawersAsync().GetAwaiter().GetResult()[0].DisplayName);
            dialogs.Choices.Enqueue(1);
            File.SetAttributes(second, FileAttributes.ReadOnly);
            try { True(!PumpSceneTask(() => main.OpenSceneFileAsync(first, "A")), "保存失败仍然替换画板"); }
            finally { File.SetAttributes(second, FileAttributes.Normal); }
            Equal("不应丢失", repository.GetDrawersAsync().GetAwaiter().GetResult()[0].DisplayName);
            True(dialogs.Errors.Count == 1, "保存失败没有提示");
            dialogs.Errors.Clear();
            repository.SaveViewportAsync(new BoardViewport { DrawerId = "B", WindowOpacity = .6 }).GetAwaiter().GetResult();
            dialogs.Choices.Enqueue(0);
            True(!PumpSceneTask(() => main.OpenSceneFileAsync(first, "B")), "只有背景透明度改动的画板被无提示替换");
            Equal(.6, repository.GetViewportAsync("B").GetAwaiter().GetResult().WindowOpacity);
            dialogs.Choices.Enqueue(1); // save before opening
            True(PumpSceneTask(() => main.OpenSceneFileAsync(first, "A")), "保存后打开失败");
            using (var saved = SceneFileService.ReadAsync(second).GetAwaiter().GetResult()) Equal("不应丢失", saved.Document.Name);
            Equal(snapshot.Document.Name, repository.GetDrawersAsync().GetAwaiter().GetResult()[0].DisplayName);
            PumpDrawerAnimation(250);
            var board = app.FindBoard("A")!;
            True(board is not null && board.IsVisible, "场景载入后没有打开画板");
            True(board!.Left >= SystemParameters.VirtualScreenLeft && board.Top >= SystemParameters.VirtualScreenTop, "换电脑后窗口仍在屏幕外");
            var gif = repository.GetItemsAsync("A").GetAwaiter().GetResult().Single(i => GifAnimationService.IsGif(i.AssetPath));
            BoardSelection(board).Clear(); BoardSelection(board).Add(gif.Id);
            var playback = (GifPlaybackState)CallDrawing(board, "SelectedGif")!;
            True(!playback.IsPlaying && playback.FrameIndex == 2 && playback.Speed == 2, "实际窗口没有恢复 GIF 状态");
            var binding = repository.GetSceneBindingAsync("A").GetAwaiter().GetResult()!;
            repository.UpdateDrawerNameAsync("A", "保留本机编辑").GetAwaiter().GetResult();
            var count = repository.GetDrawersAsync().GetAwaiter().GetResult().Count;
            True(PumpSceneTask(() => main.OpenSceneFileAsync(first)), "双击已关联场景失败");
            Equal(count, repository.GetDrawersAsync().GetAwaiter().GetResult().Count);
            Equal("保留本机编辑", repository.GetDrawersAsync().GetAwaiter().GetResult()[0].DisplayName);
            dialogs.Choices.Enqueue(0);
            True(!PumpSceneTask(main.ConfirmSceneExitAsync), "取消退出无效");
            dialogs.Choices.Enqueue(2);
            True(PumpSceneTask(main.ConfirmSceneExitAsync), "不保存退出无效");
            Equal(originalHash, SceneFileService.HashFileAsync(first).GetAwaiter().GetResult());
            dialogs.SavePaths.Enqueue(null);
            True(!PumpSceneTask(() => main.SaveSceneAsync("A", true)), "取消另存仍写入");
            Equal(binding.FilePath, repository.GetSceneBindingAsync("A").GetAwaiter().GetResult()!.FilePath);
            PumpSceneTask(async () =>
            {
                var activation = (Task)typeof(App).GetMethod("HandleSceneFilesAsync", PrivateInstance)!.Invoke(app, new object[] { new[] { second } })!;
                True(!activation.IsCompleted, "冷启动场景未等待资料库和主窗准备完成");
                ((TaskCompletionSource)typeof(App).GetField("_sceneStartup", PrivateInstance)!.GetValue(app)!).TrySetResult();
                await activation;
                return true;
            });
            True(repository.GetDrawersAsync().GetAwaiter().GetResult().Any(d => d.Id == "E" && d.ScenePath == second),
                "文件激活入口没有新建抽屉");
            True(dialogs.Errors.Count == 0, string.Join("\n", dialogs.Errors));
            var menu = (ScreenshotCollector.Controls.DrawerMenuPopup)MainCall(main, "CreateDrawerMenu", MainDrawers(main)[0])!;
            SaveDrawingTestVisual((FrameworkElement)menu.Child, "scene-drawer-menu.png");
            VerifyDrawerSceneReplacement(main, repository, imports, dialogs, root, first);
        }
        finally
        {
            foreach (var drawer in repository.GetDrawersAsync().GetAwaiter().GetResult())
                if (app.FindBoard(drawer.Id) is { } board)
                {
                    using var lease = PumpSceneTask(board.PrepareSceneAsync);
                    board.CloseForSceneReplacement();
                }
            main.Close();
            app.Resources[typeof(Window)] = style;
            typeof(App).GetProperty("Repository")!.SetValue(app, oldRepository);
            typeof(App).GetProperty("ImportService")!.SetValue(app, oldImports);
            typeof(App).GetProperty("CollectorWindow")!.SetValue(app, oldCollector);
            Directory.Delete(root, true);
        }
    }

    private static void VerifyDrawerSceneReplacement(MainWindow main, BoardRepository repository,
        BoardImportService imports, TestSceneDialogs dialogs, string root, string incoming)
    {
        bool Pick(string id, string? path)
        {
            dialogs.OpenPaths.Enqueue(path);
            return PumpSceneTask(() => (Task<bool>)MainCall(main, "OpenDrawerSceneFileAsync", id)!);
        }
        long Revision(string id) => repository.CaptureSceneAsync(id).GetAwaiter().GetResult().Revision;
        var promptCount = dialogs.Prompts.Count;
        True(PumpSceneTask(() => (Task<bool>)MainCall(main, "ConfirmSceneReplacementAsync", "C")!), "空抽屉不能直接打开");
        Equal(promptCount, dialogs.Prompts.Count);

        // The new menu action opens the picker; cancelling it has no side effects.
        var menu = (ScreenshotCollector.Controls.DrawerMenuPopup)MainCall(main, "CreateDrawerMenu",
            MainDrawers(main).Single(d => d.Id == "D"))!;
        var open = menu.Actions.Children.OfType<Button>().Single(b =>
            System.Windows.Automation.AutomationProperties.GetName(b) == "打开 .mubo 文件");
        var pickerCount = dialogs.OpenPickerCount;
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(pickerCount + 1, dialogs.OpenPickerCount);
        Equal(promptCount, dialogs.Prompts.Count);

        var image = LinkedTestImage(root, "replace-original.png");
        imports.ImportFilesAsync("C", new[] { image }).GetAwaiter().GetResult();
        var before = Revision("C");
        dialogs.Choices.Enqueue(0);
        True(!Pick("C", incoming), "图片抽屉取消仍被替换");
        Equal(before, Revision("C"));
        Equal("保存", dialogs.Prompts.Last().Primary);
        Equal("覆盖", dialogs.Prompts.Last().Alternative);
        True(dialogs.Prompts.Last().Message.Contains("未保存图像"), "图片抽屉没有说明未保存内容");
        dialogs.Choices.Enqueue(2);
        True(Pick("C", incoming), "覆盖无法打开指定抽屉");
        Equal(incoming, repository.GetSceneBindingAsync("C").GetAwaiter().GetResult()!.FilePath);
        PumpDrawerAnimation(200);

        // Material-only drawers must be protected exactly like board images.
        imports.ImportFilesAsync("D", new[] { image }, destination: ImageImportDestination.Materials).GetAwaiter().GetResult();
        var material = repository.GetMaterialsAsync("D").GetAwaiter().GetResult().Single();
        before = Revision("D");
        dialogs.Choices.Enqueue(0);
        True(!Pick("D", incoming), "素材抽屉取消仍被替换");
        Equal(before, Revision("D"));
        True(dialogs.Prompts.Last().Message.Contains("素材区"), "提醒遗漏素材区");
        dialogs.Choices.Enqueue(1);
        dialogs.SavePaths.Enqueue(null);
        True(!Pick("D", incoming), "取消保存位置仍然替换");
        Equal(before, Revision("D"));
        dialogs.Choices.Enqueue(1);
        dialogs.SavePaths.Enqueue(Path.Combine(root, "missing-directory", "save.mubo"));
        True(!Pick("D", incoming), "保存失败仍然替换素材");
        Equal(before, Revision("D"));
        True(dialogs.Errors.Single().StartsWith("场景保存失败"), "保存失败没有准确提示");
        dialogs.Errors.Clear();
        var invalid = Path.Combine(root, "invalid.mubo");
        File.WriteAllText(invalid, "not a scene");
        promptCount = dialogs.Prompts.Count;
        True(!Pick("D", invalid), "无效场景被接受");
        Equal(promptCount, dialogs.Prompts.Count);
        Equal(before, Revision("D"));
        dialogs.Errors.Clear();

        var materialBackup = Path.Combine(root, "materials-backup.mubo");
        dialogs.SavePaths.Enqueue(materialBackup);
        dialogs.Choices.Enqueue(1);
        var drawerCount = repository.GetDrawersAsync().GetAwaiter().GetResult().Count;
        True(Pick("D", incoming), "保存素材后打开失败");
        Equal(drawerCount, repository.GetDrawersAsync().GetAwaiter().GetResult().Count);
        using (var saved = SceneFileService.ReadAsync(materialBackup).GetAwaiter().GetResult())
            Equal(material.Id, saved.Document.Materials.Single().Id);
        True(File.Exists(image), "替换删除了原图");
        Equal(0, repository.GetMaterialsAsync("D").GetAwaiter().GetResult().Count);
        PumpDrawerAnimation(200);

        // Save, then reopen that same file: never import the pre-save snapshot.
        var current = Path.Combine(root, "same-file.mubo");
        dialogs.SavePaths.Enqueue(current);
        True(PumpSceneTask(() => main.SaveSceneAsync("D", true)), "另存准备失败");
        promptCount = dialogs.Prompts.Count;
        True(PumpSceneTask(() => (Task<bool>)MainCall(main, "ConfirmSceneReplacementAsync", "D")!), "已保存抽屉仍需提示");
        Equal(promptCount, dialogs.Prompts.Count);
        repository.UpdateDrawerNameAsync("D", "同文件保存后的新名称").GetAwaiter().GetResult();
        imports.ImportFilesAsync("D", new[] { image }, destination: ImageImportDestination.Materials).GetAwaiter().GetResult();
        before = Revision("D");
        dialogs.Choices.Enqueue(0);
        True(!Pick("D", current), "已绑定场景取消仍被替换");
        Equal(before, Revision("D"));
        True(dialogs.Prompts.Last().Message.Contains(".mubo"), "绑定场景没有提示保存修改");
        dialogs.Choices.Enqueue(1);
        True(Pick("D", current), "同文件保存后重开失败");
        using (var saved = SceneFileService.ReadAsync(current).GetAwaiter().GetResult())
        {
            Equal("同文件保存后的新名称", saved.Document.Name);
            Equal(1, saved.Document.Materials.Count);
        }
        Equal("同文件保存后的新名称", repository.GetDrawersAsync().GetAwaiter().GetResult().Single(d => d.Id == "D").DisplayName);
        Equal(1, repository.GetMaterialsAsync("D").GetAwaiter().GetResult().Count);
        True(dialogs.Errors.Count == 0, string.Join("\n", dialogs.Errors));
    }

    private static void SceneAutoSaveBoundFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var (repository, imports) = SceneRepository(Path.Combine(root, "library"));
            PopulateScene(repository, imports, root);
            var path = Path.Combine(root, "autosave.mubo");
            var initial = repository.CaptureSceneAsync("A").GetAwaiter().GetResult();
            var initialHash = SceneFileService.WriteAsync(path, initial).GetAwaiter().GetResult();
            repository.MarkSceneSavedAsync(new SceneBinding("A", path, initial.Revision, initialHash))
                .GetAwaiter().GetResult();
            repository.UpdateDrawerNameAsync("A", "自动保存后的名称").GetAwaiter().GetResult();

            var main = new MainWindow(repository, imports, new TestSceneDialogs());
            typeof(MainWindow).GetField("_settings", PrivateInstance)!.SetValue(main,
                new AppSettings { AutoSaveEnabled = true, AutoSaveIntervalMinutes = 7 });
            try
            {
                PumpSceneTask(async () =>
                {
                    await (Task)typeof(MainWindow).GetMethod("AutoSaveDirtyScenesAsync", PrivateInstance)!
                        .Invoke(main, null)!;
                    return true;
                });
                using (var saved = SceneFileService.ReadAsync(path).GetAwaiter().GetResult())
                    Equal("自动保存后的名称", saved.Document.Name);
                True(!repository.GetDrawersAsync().GetAwaiter().GetResult().Single(x => x.Id == "A").HasUnsavedScene,
                    "自动保存后场景仍被标记为未保存");

                repository.UpdateDrawerNameAsync("A", "不应覆盖外部文件").GetAwaiter().GetResult();
                File.AppendAllText(path, "external-change");
                var externallyChanged = File.ReadAllBytes(path);
                PumpSceneTask(async () =>
                {
                    await (Task)typeof(MainWindow).GetMethod("AutoSaveDirtyScenesAsync", PrivateInstance)!
                        .Invoke(main, null)!;
                    return true;
                });
                True(externallyChanged.SequenceEqual(File.ReadAllBytes(path)),
                    "外部修改冲突时自动保存仍覆盖了场景文件");
                True(repository.GetDrawersAsync().GetAwaiter().GetResult().Single(x => x.Id == "A").HasUnsavedScene,
                    "外部冲突后错误清除了未保存状态");
            }
            finally
            {
                main.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), main,
                    typeof(MainWindow).GetMethod("OnClosing", PrivateInstance | BindingFlags.DeclaredOnly)!);
                main.Close();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void SceneImportTransactionRollback()
    {
        var root = CreateTempDirectory();
        try
        {
            var (source, imports) = SceneRepository(Path.Combine(root, "source"));
            var snapshot = PopulateScene(source, imports, root);
            var file = Path.Combine(root, "scene.iscene");
            SceneFileService.WriteAsync(file, snapshot).GetAwaiter().GetResult();
            using var scene = SceneFileService.ReadAsync(file).GetAwaiter().GetResult();
            var (target, targetImports) = SceneRepository(Path.Combine(root, "target"));
            PopulateScene(target, targetImports, Path.Combine(root, "target"));
            target.UpdateDrawerNameAsync("A", "原画板必须保留").GetAwaiter().GetResult();
            var before = JsonSerializer.Serialize(target.CaptureSceneAsync("A").GetAwaiter().GetResult().Document);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "target", "boards.db")};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER scene_test_abort BEFORE INSERT ON text_items BEGIN SELECT RAISE(ABORT,'test insertion failure'); END;";
                command.ExecuteNonQuery();
            }
            var rejected = false;
            try { target.ImportSceneAsync("A", scene, file).GetAwaiter().GetResult(); }
            catch (SqliteException) { rejected = true; }
            True(rejected, "测试没有触发事务中途失败");
            Equal(before, JsonSerializer.Serialize(target.CaptureSceneAsync("A").GetAwaiter().GetResult().Document));
            True(target.GetSceneBindingAsync("A").GetAwaiter().GetResult() is null, "失败导入改变了文件关联");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void SceneLiveGifAndTextSnapshot() => WithDrawingBoard((window, repository) =>
    {
        var gif = AddAnimatedImage(window, repository);
        var playback = (GifPlaybackState)CallDrawing(window, "SelectedGif")!;
        playback.SetSpeed(3); playback.Seek(2);
        using (PumpSceneTask(window.PrepareSceneAsync))
        {
            var snapshot = repository.CaptureSceneAsync("A").GetAwaiter().GetResult();
            Equal(2, snapshot.Document.Gifs.Single().FrameIndex);
            Equal(3d, snapshot.Document.Gifs.Single().Speed);
            True(!snapshot.Document.Gifs.Single().IsPlaying, "保存快照未捕获实时暂停状态");
            True(!window.IsEnabled, "场景保存期间画板仍接受编辑");
        }
        True(window.IsEnabled, "保存结束未恢复画板交互");
        var doc = RichTextDocumentService.CreateDefault();
        var text = new BoardTextItem { DrawerId = "A", DocumentData = RichTextDocumentService.Save(doc) };
        repository.AddTextItemsAsync(new[] { text }).GetAwaiter().GetResult();
        AwaitImageUiTask(window, "ReloadAsync");
        var live = ((List<BoardTextItem>)typeof(BoardWindow).GetField("_textItems", PrivateInstance)!.GetValue(window)!).Single();
        CallDrawing(window, "BeginTextEditing", live);
        var editor = (System.Windows.Controls.RichTextBox)typeof(BoardWindow).GetField("_activeTextEditor", PrivateInstance)!.GetValue(window)!;
        editor.Document.Blocks.Clear(); editor.Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("尚未离开编辑框的文字")));
        using (PumpSceneTask(window.PrepareSceneAsync))
        {
            var saved = repository.CaptureSceneAsync("A").GetAwaiter().GetResult();
            Equal("尚未离开编辑框的文字", RichTextDocumentService.PlainText(RichTextDocumentService.Load(saved.Document.Texts.Single().DocumentData)));
        }
    });

    private static void ScenePromptChoices()
    {
        var owner = new Window { Width = 500, Height = 300, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        owner.Show();
        try
        {
            foreach (var choice in new[] { 0, 1, 2 })
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += (_, _) =>
                {
                    var prompt = owner.OwnedWindows.OfType<PromptWindow>().FirstOrDefault();
                    if (prompt is null) return;
                    timer.Stop();
                    var chrome = (FrameworkElement)prompt.FindName("PromptChrome");
                    SaveDrawingTestVisual(chrome, "scene-save-prompt.png", false);
                    ((Button)prompt.FindName(choice == 0 ? "PromptCancel" : choice == 1 ? "PromptConfirm" : "PromptAlternative"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                // Keep the native test dialog invisible without changing its content render.
                var style = Application.Current.Resources[typeof(Window)];
                var hidden = new Style(typeof(Window), (Style)style);
                hidden.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));
                hidden.Setters.Add(new Setter(Window.ShowActivatedProperty, false));
                Application.Current.Resources[typeof(Window)] = hidden;
                timer.Start();
                try { Equal(choice, PromptWindow.Choose(owner, "打开前保存当前画板？", "打开场景将替换这个抽屉的内容。不保存会放弃当前尚未写入场景文件的内容。", "保存", "不保存")); }
                finally { timer.Stop(); Application.Current.Resources[typeof(Window)] = style; }
            }
        }
        finally { owner.Close(); }
    }

    private static void SceneMissingFonts()
    {
        var doc = RichTextDocumentService.CreateDefault();
        const string missingName = "InspirationCollector Test Missing Font 782194";
        doc.FontFamily = new System.Windows.Media.FontFamily(missingName);
        doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("替代字体仍可编辑")));
        var data = RichTextDocumentService.Save(doc);
        var scene = new SceneDocument { Texts = new List<BoardTextItem> { new() { DocumentData = data } } };
        True(SceneFontService.MissingFonts(scene).Contains(missingName), "缺少字体没有提示");
        Equal(data, scene.Texts[0].DocumentData);
        True(RichTextDocumentService.PlainText(RichTextDocumentService.Load(data)).Contains("仍可编辑"), "字体缺失破坏了文字");
    }

    private static void SceneUnavailableLinks() => WithDrawingBoard((window, repository) =>
    {
        var style = Application.Current.Resources[typeof(Window)];
        var hidden = new Style(typeof(Window), (Style)style);
        hidden.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));
        hidden.Setters.Add(new Setter(Window.ShowActivatedProperty, false));
        hidden.Setters.Add(new Setter(Window.ShowInTaskbarProperty, false));
        Application.Current.Resources[typeof(Window)] = hidden;
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(BoardWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        window.Show();
        var originalContext = SynchronizationContext.Current;
        try
        {
            AddEditableImage(window);
            var note = AddLinkedTestNote(window, repository);
            var picture = LiveImages(window).Single();
            foreach (var isText in new[] { false, true })
            {
                var unavailable = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.txt");
                if (isText) AwaitImageUiTask(window, "SaveTextLinksAsync", note, "https://example.com/note", unavailable);
                else AwaitImageUiTask(window, "SaveImageLinksAsync", picture, "https://example.com/image", unavailable);
                BoardSelection(window).Clear(); BoardSelection(window).Add(isText ? note.Id : picture.Id);
                var step = 0;
                var frame = new DispatcherFrame();
                var started = DateTime.UtcNow;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                timer.Tick += (_, _) =>
                {
                    if (step == 0 && window.OwnedWindows.OfType<PromptWindow>().FirstOrDefault() is { } prompt)
                    {
                        step = 1;
                        True(prompt.Title == "无法打开", "缺失链接提示错误");
                        // Changing selection must not change which item's links get edited.
                        BoardSelection(window).Clear(); BoardSelection(window).Add(isText ? picture.Id : note.Id);
                        ((Button)prompt.FindName("PromptConfirm")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (step == 1 && window.OwnedWindows.OfType<ImageLinksWindow>().FirstOrDefault() is { } links)
                    {
                        step = 2;
                        Equal(unavailable, ((TextBox)links.FindName("FileLinkInput")).Text);
                        ((Button)links.FindName("ClearFileLinkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        typeof(ImageLinksWindow).GetMethod("OnSaveClick", PrivateInstance)!.Invoke(links, new object[] { links, new RoutedEventArgs() });
                    }
                    else if (step == 2 || DateTime.UtcNow - started > TimeSpan.FromSeconds(8))
                    {
                        timer.Stop();
                        if (step != 2)
                            foreach (Window modal in window.OwnedWindows) modal.Close();
                        frame.Continue = false;
                    }
                };
                timer.Start();
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                    CallDrawing(window, isText ? "OnOpenTextFileLink" : "OnOpenImageFileLink", window, new RoutedEventArgs());
                    Dispatcher.PushFrame(frame);
                }
                finally { timer.Stop(); SynchronizationContext.SetSynchronizationContext(originalContext); }
                Equal(2, step);
                var remaining = isText ? repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single().FileLink :
                    repository.GetItemsAsync("A").GetAwaiter().GetResult().Single().FileLink;
                Equal("", remaining);
                if (isText) AwaitImageUiTask(window, "SaveTextLinksAsync", note, "https://example.com/note", Path.GetTempPath());
                else AwaitImageUiTask(window, "SaveImageLinksAsync", picture, "https://example.com/image", Path.GetTempPath());
                True((isText ? note.FileLink : picture.FileLink).Length > 0, "无法重新设定文件地址");
                True((isText ? note.WebLink : picture.WebLink).StartsWith("https://example.com/"), "清除文件链接影响网页链接");
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
            Application.Current.Resources[typeof(Window)] = style;
        }
    });
}
