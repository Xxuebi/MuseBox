using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScreenshotCollector.Services;

public sealed record ShellIntegrationStatus(
    bool FilesAvailable,
    bool AssociationInstalled,
    bool ThumbnailInstalled,
    string AssociationDetail,
    string ThumbnailDetail);

public static class ShellIntegrationService
{
    public const string SceneProgId = "MuseBox.Scene";
    public const string ThumbnailClassId = "{6F67433A-1EA6-47D0-982B-30EFAE588F38}";
    public const string ThumbnailHandlerId = "{E357FCCD-A995-4576-B01F-234630154E96}";
    private const string ThumbnailProgId = "MuseBox.SceneThumbnailProvider";
    private const string ManagedComponentCategory = "{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}";
    private const string Classes = @"Software\Classes";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    public static ShellIntegrationStatus GetStatus()
    {
        var paths = Paths();
        var filesAvailable = File.Exists(paths.Application) && File.Exists(paths.SceneIcon) &&
            File.Exists(paths.Provider);
        var association = string.Equals(Read($@"{Classes}\.mubo"), SceneProgId, StringComparison.Ordinal) &&
            SamePath(Unquote(Read($@"{Classes}\{SceneProgId}\DefaultIcon").Split(',')[0]), paths.SceneIcon) &&
            CommandTargets(Read($@"{Classes}\{SceneProgId}\shell\open\command"), paths.Application);
        var codeBase = Read($@"{Classes}\CLSID\{ThumbnailClassId}\InprocServer32", "CodeBase");
        var thumbnail = string.Equals(Read($@"{Classes}\.mubo\ShellEx\{ThumbnailHandlerId}"),
                ThumbnailClassId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Read($@"{Classes}\SystemFileAssociations\.mubo\ShellEx\{ThumbnailHandlerId}"),
                ThumbnailClassId, StringComparison.OrdinalIgnoreCase) &&
            SamePath(UriPath(codeBase), paths.Provider);
        return new ShellIntegrationStatus(filesAvailable, association, thumbnail,
            association ? "已安装" : "未安装或路径已改变",
            thumbnail ? "已安装" : filesAvailable ? "未安装或需要修复" : "当前程序目录缺少缩略图组件");
    }

    public static void RepairAssociation()
    {
        var paths = Paths();
        RequireFile(paths.Application, "MuseBox.exe");
        RequireFile(paths.SceneIcon, "场景文件图标");
        using (var extension = Registry.CurrentUser.CreateSubKey($@"{Classes}\.mubo", true))
            extension.SetValue(string.Empty, SceneProgId);
        using (var openWith = Registry.CurrentUser.CreateSubKey($@"{Classes}\.mubo\OpenWithProgids", true))
            openWith.SetValue(SceneProgId, string.Empty);
        using (var kind = Registry.CurrentUser.CreateSubKey($@"{Classes}\{SceneProgId}", true))
            kind.SetValue(string.Empty, "MuseBox 场景");
        using (var icon = Registry.CurrentUser.CreateSubKey($@"{Classes}\{SceneProgId}\DefaultIcon", true))
            icon.SetValue(string.Empty, Quote(paths.SceneIcon) + ",0");
        using (var command = Registry.CurrentUser.CreateSubKey($@"{Classes}\{SceneProgId}\shell\open\command", true))
            command.SetValue(string.Empty, Quote(paths.Application) + " \"%1\"");
        NotifyShell();
    }

