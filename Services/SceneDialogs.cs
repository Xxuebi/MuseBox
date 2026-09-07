using System.Windows;

namespace ScreenshotCollector.Services;

public interface ISceneDialogs
{
    string? SaveFile(Window owner, string filename, bool saveAs);
    string? SaveExportPng(Window owner, string filename);
    int Choose(Window owner, string title, string message, string primary, string alternative);
    void Inform(Window owner, string title, string message);
}

public sealed class SceneDialogs : ISceneDialogs
{
    public string? SaveFile(Window owner, string filename, bool saveAs)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = saveAs ? "场景另存为" : "保存场景", Filter = "MuseBox 场景 (*.mubo)|*.mubo",
            DefaultExt = SceneFileService.Extension, AddExtension = true, FileName = filename, OverwritePrompt = true
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public string? SaveExportPng(Window owner, string filename)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出合成 PNG", Filter = "PNG 图像 (*.png)|*.png",
            DefaultExt = ".png", AddExtension = true, FileName = filename, OverwritePrompt = true
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public int Choose(Window owner, string title, string message, string primary, string alternative)
        => PromptWindow.Choose(owner, title, message, primary, alternative);
    public void Inform(Window owner, string title, string message)
        => new PromptWindow(title, message, "知道了", false) { Owner = owner }.ShowDialog();
}
