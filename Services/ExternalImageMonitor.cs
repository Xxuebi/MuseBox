using System.Collections.Concurrent;
using ScreenshotCollector.Models;
namespace ScreenshotCollector.Services;

// File notifications are hints; periodic probing also handles renamed/recreated folders and lost events.
public sealed class ExternalImageMonitor : IDisposable
{
    public sealed record Change(string DrawerId, string Path);
    private sealed class State
    {
        public string Signature = "";
        public DateTime Since;
        public string AppliedSignature = "";
        public string Hash = "";
    }
    private readonly IBoardRepository _repository;
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _events = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    public ExternalImageMonitor(IBoardRepository repository) => _repository = repository;
    public async Task<IReadOnlyList<Change>> PollAsync()
    {
        if (_disposed) return Array.Empty<Change>();
        var assets = await _repository.GetLinkedAssetsAsync();
        if (_disposed) return Array.Empty<Change>();
        var directories = assets.Select(a => Path.GetDirectoryName(a.FileName)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _watchers.Keys.Where(p => !directories.Contains(p)).ToArray())
        { _watchers[stale].Dispose(); _watchers.Remove(stale); }
        foreach (var directory in directories.Where(p => !_watchers.ContainsKey(p) && Directory.Exists(p)))
        {
            try
            {
                var watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler changed = (_, e) => _events.AddOrUpdate(e.FullPath, 1, (_, n) => n + 1);
                watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
                watcher.Renamed += (_, e) => { changed(watcher, e); _events.AddOrUpdate(e.OldFullPath, 1, (_, n) => n + 1); };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(directory, watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var changes = new List<Change>();
        foreach (var asset in assets)
        {
            var path = asset.FileName;
            if (!_states.TryGetValue(path, out var state)) _states[path] = state = new State { Hash = asset.Hash };
            string signature;
            try { var file = new FileInfo(path); signature = file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { signature = "unreadable"; }
            signature += ":" + _events.GetValueOrDefault(path);
            if (state.Signature != signature) { state.Signature = signature; state.Since = DateTime.UtcNow; continue; }
            if ((state.AppliedSignature == signature && state.Hash != "unavailable") ||
                DateTime.UtcNow - state.Since < TimeSpan.FromMilliseconds(500)) continue;
            var updated = asset;
            string digest;
            try
            {
                updated = await Task.Run(async () =>
                {
                    using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    ImageFileFormatService.Invalidate(path);
                    AssetPathResolver.ValidateReadableImage(path);
                    using var image = System.Drawing.Image.FromFile(path);
                    return asset with { Hash = await SceneFileService.HashFileAsync(path),
                        PixelWidth = image.Width, PixelHeight = image.Height, Extension = ImageFileFormatService.FromFile(path)! };
                });
                digest = updated.Hash;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
            { digest = "unavailable"; }
            if (_disposed) break;
            if (state.Hash != digest)
            {
                foreach (var drawer in await _repository.RefreshLinkedAssetAsync(updated))
                    changes.Add(new Change(drawer, path));
            }
            state.Hash = digest;
            state.AppliedSignature = signature;
        }
        var active = assets.Select(a => a.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _states.Keys.Where(p => !active.Contains(p)).ToArray()) { _states.Remove(stale); _events.TryRemove(stale, out _); }
        return changes;
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }
}

