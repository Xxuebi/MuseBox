using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class App
{
    private SceneActivationService? _sceneActivation;
    private readonly TaskCompletionSource _sceneStartup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _sceneOpenGate = new(1, 1);

    private void InitializeSceneActivation()
    {
        _sceneActivation = new SceneActivationService(paths =>
            Dispatcher.BeginInvoke(new Action(() => _ = HandleSceneFilesAsync(paths))));
        _sceneActivation.Start();
    }
    private async Task HandleSceneFilesAsync(string[] paths)
    {
        try
        {
            await _sceneStartup.Task;
            await _sceneOpenGate.WaitAsync();
            try
            {
                foreach (var path in paths)
                {
                    while (!IsExiting && CollectorWindow.IsOperationBusy) await Task.Delay(100);
                    if (IsExiting) return;
                    ShowCollector();
                    await CollectorWindow.OpenSceneFileAsync(path);
                }
            }
            finally { _sceneOpenGate.Release(); }
        }
        catch (Exception error) { if (!IsExiting) PromptWindow.Inform("无法打开场景", error.Message); }
    }
}
