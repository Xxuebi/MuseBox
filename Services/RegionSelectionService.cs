using System.Windows;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public sealed class RegionSelectionService : IRegionSelectionService
{
    public async Task<RegionSelectionResult?> SelectRegionAsync(
        IReadOnlyList<CapturedScreen> screens,
        CancellationToken cancellationToken = default)
    {
        if (screens.Count == 0)
        {
            throw new ArgumentException("至少需要一块已采集的显示器。", nameof(screens));
        }

        var completion = new TaskCompletionSource<RegionSelectionResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var overlays = new List<SelectionOverlayWindow>();
        var isClosing = false;

        void Finish(RegionSelectionResult? result)
        {
            if (!completion.TrySetResult(result))
            {
                return;
            }

            isClosing = true;
            foreach (var overlay in overlays)
            {
                overlay.Close();
            }
        }

        foreach (var screen in screens)
        {
            var overlay = new SelectionOverlayWindow(screen);
            overlay.SelectionCompleted += (_, args) =>
                Finish(new RegionSelectionResult(args.Screen, args.PixelBounds));
            overlay.SelectionCancelled += (_, _) =>
            {
                if (!isClosing)
                {
                    Finish(null);
                }
            };
            overlays.Add(overlay);
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            Application.Current.Dispatcher.BeginInvoke(() => Finish(null));
        });

        foreach (var overlay in overlays)
        {
            overlay.Show();
        }

        overlays[^1].Activate();
        return await completion.Task;
    }
}
