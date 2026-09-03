using System.Windows;

namespace ScreenshotCollector.Services;

public interface ISceneDialogs
{
    string? OpenFile(Window owner);
    string? SaveFile(Window owner, string filename, bool saveAs);
    int Choose(Window owner, string title, string message, string primary, string alternative);
    void Inform(Window owner, string title, string message);
}

public sealed class SceneDialogs : ISceneDialogs
{
    public string? OpenFile(Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开场景",
            Filter = "MuseBox 场景 (*.mubo)|*.mubo|旧版场景 (*.iscene)|*.iscene",
            CheckFileExists = true
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public string? SaveFile(Window owner, string filename, bool saveAs)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = saveAs ? "场景另存为" : "保存场景", Filter = "MuseBox 场景 (*.mubo)|*.mubo",
            DefaultExt = SceneFileService.Extension, AddExtension = true, FileName = filename, OverwritePrompt = true
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public int Choose(Window owner, string title, string message, string primary, string alternative)
        => PromptWindow.Choose(owner, title, message, primary, alternative);
    public void Inform(Window owner, string title, string message)
        => new PromptWindow(title, message, "知道了", false) { Owner = owner }.ShowDialog();
}