    public static void UninstallAssociation()
    {
        using (var extension = Registry.CurrentUser.OpenSubKey($@"{Classes}\.mubo", true))
            if (string.Equals(extension?.GetValue(string.Empty) as string, SceneProgId, StringComparison.Ordinal))
                extension?.DeleteValue(string.Empty, false);
        using (var openWith = Registry.CurrentUser.OpenSubKey($@"{Classes}\.mubo\OpenWithProgids", true))
            openWith?.DeleteValue(SceneProgId, false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{SceneProgId}", false);
        NotifyShell();
    }

    public static void RepairThumbnailProvider()
    {
        var paths = Paths();
        RequireFile(paths.Provider, "资源管理器缩略图组件");
        var assembly = AssemblyName.GetAssemblyName(paths.Provider);
        var inprocPath = $@"{Classes}\CLSID\{ThumbnailClassId}\InprocServer32";
        using (var progId = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ThumbnailProgId}", true))
            progId.SetValue(string.Empty, "MuseBox.ThumbnailProvider.SceneThumbnailProvider");
        using (var progIdClass = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ThumbnailProgId}\CLSID", true))
            progIdClass.SetValue(string.Empty, ThumbnailClassId);
        using (var classKey = Registry.CurrentUser.CreateSubKey($@"{Classes}\CLSID\{ThumbnailClassId}", true))
            classKey.SetValue(string.Empty, "MuseBox.ThumbnailProvider.SceneThumbnailProvider");
        using (var inproc = Registry.CurrentUser.CreateSubKey(inprocPath, true))
        {
            inproc.SetValue(string.Empty, Path.Combine(Environment.SystemDirectory, "mscoree.dll"));
            inproc.SetValue("ThreadingModel", "Both");
            inproc.SetValue("Class", "MuseBox.ThumbnailProvider.SceneThumbnailProvider");
            inproc.SetValue("Assembly", assembly.FullName ?? assembly.Name ?? "MuseBox.ThumbnailProvider");
            inproc.SetValue("RuntimeVersion", "v4.0.30319");
            inproc.SetValue("CodeBase", new Uri(paths.Provider).AbsoluteUri);
        }
        using (var version = Registry.CurrentUser.CreateSubKey(
                   $@"{inprocPath}\{assembly.Version ?? new Version(1, 0, 40, 0)}", true))
        {
            version.SetValue("Class", "MuseBox.ThumbnailProvider.SceneThumbnailProvider");
            version.SetValue("Assembly", assembly.FullName ?? assembly.Name ?? "MuseBox.ThumbnailProvider");
            version.SetValue("RuntimeVersion", "v4.0.30319");
            version.SetValue("CodeBase", new Uri(paths.Provider).AbsoluteUri);
        }
        using (var progId = Registry.CurrentUser.CreateSubKey(
                   $@"{Classes}\CLSID\{ThumbnailClassId}\ProgId", true))
            progId.SetValue(string.Empty, ThumbnailProgId);
        using var managedCategory = Registry.CurrentUser.CreateSubKey(
            $@"{Classes}\CLSID\{ThumbnailClassId}\Implemented Categories\{ManagedComponentCategory}", true);
        WriteHandler($@"{Classes}\.mubo\ShellEx\{ThumbnailHandlerId}");
        WriteHandler($@"{Classes}\SystemFileAssociations\.mubo\ShellEx\{ThumbnailHandlerId}");
        using (var approved = Registry.CurrentUser.CreateSubKey(Approved, true))
            approved.SetValue(ThumbnailClassId, "MuseBox Scene Thumbnail Provider");
        NotifyShell();
    }

    public static void UninstallThumbnailProvider()
    {
        DeleteHandlerIfOwned($@"{Classes}\.mubo\ShellEx\{ThumbnailHandlerId}");
        DeleteHandlerIfOwned($@"{Classes}\SystemFileAssociations\.mubo\ShellEx\{ThumbnailHandlerId}");
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\CLSID\{ThumbnailClassId}", false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{ThumbnailProgId}", false);
        using (var approved = Registry.CurrentUser.OpenSubKey(Approved, true))
            approved?.DeleteValue(ThumbnailClassId, false);
        NotifyShell();
    }

    internal static (string Application, string SceneIcon, string Provider) Paths(string? baseDirectory = null)
    {
        var root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        return (Path.Combine(root, "MuseBox.exe"),
            Path.Combine(root, "Assets", "scene-icon.ico"),
            Path.Combine(root, "ThumbnailProvider", "MuseBox.ThumbnailProvider.dll"));
    }

    private static void WriteHandler(string keyPath)
    {
        using var handler = Registry.CurrentUser.CreateSubKey(keyPath, true);
        handler.SetValue(string.Empty, ThumbnailClassId);
    }

    private static void DeleteHandlerIfOwned(string keyPath)
    {
        bool owned;
        using (var handler = Registry.CurrentUser.OpenSubKey(keyPath))
            owned = string.Equals(handler?.GetValue(string.Empty) as string,
                ThumbnailClassId, StringComparison.OrdinalIgnoreCase);
        if (!owned) return;
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
    }

    private static string Read(string path, string name = "")
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name) as string ?? string.Empty;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        { return string.Empty; }
    }

    private static string UriPath(string value)
    {
        try { return new Uri(value).LocalPath; }
        catch { return string.Empty; }
    }

    private static string Unquote(string value) => value.Trim().Trim('"');
    private static string Quote(string value) => "\"" + value + "\"";
    private static bool SamePath(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    private static bool CommandTargets(string command, string application) =>
        command.StartsWith(Quote(application), StringComparison.OrdinalIgnoreCase) &&
        command.EndsWith("\"%1\"", StringComparison.Ordinal);
    private static void RequireFile(string path, string label)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{label}不存在，请重新下载完整便携版。", path);
    }
    private static void NotifyShell() => NativeMethods.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    private static class NativeMethods
    {
        [DllImport("shell32.dll")]
        internal static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
