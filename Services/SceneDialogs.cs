using System.Windows;

namespace ScreenshotCollector.Services;

public interface ISceneDialogs
{
    string? OpenFile(Window owner);
    string? SaveFile(Window owner, string filename, bool saveAs);
    string? SaveExportPng(Window owner, string filename);
    int Choose(Window owner, string title, string message, string primary, string alternative);
    void Inform(Window owner, string title, string message);
    (int Choice, bool Remember) ChooseImageImport(Window owner, string drawer, int count) =>
        (Choose(owner, "导入图像", SceneDialogs.ImageImportMessage(drawer, count), "复制进画板", "链接原文件"), false);
    (int Choice, bool Remember) ChooseLinkedSave(Window owner) =>
        (Choose(owner, "保存外部链接", "保持链接不打包原图；复制进画板后不再依赖原文件。", "复制进画板", "保持链接"), false);
}

public sealed class SceneDialogs : ISceneDialogs
{
    public static string ImageImportMessage(string drawer, int count) =>
        $"目标抽屉：{drawer}　图片：{count} 张\n\n复制进画板：将图像复制进画板，不依赖原文件\n链接原文件：仅链接图片，不复制进画板，移动或删除原文件将使链接失效";
    public (int Choice, bool Remember) ChooseImageImport(Window owner, string drawer, int count) =>
        PromptWindow.ChooseRemember(owner, "导入图像", ImageImportMessage(drawer, count),
            "复制进画板", "链接原文件", "保存选择，下次不提醒");
    public (int Choice, bool Remember) ChooseLinkedSave(Window owner) => PromptWindow.ChooseRemember(owner,
        "保存外部链接", "此画板包含外部链接。\n\n复制进画板：将当前图片收进资料库并打包到场景，不再依赖原文件。\n保持链接：仅保存原文件绝对路径，移动场景到其他电脑时需要原图。", "复制进画板", "保持链接");
    public string? OpenFile(Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 .mubo 文件", Filter = "MuseBox 画板 (*.mubo)|*.mubo",
            DefaultExt = SceneFileService.Extension, Multiselect = false, CheckFileExists = true
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
