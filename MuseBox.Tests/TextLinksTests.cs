using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;
using RichTextBox = System.Windows.Controls.RichTextBox;
using Size = System.Windows.Size;

namespace ScreenshotCollector.Tests;

internal static partial class Program
{
    private static List<BoardTextItem> LiveTexts(BoardWindow window) =>
        (List<BoardTextItem>)typeof(BoardWindow).GetField("_textItems", PrivateInstance)!.GetValue(window)!;

    private static BoardTextItem AddLinkedTestNote(BoardWindow window, BoardRepository repository)
    {
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(BoardWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        var document = RichTextDocumentService.CreateDefault();
        document.Blocks.Clear();
        document.Blocks.Add(new Paragraph(new Run("灵感注释 · 参考链接") { FontSize = 28, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White }));
        var note = new BoardTextItem
        {
            DrawerId = "A", X = 250, Y = 240, Width = 320, Height = 46,
            BackgroundColor = "#FF36B9BF", DocumentData = RichTextDocumentService.Save(document)
        };
        repository.AddTextItemsAsync(new[] { note }).GetAwaiter().GetResult();
        AwaitImageUiTask(window, "ReloadAsync");
        BoardSelection(window).Clear(); BoardSelection(window).Add(note.Id);
        ArrangeBoardSurface(window);
        CallDrawing(window, "UpdateSelectionVisuals");
        return LiveTexts(window).Single();
    }

    private static void TextLinksPersistAndUndo() => WithDrawingBoard((window, repository) =>
    {
        var item = AddLinkedTestNote(window, repository);
        var content = item.DocumentData;
        var links = (Border)window.FindName("TextLinkButtons");
        Equal(Visibility.Collapsed, links.Visibility);
        var history = UndoCount(window);
        AwaitImageUiTask(window, "SaveTextLinksAsync", item, "example.com/note", "");
        Equal("https://example.com/note", item.WebLink);
        Equal(history + 1, UndoCount(window));
        Equal(Visibility.Visible, ((Button)window.FindName("TextWebLinkButton")).Visibility);
        Equal(Visibility.Collapsed, ((Button)window.FindName("TextFileLinkButton")).Visibility);
        AwaitImageUiTask(window, "SaveTextLinksAsync", item, item.WebLink, "");
        Equal(history + 1, UndoCount(window));
        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        AwaitImageUiTask(window, "SaveTextLinksAsync", item, item.WebLink, folder);
        var saved = repository.GetTextItemsAsync("A").GetAwaiter().GetResult().Single();
        Equal(item.WebLink, saved.WebLink); Equal(folder, saved.FileLink); Equal(content, saved.DocumentData);
        Equal(saved.WebLink, saved.Clone().WebLink); Equal(folder, saved.Clone().FileLink);
        AwaitImageUiTask(window, "UndoAsync");
        Equal("", LiveTexts(window).Single().FileLink);
        AwaitImageUiTask(window, "RedoAsync");
        item = LiveTexts(window).Single();
        Equal(folder, item.FileLink);
        AwaitImageUiTask(window, "SaveTextLinksAsync", item, "", folder);
        Equal(Visibility.Collapsed, ((Button)window.FindName("TextWebLinkButton")).Visibility);
        Equal(Visibility.Visible, ((Button)window.FindName("TextFileLinkButton")).Visibility);
        AwaitImageUiTask(window, "SaveTextLinksAsync", item, "", "");
        Equal(Visibility.Collapsed, links.Visibility);
        var beforeInvalid = UndoCount(window);
        var rejected = false;
        try { AwaitImageUiTask(window, "SaveTextLinksAsync", item, "javascript:alert(1)", ""); }
        catch (ArgumentException) { rejected = true; }
        True(rejected, "注释链接接受了可执行网页协议");
        Equal(beforeInvalid, UndoCount(window)); Equal("", item.WebLink);
        AwaitImageUiTask(window, "UndoAsync");
        Equal(folder, LiveTexts(window).Single().FileLink);
        Equal(content, LiveTexts(window).Single().DocumentData);
    });

    private static void TextLinksPositioning() => WithDrawingBoard((window, repository) =>
    {
        var item = AddLinkedTestNote(window, repository);
        item.WebLink = "https://example.com"; item.FileLink = @"C:\参考资料";
        CallDrawing(window, "UpdateResizeHandles");
        var surface = (Grid)window.FindName("BoardSurface");
        var links = (Border)window.FindName("TextLinkButtons");
        Equal(Visibility.Visible, links.Visibility);
        Equal(item.X + item.Width - links.DesiredSize.Width, Canvas.GetLeft(links), .001);
        Equal(item.Y + item.Height + 18, Canvas.GetTop(links), .001);
        True(!((Button)window.FindName("TextWebLinkButton")).Focusable, "链接按钮会抢走文字编辑焦点");
        True((bool)typeof(BoardWindow).GetMethod("IsToolPaletteSource", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { window.FindName("TextWebLinkButton") })!, "链接点击会进入画板拖拽/取消选择逻辑");
        SaveDrawingTestVisual(surface, "text-links-outside.png", false);
        item.X = 210; item.Y = 120; item.Rotation = 90;
        SetDrawingField(window, "_viewZoom", 1.25d);
        SetDrawingField(window, "_viewPanX", 35d); SetDrawingField(window, "_viewPanY", 10d);
        CallDrawing(window, "UpdateResizeHandles");
        Equal((item.X + item.Width / 2 + item.Height / 2) * 1.25 + 35 - links.DesiredSize.Width,
            Canvas.GetLeft(links), .001);
        Equal((item.Y + item.Height / 2 + item.Width / 2) * 1.25 + 10 + 18, Canvas.GetTop(links), .001);
        item.Rotation = 0;
        SetDrawingField(window, "_viewZoom", 1d); SetDrawingField(window, "_viewPanX", 0d); SetDrawingField(window, "_viewPanY", 0d);
        item.Y = surface.ActualHeight - item.Height - 8;
        CallDrawing(window, "UpdateResizeHandles");
        True(Canvas.GetLeft(links) > item.X + item.Width, "靠近底边时未移到文字外侧");
        True(Canvas.GetTop(links) + links.DesiredSize.Height <= surface.ActualHeight - 8 + .01, "链接按钮超出底边");
        ArrangeBoardSurface(window, 600, 500);
        True(links.Visibility == Visibility.Collapsed || Canvas.GetTop(links) + links.DesiredSize.Height <= 492.01, "缩窗后链接未重新定位");
        item.Y = 2000;
        CallDrawing(window, "UpdateResizeHandles");
        Equal(Visibility.Collapsed, links.Visibility);
        item.Y = 200;
        BoardSelection(window).Add("other-selection");
        CallDrawing(window, "UpdateSelectionVisuals");
        Equal(Visibility.Collapsed, links.Visibility);
        BoardSelection(window).Clear();
        CallDrawing(window, "UpdateSelectionVisuals");
        Equal(Visibility.Collapsed, links.Visibility);
        BoardSelection(window).Add(item.Id);
        SetDrawingField(window, "_toolMode", BoardToolMode.Pen);
        CallDrawing(window, "UpdateResizeHandles");
        Equal(Visibility.Collapsed, links.Visibility);
        SetDrawingField(window, "_toolMode", BoardToolMode.Select);
    });

    private static void TextLinksEditingHistory() => WithDrawingBoard((window, repository) =>
    {
        var item = AddLinkedTestNote(window, repository);
        var original = RichTextDocumentService.PlainText(RichTextDocumentService.Load(item.DocumentData));
        CallDrawing(window, "BeginTextEditing", item);
        var editor = (RichTextBox)typeof(BoardWindow).GetField("_activeTextEditor", PrivateInstance)!.GetValue(window)!;
        editor.Document.Blocks.Add(new Paragraph(new Run("新增内容")));
        AwaitImageUiTask(window, "SaveTextLinksAsync", item, "https://example.com/new", "");
        True(typeof(BoardWindow).GetField("_activeTextEditor", PrivateInstance)!.GetValue(window) is null, "设置链接后编辑事务仍未完成");
        True(RichTextDocumentService.PlainText(RichTextDocumentService.Load(item.DocumentData)).Contains("新增内容"), "保存链接丢失了尚未提交的文字");
        Equal(2, UndoCount(window));
        AwaitImageUiTask(window, "UndoAsync");
        Equal("", LiveTexts(window).Single().WebLink);
        True(RichTextDocumentService.PlainText(RichTextDocumentService.Load(LiveTexts(window).Single().DocumentData)).Contains("新增内容"), "撤回链接同时撤回了文字");
        AwaitImageUiTask(window, "UndoAsync");
        Equal(original, RichTextDocumentService.PlainText(RichTextDocumentService.Load(LiveTexts(window).Single().DocumentData)));
        AwaitImageUiTask(window, "RedoAsync"); AwaitImageUiTask(window, "RedoAsync");
        Equal("https://example.com/new", LiveTexts(window).Single().WebLink);
    });

    private static void TextLinksDialogAndMenu() => WithDrawingBoard((window, repository) =>
    {
        AddLinkedTestNote(window, repository);
        var button = (Button)window.FindName("TextLinksButton");
        var popup = (Popup)window.FindName("TextMorePopup");
        True((bool)typeof(BoardWindow).GetMethod("IsWithinPopupElement", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { button, popup.Child })!, "链接设置不在文字右侧子菜单内");
        SaveDrawingTestVisual((FrameworkElement)popup.Child, "text-more-with-links.png");
        var dialog = new ImageLinksWindow("https://example.com", @"C:\参考资料", "注释链接");
        try
        {
            Equal("注释链接", dialog.Title);
            Equal("注释链接", ((TextBlock)dialog.FindName("LinksTitleText")).Text);
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(530, 346)); content.Arrange(new Rect(0, 0, 530, 346));
            SaveDrawingTestVisual(content, "text-link-dialog.png", false);
            ((Button)dialog.FindName("ClearWebLinkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)dialog.FindName("ClearFileLinkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal("", ((TextBox)dialog.FindName("WebLinkInput")).Text);
            Equal("", ((TextBox)dialog.FindName("FileLinkInput")).Text);
        }
        finally { dialog.Close(); }
    });
}
