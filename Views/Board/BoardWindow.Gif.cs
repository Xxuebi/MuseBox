using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private sealed class GifBinding
    {
        public string Path { get; }
        public Image Target { get; set; }
        public GifPlaybackState? Playback { get; set; }
        public bool Failed { get; set; }
        public Task Loading { get; set; } = Task.CompletedTask;
        public GifBinding(string path, Image target) { Path = path; Target = target; }
    }
    private readonly Dictionary<string, GifBinding> _gifBindings = new();
    private readonly Dictionary<string, Task<GifAnimation>> _gifLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _gifLifetime = new();
    private readonly DispatcherTimer _gifTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private long _gifLastTick;
    private string? _gifFramesItemId, _gifContextItemId;
    private bool _syncingGifFrames;

    private void InitializeGifPlayback()
    {
        _gifTimer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Math.Min(1000, (now - _gifLastTick) * 1000d / Stopwatch.Frequency);
            _gifLastTick = now;
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            foreach (var binding in _gifBindings.Values)
                if (binding.Playback is { } playback && playback.Advance(elapsed))
                    binding.Target.Source = playback.Animation.Frames[playback.FrameIndex].Image;
            UpdateGifToolbar();
        };
        Closed += (_, _) =>
        {
            _gifTimer.Stop();
            _gifLifetime.Cancel();
            _gifBindings.Clear();
            _gifLoads.Clear();
            GifFramesList.ItemsSource = null;
        };
    }

    private Task AttachGifAsync(BoardItem item, Image target)
    {
        if (_gifBindings.TryGetValue(item.Id, out var existing) && existing.Path == item.AssetPath)
        {
            existing.Target = target;
            if (existing.Playback is { } state) target.Source = state.Animation.Frames[state.FrameIndex].Image;
            return existing.Loading;
        }
        var binding = new GifBinding(item.AssetPath, target);
        _gifBindings[item.Id] = binding;
        return binding.Loading = LoadGifBindingAsync(item, binding);
    }

    private async Task LoadGifBindingAsync(BoardItem item, GifBinding binding)
    {
        try
        {
            if (!_gifLoads.TryGetValue(item.AssetPath, out var load))
                _gifLoads[item.AssetPath] = load = GifAnimationService.LoadAsync(item.AssetPath, _gifLifetime.Token);
            var animation = await load.ConfigureAwait(false);
            if (_gifLifetime.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_gifLifetime.IsCancellationRequested || !_gifBindings.TryGetValue(item.Id, out var current) || current != binding) return;
                binding.Playback = new GifPlaybackState(animation);
                if (_savedGifStates.TryGetValue(item.Id, out var saved))
                {
                    binding.Playback.SetSpeed(saved.Speed);
                    binding.Playback.Seek(saved.FrameIndex);
                    binding.Playback.SetPlaying(saved.IsPlaying);
                }
                binding.Target.Source = animation.Frames[binding.Playback.FrameIndex].Image;
                if (!_gifTimer.IsEnabled)
                {
                    _gifLastTick = Stopwatch.GetTimestamp();
                    _gifTimer.Start();
                }
                UpdateImageToolbar();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_gifLifetime.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                binding.Failed = true;
                BoardStatus.Text = $"无法播放 GIF：{error.Message}";
                UpdateGifToolbar();
            });
        }
    }

    private void PruneGifBindings()
    {
        foreach (var pair in _gifBindings.ToArray())
            if (!_items.Any(item => item.Id == pair.Key && item.AssetPath == pair.Value.Path))
                _gifBindings.Remove(pair.Key);
        var paths = _items.Where(item => GifAnimationService.IsGif(item.AssetPath)).Select(item => item.AssetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _gifLoads.Keys.Where(path => !paths.Contains(path)).ToArray()) _gifLoads.Remove(path);
        if (_gifBindings.Count == 0) _gifTimer.Stop();
    }

    private GifPlaybackState? SelectedGif() => SelectedToolbarImage() is { } item &&
        _gifBindings.TryGetValue(item.Id, out var binding) ? binding.Playback : null;

    private void UpdateGifToolbar()
    {
        if (GifImageTools.Visibility != Visibility.Visible) return;
        var state = SelectedGif();
        var playing = state is { IsPlaying: true };
        GifPlaybackButton.IsEnabled = state is not null;
        GifPlaybackButton.ToolTip = state is null ? "载入中…" : playing ? "暂停" : "播放";
        System.Windows.Automation.AutomationProperties.SetName(GifPlaybackButton, playing ? "暂停" : "播放");
        GifPlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        GifPauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        GifPreviousButton.IsEnabled = GifNextButton.IsEnabled = state?.Animation.Frames.Count > 1;
        GifSpeedButton.IsEnabled = GifFramesButton.IsEnabled = state is not null;
        GifSpeedLabel.Text = $"{state?.Speed ?? 1:0.##}×";
        GifFrameStatus.Text = state is null ? "载入中…" : $"{state.FrameIndex + 1} / {state.Animation.Frames.Count}";
        if (state is null && SelectedToolbarImage() is { } item && _gifBindings.TryGetValue(item.Id, out var binding) && binding.Failed)
            GifFrameStatus.Text = "载入失败";
        if (state is not null && _gifFramesItemId is not null)
        {
            _syncingGifFrames = true;
            try { GifFramesList.SelectedIndex = state.FrameIndex; }
            finally { _syncingGifFrames = false; }
        }
    }

    private void RefreshSelectedGif()
    {
        if (SelectedToolbarImage() is { } item && _gifBindings.TryGetValue(item.Id, out var binding) && binding.Playback is { } state)
            binding.Target.Source = state.Animation.Frames[state.FrameIndex].Image;
        UpdateGifToolbar();
        QueueGifStateSave();
    }
    private void OnGifPlaybackToggleClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGif() is not { } state) return;
        state.SetPlaying(!state.IsPlaying);
        RefreshSelectedGif();
    }
    private void OnGifStepClick(object sender, RoutedEventArgs e)
    {
        SelectedGif()?.Step((sender as FrameworkElement)?.Tag?.ToString() == "-1" ? -1 : 1);
        RefreshSelectedGif();
    }
    private void OnGifSpeedClick(object sender, RoutedEventArgs e)
    {
        if (GifSpeedPopup.IsOpen) { GifSpeedPopup.IsOpen = false; return; }
        CloseToolPopups();
        GifSpeedPopup.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        GifSpeedPopup.HorizontalOffset = (GifSpeedButton.ActualWidth - GifSpeedPopup.Child.DesiredSize.Width) / 2;
        GifSpeedPopup.IsOpen = true;
    }
    private void OnGifSpeedSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string text } && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            SelectedGif()?.SetSpeed(speed);
        GifSpeedPopup.IsOpen = false;
        UpdateGifToolbar();
        QueueGifStateSave();
    }
    private void CloseGifFrames()
    {
        _gifFramesItemId = null;
        PopupTransitions.HidePanel(GifFramesPanel);
        GifFramesPanel.Visibility = Visibility.Collapsed;
        GifFramesList.ItemsSource = null;
    }
    private void OnGifFramesClick(object sender, RoutedEventArgs e)
    {
        if (_gifFramesItemId is not null) { CloseGifFrames(); return; }
        var item = SelectedToolbarImage();
        var state = SelectedGif();
        if (item is null || state is null) return;
        state.SetPlaying(false);
        QueueGifStateSave();
        _gifFramesItemId = item.Id;
        GifFramesPanel.Visibility = Visibility.Visible;
        GifFramesTitle.Text = $"全部帧 · 共 {state.Animation.Frames.Count} 帧";
        _syncingGifFrames = true;
        try
        {
            GifFramesList.ItemsSource = state.Animation.Frames;
            GifFramesList.SelectedIndex = state.FrameIndex;
        }
        finally { _syncingGifFrames = false; }
        UpdateImageToolbar();
        PopupTransitions.ShowPanel(GifFramesPanel);
    }
    private void OnGifFrameSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGifFrames || GifFramesList.SelectedItem is not GifFrame frame) return;
        SelectedGif()?.Seek(frame.Index);
        RefreshSelectedGif();
    }
    private void OnGifFramePointerDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != GifFramesList; node = GetTreeParent(node))
            if (node is ListBoxItem { DataContext: GifFrame frame })
            {
                SelectedGif()?.Seek(frame.Index);
                RefreshSelectedGif();
                break;
            }
    }
    private void PositionGifFrames(Rect bounds)
    {
        if (_gifFramesItemId != SelectedToolbarImage()?.Id) CloseGifFrames();
        if (_gifFramesItemId is null) return;
        GifFramesPanel.Width = Math.Min(366, Math.Max(150, BoardSurface.ActualWidth - 16));
        var columns = Math.Max(1, (int)((GifFramesPanel.Width - 40) / 108));
        var contentHeight = Math.Ceiling((SelectedGif()?.Animation.Frames.Count ?? 1) / (double)columns) * 90 + 56;
        GifFramesPanel.Height = Math.Min(contentHeight, Math.Min(340, Math.Max(120, BoardSurface.ActualHeight - 100)));
        var x = bounds.Right + 12;
        if (x + GifFramesPanel.Width > BoardSurface.ActualWidth - 8) x = bounds.Left - GifFramesPanel.Width - 12;
        if (x < 8) x = BoardSurface.ActualWidth - GifFramesPanel.Width - 8;
        PopupTransitions.SetPanelPlacement(GifFramesPanel, x >= bounds.Right ? PlacementMode.Right : PlacementMode.Left);
        Canvas.SetLeft(GifFramesPanel, Math.Max(8, x));
        Canvas.SetTop(GifFramesPanel, Math.Clamp(bounds.Top, 82, Math.Max(82, BoardSurface.ActualHeight - GifFramesPanel.Height - 8)));
    }

    private void ConfigureGifContextMenu(DependencyObject? source)
    {
        _gifContextItemId = null;
        for (var node = source; node is not null && node != BoardSurface; node = GetTreeParent(node))
        {
            if (node is Border { Tag: string id } && _gifBindings.TryGetValue(id, out var binding) &&
                binding.Playback is { IsPlaying: false })
            { _gifContextItemId = id; break; }
        }
        SaveGifFrameMenuItem.Visibility = _gifContextItemId is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void OnSaveGifFrameClick(object sender, RoutedEventArgs e)
    {
        if (_gifContextItemId is not { } id || _imageEditBusy ||
            !_gifBindings.TryGetValue(id, out var binding) || binding.Playback is not { IsPlaying: false } state ||
            _items.FirstOrDefault(item => item.Id == id) is not { } item) return;
        var frameIndex = state.FrameIndex;
        await SaveGifFrameAsync(item, frameIndex);
    }

    private async Task SaveGifFrameAsync(BoardItem item, int frameIndex)
    {
        _imageEditBusy = true;
        BoardSurface.IsHitTestVisible = false;
        try
        {
            using var bitmap = await Task.Run(() => GifAnimationService.ExtractFrame(item.AssetPath, frameIndex));
            await AddEditedImageAsync(item, bitmap);
            BoardStatus.Text = $"已将第 {frameIndex + 1} 帧另存为新图片";
        }
        catch (Exception error) { BoardStatus.Text = $"帧另存失败：{error.Message}"; }
        finally { _imageEditBusy = false; BoardSurface.IsHitTestVisible = true; }
    }
}
