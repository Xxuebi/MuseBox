using System.Windows;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class MainWindow
{
    private readonly CancellationTokenSource _captureLifetime = new();
    private bool _mainClosed;

    private async Task CaptureWithSystemAsync()
    {
        var result = await new SystemScreenshotService().CaptureAsync(_captureLifetime.Token);
        switch (result)
        {
            case SystemSnipResult.Captured:
                ClearClipboardButton.Visibility = Visibility.Visible;
                SetStatus(IsCollectionMode ? "系统截图已进入剪贴板，点击抽屉收集。" : "系统截图已进入剪贴板，点击抽屉上方收集。", false);
                break;
            case SystemSnipResult.Cancelled:
                SetStatus("已取消系统截图。", false);
                break;
            case SystemSnipResult.Unavailable:
                SetStatus("未能打开系统截图，请检查 Windows 截图工具，或关闭系统截图选项。", true);
                break;
            default:
                SetStatus("系统截图等待超时，请重试。", true);
                break;
        }
    }
}
