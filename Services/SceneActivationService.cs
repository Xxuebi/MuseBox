using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace ScreenshotCollector.Services;

public sealed class SceneActivationService : IDisposable
{
    public static string PipeName => "MuseBox.Scene." + WindowsIdentity.GetCurrent().User!.Value + "." + System.Diagnostics.Process.GetCurrentProcess().SessionId;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _pipeName;
    private readonly Action<string[]> _received;
    private Task? _listener;
    public SceneActivationService(Action<string[]> received, string? pipeName = null)
        => (_received, _pipeName) = (received, pipeName ?? PipeName);
    public void Start() => _listener ??= ListenAsync();
    public static string[] ScenePaths(IEnumerable<string> args) => args
        .Where(a => SceneFileService.IsSupportedExtension(Path.GetExtension(a)))
        .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, timeout.Token);
                var size = BitConverter.ToInt32(header);
                if (size is < 2 or > 1024 * 1024) continue;
                var bytes = new byte[size];
                await pipe.ReadExactlyAsync(bytes, timeout.Token);
                var paths = JsonSerializer.Deserialize<string[]>(bytes) ?? Array.Empty<string>();
                if (paths.Length > 32 || paths.Any(p => p is null || !Path.IsPathFullyQualified(p) || p.Length > 32768)) continue;
                _received(ScenePaths(paths));
                await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or ArgumentException) { }
        }
    }
    public static async Task SendAsync(string[] paths, string? pipeName = null, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        var data = JsonSerializer.SerializeToUtf8Bytes(ScenePaths(paths));
        await pipe.WriteAsync(BitConverter.GetBytes(data.Length), timeout.Token);
        await pipe.WriteAsync(data, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        var ack = new byte[1];
        await pipe.ReadExactlyAsync(ack, timeout.Token);
        if (ack[0] != 1) throw new IOException("正在运行的实例未接收场景。");
    }
    public void Dispose() { _lifetime.Cancel(); }
}
