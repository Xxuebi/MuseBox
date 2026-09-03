using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenshotCollector.Services;
using ScreenshotCollector.Controls;
using System.Windows.Media;

namespace ScreenshotCollector;

public partial class MainWindow
{
    private readonly DispatcherTimer _drawerHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _drawerScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private Button? _drawerPressedButton;
    private Point _drawerPressPoint;
    private string? _draggingDrawerId;
    private string[]? _drawerOrderBeforeDrag;
    private DrawerMenuPopup? _drawerMenu;
    private bool _suppressDrawerClick;

    private void InitializeDrawerGestures()
    {
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (_drawerMenu is not { IsExpanded: true } menu) return;
            if (menu.PlacementTarget is FrameworkElement opener &&
                new Rect(0, 0, opener.ActualWidth, opener.ActualHeight).Contains(e.GetPosition(opener))) return;
            if (menu.Child is FrameworkElement child &&
                new Rect(0, 0, child.ActualWidth, child.ActualHeight).Contains(e.GetPosition(child))) return;
            menu.Dismiss();
        }), true);
        DrawerScroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) _drawerMenu?.Dismiss(); };
        _drawerHoldTimer.Tick += (_, _) => BeginDrawerReorder();
        _drawerScrollTimer.Tick += (_, _) =>
        {
            if (_draggingDrawerId is null) return;
            var point = Mouse.GetPosition(DrawerScroll);
            if (point.Y < 25) DrawerScroll.ScrollToVerticalOffset(DrawerScroll.VerticalOffset - 12);
            else if (point.Y > DrawerScroll.ActualHeight - 25)
                DrawerScroll.ScrollToVerticalOffset(DrawerScroll.VerticalOffset + 12);
            DrawerScroll.UpdateLayout();
            UpdateDrawerReorderAt(point);
        };
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            if (_draggingDrawerId is not null) { UpdateDrawerReorderAt(e.GetPosition(DrawerScroll)); e.Handled = true; }
        }), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(async (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _drawerHoldTimer.Stop();
            _drawerPressedButton = null;
            if (_draggingDrawerId is not null) { e.Handled = true; await FinishDrawerReorderAsync(true); }
        }), true);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Down && _drawerMenu is { IsExpanded: true } menu)
            {
                menu.Actions.Children.OfType<Button>().FirstOrDefault(x => x.IsEnabled)?.Focus();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Escape) return;
            if (_draggingDrawerId is not null) { e.Handled = true; await FinishDrawerReorderAsync(false); }
            _drawerMenu?.Dismiss();
        };
        LostMouseCapture += async (_, _) =>
        {
            if (_draggingDrawerId is not null && Mouse.Captured != DrawerScroll)
                await FinishDrawerReorderAsync(false);
        };
        Deactivated += (_, _) => CancelDrawerGesture();
        SizeChanged += (_, _) => CancelDrawerGesture();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelDrawerGesture(); };
        Closed += (_, _) => CancelDrawerGesture();
    }

    private void ApplyDrawerLetterVisibility()
    {
        foreach (var drawer in _drawers) drawer.ShowLetter = _settings.ShowDrawerLetters;
    }

    private void OnDrawerSettingsPress(object sender, MouseButtonEventArgs e)
    {
        if (_isBusy || CollectionTransitioning || sender is not Button button) return;
        _suppressDrawerClick = false;
        _drawerPressedButton = button;
        _drawerPressPoint = e.GetPosition(DrawerScroll);
        _drawerHoldTimer.Stop();
        _drawerHoldTimer.Start();
    }

    private void OnDrawerSettingsPointerMove(object sender, MouseEventArgs e)
    {
        if (_draggingDrawerId is not null || _drawerPressedButton is null) return;
        var delta = e.GetPosition(DrawerScroll) - _drawerPressPoint;
        if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _drawerHoldTimer.Stop();
            _drawerPressedButton = null;
        }
    }

    private void OnDrawerSettingsClick(object sender, RoutedEventArgs e)
    {
        _drawerHoldTimer.Stop();
        _drawerPressedButton = null;
        if (_suppressDrawerClick) { _suppressDrawerClick = false; return; }
        if (_isBusy || CollectionTransitioning || sender is not Button { Tag: string id } button) return;
        if (_drawerMenu?.IsOpen == true && _drawerMenu.PlacementTarget == button)
        {
            if (_drawerMenu.IsExpanded) _drawerMenu.Dismiss();
            else _drawerMenu.ShowMenu();
            return;
        }
        _drawerMenu?.Dismiss(false);
        var model = _drawers.First(x => x.Id == id);
        _drawerMenu = CreateDrawerMenu(model);
        _drawerMenu.PlacementTarget = button;
        _drawerMenu.Placement = PlacementMode.Bottom;
        _drawerMenu.HorizontalOffset = button.ActualWidth - 198;
        _drawerMenu.VerticalOffset = -5;
        _drawerMenu.ShowMenu();
    }

    private DrawerMenuPopup CreateDrawerMenu(DrawerCardModel model)
    {
        var menu = new DrawerMenuPopup();
        Button AddAction(string label, string glyph, RoutedEventHandler handler)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var action = new Button { Tag = model.Id, Content = content, Height = 36,
                Padding = new Thickness(10, 4, 6, 4), Style = (Style)FindResource("DrawerActionButton") };
            action.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, label);
            action.Click += (sender, args) => { menu.Dismiss(); handler(sender, args); };
            menu.Actions.Children.Add(action);
            return action;
        }
        AddAction("打开", "\uE8E5", OnOpenSceneClick);
        AddAction("保存", "\uE74E", OnSaveSceneClick);
        AddAction("另存为", "\uE792", OnSaveSceneAsClick);
        var separator = new Border { Height = 1, Margin = new Thickness(8, 5, 8, 5) };
        separator.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
        menu.Actions.Children.Add(separator);
        AddAction("重命名", "\uE70F", OnRenameDrawerClick);
        AddAction(model.Cover is null ? "设置封面" : "编辑封面", "\uEB9F", OnSetDrawerCoverClick);
        if (model.Cover is not null) AddAction("移除封面", "\uE894", OnClearDrawerCoverClick);
        var delete = AddAction(model.IsBuiltIn ? "清空并重置" : "删除抽屉", "\uE74D", OnDeleteDrawerClick);
        delete.IsEnabled = model.Id != "A";
        if (!delete.IsEnabled) delete.Opacity = .4;
        delete.ToolTip = model.Id == "A" ? "保留抽屉 A 不能删除" : model.DeleteToolTip;
        menu.Child.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                menu.Dismiss();
                menu.PlacementTarget?.Focus();
                args.Handled = true;
            }
            else if (args.Key is Key.Up or Key.Down or Key.Home or Key.End)
            {
                var buttons = menu.Actions.Children.OfType<Button>().Where(x => x.IsEnabled).ToArray();
                var current = Array.FindIndex(buttons, x => x.IsKeyboardFocusWithin);
                var next = args.Key switch { Key.Home => 0, Key.End => buttons.Length - 1,
                    Key.Up => (current - 1 + buttons.Length) % buttons.Length, _ => (current + 1) % buttons.Length };
                buttons[next].Focus();
                args.Handled = true;
            }
        };
        return menu;
    }

    private void BeginDrawerReorder()
    {
        _drawerHoldTimer.Stop();
        if (_isBusy || CollectionTransitioning ||
            _drawerPressedButton is not { Tag: string id } || Mouse.LeftButton != MouseButtonState.Pressed) return;
        _drawerMenu?.Dismiss(false);
        _drawerPressedButton = null;
        _suppressDrawerClick = true;
        _drawerOrderBeforeDrag = _drawers.Select(x => x.Id).ToArray();
        _draggingDrawerId = id;
        StartDrawerDragVisual(id, _drawerPressPoint);
        _drawers.First(x => x.Id == id).IsDragging = true;
        SetBusy(true);
        DrawerScroll.Cursor = Cursors.SizeAll;
        if (!Mouse.Capture(DrawerScroll)) { CancelDrawerGesture(); return; }
        _drawerScrollTimer.Start();
        SetStatus("拖动调整抽屉位置 · 松开保存 · Esc 取消", false);
    }

    private void UpdateDrawerReorderAt(Point point)
    {
        UpdateDrawerDragVisual(point);
        if (_draggingDrawerId is null || point.X < 0 || point.X > DrawerScroll.ActualWidth) return;
        var nearest = -1;
        var distance = double.PositiveInfinity;
        for (var i = 0; i < _drawers.Count; i++)
        {
            if (DrawerList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            // Hit-test final slots, not the animated card positions, to avoid
            // oscillating between two slots while the neighbours are moving.
            var center = DrawerLayoutOrigin(container) + new Vector(container.ActualWidth / 2, container.ActualHeight / 2);
            var delta = center - point;
            if (delta.LengthSquared < distance) { distance = delta.LengthSquared; nearest = i; }
        }
        var from = _drawers.IndexOf(_drawers.First(x => x.Id == _draggingDrawerId));
        if (nearest >= 0 && nearest != from)
        {
            var before = CaptureDrawerPositions();
            _drawers.Move(from, nearest);
            AnimateDrawerLayout(before);
        }
    }

    private void RestoreDrawerOrder(IReadOnlyList<string> ids)
    {
        var before = CaptureDrawerPositions();
        for (var i = 0; i < ids.Count; i++)
        {
            var model = _drawers.FirstOrDefault(x => x.Id == ids[i]);
            if (model is not null && _drawers.IndexOf(model) != i) _drawers.Move(_drawers.IndexOf(model), i);
        }
        AnimateDrawerLayout(before);
    }

    private async Task SaveDrawerOrderAsync(IReadOnlyList<string> before)
    {
        try { await _repository.UpdateDrawerOrderAsync(_drawers.Select(x => x.Id).ToArray()); }
        catch { RestoreDrawerOrder(before); throw; }
    }

    private async Task FinishDrawerReorderAsync(bool save)
    {
        var before = _drawerOrderBeforeDrag;
        _draggingDrawerId = null;
        _drawerOrderBeforeDrag = null;
        _drawerScrollTimer.Stop();
        _drawerHoldTimer.Stop();
        DrawerScroll.Cursor = Cursors.Arrow;
        if (Mouse.Captured == DrawerScroll) Mouse.Capture(null);
        if (before is null) { ClearDrawerDragVisual(); return; }
        try
        {
            if (save) { await SaveDrawerOrderAsync(before); SetStatus("抽屉顺序已保存", false); }
            else { RestoreDrawerOrder(before); SetStatus("已取消抽屉排序", false); }
        }
        catch (Exception error) { SetStatus($"排序保存失败：{Friendly(error)}", true); }
        finally
        {
            await SettleDrawerDragVisualAsync();
            foreach (var model in _drawers) model.IsDragging = false;
            ClearDrawerDragVisual();
            _suppressDrawerClick = false;
            SetBusy(false);
        }
    }

    private void CancelDrawerGesture()
    {
        _drawerHoldTimer.Stop();
        _drawerPressedButton = null;
        _drawerMenu?.Dismiss();
        if (_draggingDrawerId is not null) _ = FinishDrawerReorderAsync(false);
        else ClearDrawerDragVisual();
    }
}
