using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenshotCollector.Models;
using ScreenshotCollector.Services;

namespace ScreenshotCollector;

internal sealed record TextFontChoice(FontFamily Family, string DisplayName);

public partial class BoardWindow
{
    private bool _erasing;
    private bool _drawingRenderPending;
    private bool _syncingTextToolbar;
    private bool _creatingTextItem;
    private bool _textAutoFitPending;
    private bool _textPalettePositionPending;

    private static readonly double[] TextPointSizes =
        [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 64, 72, 96, 120, 144];

    private void InitializeTextToolbar()
    {
        _syncingTextToolbar = true;
        TextSizeCombo.ItemsSource = TextPointSizes;
        TextSizeCombo.SelectedItem = 16d;
        var fonts = Fonts.SystemFontFamilies
            .Select(font => new TextFontChoice(font, GetLocalizedFontName(font)))
            .OrderBy(font => font.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        TextFontCombo.ItemsSource = fonts;
        TextFontCombo.SelectedItem = fonts.FirstOrDefault(font =>
            font.Family.Source.Equals("Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase)) ?? fonts.FirstOrDefault();
        _syncingTextToolbar = false;
    }

    private static string GetLocalizedFontName(FontFamily font)
    {
        var languages = new[]
        {
            CultureInfo.CurrentUICulture.IetfLanguageTag,
            CultureInfo.CurrentCulture.IetfLanguageTag,
            "zh-CN", "en-US"
        };
        foreach (var language in languages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (font.FamilyNames.TryGetValue(XmlLanguage.GetLanguage(language), out var localized) &&
                !string.IsNullOrWhiteSpace(localized))
                return localized.Equals(font.Source, StringComparison.OrdinalIgnoreCase)
                    ? localized : $"{localized}  ({font.Source})";
        }
        return font.FamilyNames.Values.FirstOrDefault() ?? font.Source;
    }

    private void OnSelectToolClick(object sender, RoutedEventArgs e) => SetToolMode(BoardToolMode.Select);

    private void OnAddTextClick(object sender, RoutedEventArgs e) => SetToolMode(BoardToolMode.Text);

    private void OnDrawClick(object sender, RoutedEventArgs e) => SetToolMode(BoardToolMode.Pen);

    private void SetToolMode(BoardToolMode mode)
    {
        if (_activeTextEditor is not null) _ = CommitTextEditingAsync();
        var wasDrawing = IsDrawingTool(_toolMode);
        var willDraw = IsDrawingTool(mode);
        if (mode != _toolMode && (_previewDrawing is not null || _erasing)) _ = CompleteDrawingAsync();
        if (wasDrawing && !willDraw)
        {
            _drawingSessionId = null;
            _selected.Clear();
        }
        else if (!wasDrawing && willDraw)
        {
            _drawingSessionId = null;
            _selected.Clear();
        }
        _toolMode = mode;
        CloseToolPopups();
        UpdateDrawingToolbarState();
        UpdateTextPaletteForSelection();
        DrawingPalette.Visibility = IsDrawingTool(mode) ? Visibility.Visible : Visibility.Collapsed;
        BoardSurface.Cursor = mode switch
        {
            BoardToolMode.Text => Cursors.IBeam,
            BoardToolMode.Eraser => Cursors.Arrow,
            BoardToolMode.Select => Cursors.Arrow,
            _ => Cursors.Pen
        };
        ApplyPrimaryToolTheme();
        UpdateSelectionVisuals();
        RefreshEraserCursor();
        BoardStatus.Text = mode switch
        {
            BoardToolMode.Text => "文字工具 · 点击画板添加注释 · Esc 返回选择",
            BoardToolMode.Eraser => "橡皮擦 · 拖过笔迹或形状进行擦除",
            BoardToolMode.Select => "滚轮缩放 · 中键或 Space 拖动画布 · Ctrl 多选",
            _ => "绘制工具 · 本次绘制保存为一组 · 关闭后可双击续画"
        };
    }

    private void ApplyPrimaryToolTheme()
    {
        if (SelectToolButton is null || TextToolButton is null || DrawToolButton is null) return;
        var selectedToolBrush = (Brush)FindResource("AccentSubtleBrush");
        var idleToolBrush = (Brush)FindResource("ToolbarButtonBrush");
        SelectToolButton.Background = _toolMode == BoardToolMode.Select ? selectedToolBrush : idleToolBrush;
        TextToolButton.Background = _toolMode == BoardToolMode.Text ? selectedToolBrush : idleToolBrush;
        DrawToolButton.Background = IsDrawingTool(_toolMode) ? selectedToolBrush : idleToolBrush;
    }

    private static bool IsDrawingTool(BoardToolMode mode) =>
        mode is BoardToolMode.Pen or BoardToolMode.Eraser or
            BoardToolMode.Line or BoardToolMode.Arrow or BoardToolMode.Rectangle or BoardToolMode.Ellipse;

    private async Task CreateTextAtAsync(Point world)
    {
        await CommitTextEditingAsync();
        var before = Snapshot();
        var item = new BoardTextItem
        {
            DrawerId = _drawerId,
            X = world.X,
            Y = world.Y,
            Width = 56,
            Height = 40,
            ZIndex = NextZIndex(),
            DocumentData = RichTextDocumentService.Save(RichTextDocumentService.CreateDefault())
        };
        _textItems.Add(item);
        await _repository.AddTextItemsAsync(new[] { item });
        AddTextVisual(item);
        _selected.Clear();
        _selected.Add(item.Id);
        UpdateSelectionVisuals();
        _textEditSnapshot = before;
        _creatingTextItem = true;
        ActivateTextEditor(item);
    }

    private async void BeginTextEditing(BoardTextItem item)
    {
        if (_activeTextItem?.Id == item.Id) return;
        await CommitTextEditingAsync();
        _textEditSnapshot = Snapshot();
        _creatingTextItem = false;
        ActivateTextEditor(item);
    }

    private void ActivateTextEditor(BoardTextItem item)
    {
        if (!_visuals.TryGetValue(item.Id, out var visual) || visual.Editor is null) return;
        _activeTextItem = item;
        _activeTextEditor = visual.Editor;
        _activeTextEditor.IsReadOnly = false;
        _activeTextEditor.Focusable = true;
        _activeTextEditor.IsHitTestVisible = true;
        _activeTextEditor.AcceptsReturn = true;
        _activeTextEditor.AcceptsTab = true;
        _activeTextEditor.SpellCheck.IsEnabled = false;
        DataObject.AddPastingHandler(_activeTextEditor, OnRichTextPasting);
        _activeTextEditor.TextChanged += OnActiveTextChanged;
        UpdateTextPaletteForSelection();
        AutoFitTextItem(item, _activeTextEditor);
        _activeTextEditor.Focus();
        Keyboard.Focus(_activeTextEditor);
        if (string.IsNullOrWhiteSpace(RichTextDocumentService.PlainText(_activeTextEditor.Document)))
            _activeTextEditor.SelectAll();
        BoardStatus.Text = "正在编辑注释 · Ctrl+Enter 完成 · Esc 完成";
    }

    private Task _textCommitTask = Task.CompletedTask;
    private Task CommitTextEditingAsync()
    {
        if (!_textCommitTask.IsCompleted) return _textCommitTask;
        return _textCommitTask = CommitTextEditingCoreAsync();
    }
    private async Task CommitTextEditingCoreAsync()
    {
        if (_activeTextEditor is null || _activeTextItem is null) return;
        var editor = _activeTextEditor;
        var item = _activeTextItem;
        var wasCreating = _creatingTextItem;
        DataObject.RemovePastingHandler(editor, OnRichTextPasting);
        editor.TextChanged -= OnActiveTextChanged;
        item.DocumentData = RichTextDocumentService.Save(editor.Document);
        if (string.IsNullOrWhiteSpace(item.LayerName)) item.LayerName = BoardLayerNameService.DefaultName(item);
        AutoFitTextItem(item, editor);
        editor.IsReadOnly = true;
        editor.Focusable = false;
        editor.IsHitTestVisible = true;
        _activeTextEditor = null;
        _activeTextItem = null;
        _creatingTextItem = false;
        if (string.IsNullOrWhiteSpace(RichTextDocumentService.PlainText(editor.Document)))
        {
            _textEditSnapshot = null;
            _selected.Remove(item.Id);
            _textItems.RemoveAll(x => x.Id == item.Id);
            if (_visuals.Remove(item.Id, out var visual)) WorldCanvas.Children.Remove(visual.Border);
            await _repository.DeleteTextItemsAsync(new[] { item.Id });
            BoardLayerTreeService.RemoveEmptyGroups(_groups, AllElements);
            await PersistLayerTreeAsync();
            if (wasCreating) SetToolMode(BoardToolMode.Select);
            BoardSurface.Focus();
            UpdateSelectionVisuals();
            return;
        }
        if (_textEditSnapshot is not null)
        {
            PushUndoSnapshot(_textEditSnapshot);
            _textEditSnapshot = null;
        }
        await _repository.UpdateTextItemsAsync(new[] { item });
        if (wasCreating) SetToolMode(BoardToolMode.Select);
        BoardSurface.Focus();
        UpdateSelectionVisuals();
    }

    private void OnActiveTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_textAutoFitPending) return;
        _textAutoFitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _textAutoFitPending = false;
            if (_activeTextItem is not null && _activeTextEditor is not null)
            {
                AutoFitTextItem(_activeTextItem, _activeTextEditor);
                UpdateResizeHandles();
            }
        });
    }

    private void AutoFitTextItem(BoardTextItem item, RichTextBox editor)
    {
        var text = RichTextDocumentService.PlainText(editor.Document).Replace("\r", string.Empty);
        var fontSize = GetLargestFontSize(editor.Document);
        var typeface = new Typeface(GetFirstFontFamily(editor.Document), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var lines = text.Split('\n');
        var widest = 0d;
        var totalHeight = 0d;
        const double maximumTextWidth = 680;
        foreach (var sourceLine in lines.Length == 0 ? new[] { string.Empty } : lines)
        {
            var line = string.IsNullOrEmpty(sourceLine) ? " " : sourceLine.Replace("\t", "    ");
            var formatted = new FormattedText(
                line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, dpi);
            var lineWidth = Math.Max(1, formatted.WidthIncludingTrailingWhitespace);
            widest = Math.Max(widest, Math.Min(maximumTextWidth, lineWidth));
            totalHeight += Math.Max(fontSize * 1.28, formatted.Height) *
                           Math.Max(1, Math.Ceiling(lineWidth / maximumTextWidth));
        }
        var horizontalPadding = editor.Document.PagePadding.Left + editor.Document.PagePadding.Right + 6;
        var verticalPadding = editor.Document.PagePadding.Top + editor.Document.PagePadding.Bottom + 6;
        item.Width = Math.Clamp(widest + horizontalPadding, 42, maximumTextWidth + horizontalPadding);
        item.Height = Math.Clamp(totalHeight + verticalPadding, 34, 2000);
        UpdateItemVisual(item);
        PositionTextPalette();
    }

    private static double GetLargestFontSize(FlowDocument document)
    {
        var largest = document.FontSize;
        for (var pointer = document.ContentStart;
             pointer is not null && pointer.CompareTo(document.ContentEnd) < 0;
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            if (pointer.Parent is TextElement element &&
                element.GetValue(TextElement.FontSizeProperty) is double size && double.IsFinite(size))
                largest = Math.Max(largest, size);
        }
        return Math.Max(RichTextDocumentService.ToDip(8), largest);
    }

    private static FontFamily GetFirstFontFamily(FlowDocument document)
    {
        for (var pointer = document.ContentStart;
             pointer is not null && pointer.CompareTo(document.ContentEnd) < 0;
             pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text &&
                pointer.Parent is TextElement element && element.FontFamily is { } family)
                return family;
        }
        return document.FontFamily;
    }

    private BoardTextItem? SelectedTextItem() => _selected.Count == 1
        ? _textItems.FirstOrDefault(item => _selected.Contains(item.Id))
        : null;

    private void UpdateTextPaletteForSelection()
    {
        if (IsDrawingTool(_toolMode))
        {
            TextMorePopup.IsOpen = false;
            TextPalette.Visibility = Visibility.Collapsed;
            return;
        }
        var item = _activeTextItem ?? SelectedTextItem();
        TextPalette.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null)
        {
            TextMorePopup.IsOpen = false;
            return;
        }
        SyncTextToolbar(item);
        PositionTextPalette();
        if (_textPalettePositionPending) return;
        _textPalettePositionPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _textPalettePositionPending = false;
            PositionTextPalette();
        }));
    }

    private void PositionTextPalette()
    {
        if (TextPalette.Visibility != Visibility.Visible) return;
        var item = _activeTextItem ?? SelectedTextItem();
        if (item is null || BoardSurface.ActualWidth <= 0 || BoardSurface.ActualHeight <= 0) return;
        // Position is a render translation, not a margin: DesiredSize includes Margin,
        // so measuring a previously positioned palette used to feed its offset back
        // into its size and make repeated selections jump around the board.
        TextPalette.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = TextPalette.DesiredSize.Width;
        var height = TextPalette.DesiredSize.Height;
        var center = new Point(item.X + item.Width / 2, item.Y + item.Height / 2);
        var corners = new[]
        {
            new Point(item.X, item.Y), new Point(item.X + item.Width, item.Y),
            new Point(item.X + item.Width, item.Y + item.Height), new Point(item.X, item.Y + item.Height)
        }.Select(point => BoardMath.RotatePoint(point, center, item.Rotation)).ToArray();
        var screenTop = corners.Min(point => point.Y) * _viewZoom + _viewPanY;
        var screenBottom = corners.Max(point => point.Y) * _viewZoom + _viewPanY;
        var left = center.X * _viewZoom + _viewPanX - width / 2;
        var top = screenTop - height - 12;
        if (top < 8) top = screenBottom + 12;
        left = Math.Clamp(left, 8, Math.Max(8, BoardSurface.ActualWidth - width - 8));
        top = Math.Clamp(top, 8, Math.Max(8, BoardSurface.ActualHeight - height - 8));
        TextPaletteTranslate.X = left;
        TextPaletteTranslate.Y = top;
        RefreshOpenToolPopups();
    }

    private void SyncTextToolbar(BoardTextItem item)
    {
        if (!_visuals.TryGetValue(item.Id, out var visual) || visual.Editor is null) return;
        var editor = visual.Editor;
        var range = _activeTextEditor == editor && !editor.Selection.IsEmpty
            ? editor.Selection
            : new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd);
        _syncingTextToolbar = true;
        var sizeValue = range.GetPropertyValue(TextElement.FontSizeProperty);
        var sizeDip = sizeValue is double size ? size : editor.Document.FontSize;
        var points = RichTextDocumentService.ToPoints(sizeDip);
        TextSizeCombo.SelectedItem = TextPointSizes.MinBy(candidate => Math.Abs(candidate - points));
        var familyValue = range.GetPropertyValue(TextElement.FontFamilyProperty);
        var familyName = familyValue is FontFamily family ? family.Source : editor.Document.FontFamily.Source;
        TextFontCombo.SelectedItem = TextFontCombo.Items.OfType<TextFontChoice>().FirstOrDefault(font =>
            font.Family.Source.Equals(familyName, StringComparison.OrdinalIgnoreCase)) ??
            TextFontCombo.Items.OfType<TextFontChoice>().FirstOrDefault();
        TextColorPreview.Background = PropertyBrush(range, TextElement.ForegroundProperty, editor.Document.Foreground);
        TextBackgroundPreview.Background = ParseBrush(item.BackgroundColor, Brushes.Transparent);
        TextBoldButton.Background = FormatButtonBrush(Equals(
            range.GetPropertyValue(TextElement.FontWeightProperty), FontWeights.Bold));
        TextItalicButton.Background = FormatButtonBrush(Equals(
            range.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic));
        TextUnderlineButton.Background = FormatButtonBrush(
            range.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection decorations &&
            decorations.Count > 0);
        _syncingTextToolbar = false;
    }

    private Brush FormatButtonBrush(bool active) => active
        ? (Brush)FindResource("AccentSubtleBrush")
        : Brushes.Transparent;

    private static Brush PropertyBrush(TextRange range, DependencyProperty property, Brush fallback) =>
        range.GetPropertyValue(property) is Brush brush ? brush : fallback;

    private bool TryGetTextTarget(out BoardTextItem item, out RichTextBox editor, out bool editing)
    {
        item = _activeTextItem ?? SelectedTextItem()!;
        editing = _activeTextItem is not null;
        if (item is null || !_visuals.TryGetValue(item.Id, out var visual) || visual.Editor is null)
        {
            editor = null!;
            return false;
        }
        editor = visual.Editor;
        return true;
    }

    private async Task ApplyTextFormattingAsync(Action<RichTextBox> action)
    {
        if (_syncingTextToolbar || !TryGetTextTarget(out var item, out var editor, out var editing)) return;
        var before = editing ? null : Snapshot();
        var oldReadOnly = editor.IsReadOnly;
        var oldFocusable = editor.Focusable;
        if (!editing)
        {
            editor.IsReadOnly = false;
            editor.Focusable = true;
            editor.SelectAll();
        }
        action(editor);
        item.DocumentData = RichTextDocumentService.Save(editor.Document);
        AutoFitTextItem(item, editor);
        if (!editing)
        {
            editor.IsReadOnly = oldReadOnly;
            editor.Focusable = oldFocusable;
            PushUndoSnapshot(before!);
            await _repository.UpdateTextItemsAsync(new[] { item });
            BoardSurface.Focus();
        }
        else editor.Focus();
        UpdateSelectionVisuals();
    }

    private void OnRichTextPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (_activeTextEditor is null) return;
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }
        var text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        e.CancelCommand();
        RichTextDocumentService.InsertPlainText(_activeTextEditor, text);
    }

    private async void OnTextBoldClick(object sender, RoutedEventArgs e) =>
        await ApplyTextFormattingAsync(editor => ToggleTextProperty(
            editor.Selection, TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal));
    private async void OnTextItalicClick(object sender, RoutedEventArgs e) =>
        await ApplyTextFormattingAsync(editor => ToggleTextProperty(
            editor.Selection, TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal));
    private async void OnTextUnderlineClick(object sender, RoutedEventArgs e) =>
        await ApplyTextFormattingAsync(editor =>
        {
            var current = editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
            editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty,
                current is TextDecorationCollection decorations && decorations.Count > 0
                    ? null : TextDecorations.Underline);
        });
    private void OnTextBulletsClick(object sender, RoutedEventArgs e) => ExecuteTextCommand(EditingCommands.ToggleBullets);
    private void OnTextNumberingClick(object sender, RoutedEventArgs e) => ExecuteTextCommand(EditingCommands.ToggleNumbering);

    private static void ToggleTextProperty(TextRange selection, DependencyProperty property, object enabled, object disabled)
    {
        var current = selection.GetPropertyValue(property);
        selection.ApplyPropertyValue(property, Equals(current, enabled) ? disabled : enabled);
    }

    private async void ExecuteTextCommand(RoutedCommand command)
    {
        await ApplyTextFormattingAsync(editor =>
        {
            editor.Focus();
            command.Execute(null, editor);
        });
    }

    private async void OnTextAlignClick(object sender, RoutedEventArgs e)
    {
        await ApplyTextFormattingAsync(editor =>
        {
            var value = editor.Selection.GetPropertyValue(Block.TextAlignmentProperty);
            var alignment = value is TextAlignment current ? current : TextAlignment.Left;
            editor.Selection.ApplyPropertyValue(Block.TextAlignmentProperty, alignment switch
            {
                TextAlignment.Left => TextAlignment.Center,
                TextAlignment.Center => TextAlignment.Right,
                TextAlignment.Right => TextAlignment.Justify,
                _ => TextAlignment.Left
            });
        });
    }

    private async void OnTextSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTextToolbar || !TryReadTextSize(out var points)) return;
        await ApplyTextFormattingAsync(editor => editor.Selection.ApplyPropertyValue(
            TextElement.FontSizeProperty, RichTextDocumentService.ToDip(points)));
    }

    private async void OnTextFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTextToolbar || string.IsNullOrWhiteSpace(TextFontCombo.Text)) return;
        if (TextFontCombo.SelectedItem is not TextFontChoice selected) return;
        var family = selected.Family;
        await ApplyTextFormattingAsync(editor => editor.Selection.ApplyPropertyValue(
            TextElement.FontFamilyProperty, family));
    }

    private bool TryReadTextSize(out double points)
    {
        var value = TextSizeCombo.SelectedItem?.ToString() ?? TextSizeCombo.Text;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out points) &&
               points is >= 4 and <= 500;
    }

    private void OnTextSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !TryReadTextSize(out var points)) return;
        e.Handled = true;
        _ = ApplyTextFormattingAsync(editor => editor.Selection.ApplyPropertyValue(
            TextElement.FontSizeProperty, RichTextDocumentService.ToDip(points)));
    }

    private void OnTextSizeLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_syncingTextToolbar || !TryReadTextSize(out var points)) return;
        _ = ApplyTextFormattingAsync(editor => editor.Selection.ApplyPropertyValue(
            TextElement.FontSizeProperty, RichTextDocumentService.ToDip(points)));
    }

    private void OnTextFontKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(TextFontCombo.Text)) return;
        e.Handled = true;
        var family = new FontFamily(TextFontCombo.Text);
        _ = ApplyTextFormattingAsync(editor => editor.Selection.ApplyPropertyValue(
            TextElement.FontFamilyProperty, family));
    }

    private void OnTextFontLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_syncingTextToolbar || string.IsNullOrWhiteSpace(TextFontCombo.Text)) return;
        var family = new FontFamily(TextFontCombo.Text);
        _ = ApplyTextFormattingAsync(editor => editor.Selection.ApplyPropertyValue(
            TextElement.FontFamilyProperty, family));
    }

    private async void OnTextColorClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTextTarget(out _, out var editor, out _)) return;
        var current = BrushString(PropertyBrush(editor.Selection, TextElement.ForegroundProperty, editor.Document.Foreground));
        await PickTextPropertyColorAsync(
            TextElement.ForegroundProperty, current, Brushes.White, TextColorPreview);
    }

    private async void OnTextHighlightClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTextTarget(out _, out var editor, out _)) return;
        var current = BrushString(PropertyBrush(editor.Selection, TextElement.BackgroundProperty, Brushes.Transparent));
        var color = PickColor(current == "#00FFFFFF" ? "#FFFFE082" : current);
        if (color is null) return;
        await ApplyTextFormattingAsync(target => target.Selection.ApplyPropertyValue(
            TextElement.BackgroundProperty, ParseBrush(color, Brushes.Transparent)));
    }

    private async void OnTextBackgroundClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTextTarget(out var item, out _, out var editing)) return;
        var before = editing ? null : Snapshot();
        var original = item.BackgroundColor;
        var color = PickColor(original, preview =>
        {
            item.BackgroundColor = preview;
            if (_visuals.TryGetValue(item.Id, out var visual))
                visual.Border.Background = ParseBrush(preview, Brushes.Transparent);
            TextBackgroundPreview.Background = ParseBrush(preview, Brushes.Transparent);
        });
        if (color is null)
        {
            item.BackgroundColor = original;
            if (_visuals.TryGetValue(item.Id, out var visual))
                visual.Border.Background = ParseBrush(original, Brushes.Transparent);
            TextBackgroundPreview.Background = ParseBrush(original, Brushes.Transparent);
            return;
        }
        item.BackgroundColor = color;
        if (!editing && !string.Equals(original, color, StringComparison.OrdinalIgnoreCase))
        {
            PushUndoSnapshot(before!);
            await _repository.UpdateTextItemsAsync(new[] { item });
        }
    }

    private async Task PickTextPropertyColorAsync(
        DependencyProperty property,
        string current,
        Brush fallback,
        Border previewBar)
    {
        if (!TryGetTextTarget(out var item, out var editor, out var editing)) return;
        var before = editing ? null : Snapshot();
        var originalDocument = RichTextDocumentService.Save(editor.Document);
        var oldReadOnly = editor.IsReadOnly;
        var oldFocusable = editor.Focusable;
        if (!editing)
        {
            editor.IsReadOnly = false;
            editor.Focusable = true;
            editor.SelectAll();
        }
        var lastColor = current;
        void Preview(string color)
        {
            if (string.Equals(lastColor, color, StringComparison.OrdinalIgnoreCase)) return;
            lastColor = color;
            var brush = ParseBrush(color, fallback);
            editor.Selection.ApplyPropertyValue(property, brush);
            item.DocumentData = RichTextDocumentService.Save(editor.Document);
            previewBar.Background = brush;
            AutoFitTextItem(item, editor);
        }
        var color = PickColor(current, Preview);
        if (color is null)
        {
            editor.Document = RichTextDocumentService.Load(originalDocument);
            item.DocumentData = originalDocument;
            editor.IsReadOnly = oldReadOnly;
            editor.Focusable = oldFocusable;
            AutoFitTextItem(item, editor);
            UpdateSelectionVisuals();
            if (!editing) BoardSurface.Focus();
            return;
        }
        if (!editing)
        {
            editor.IsReadOnly = oldReadOnly;
            editor.Focusable = oldFocusable;
            if (!string.Equals(originalDocument, item.DocumentData, StringComparison.Ordinal))
            {
                PushUndoSnapshot(before!);
                await _repository.UpdateTextItemsAsync(new[] { item });
            }
            BoardSurface.Focus();
        }
        UpdateSelectionVisuals();
    }

    private static string BrushString(Brush brush) =>
        brush is SolidColorBrush solid
            ? solid.Color.A == byte.MaxValue
                ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}"
                : solid.Color.ToString()
            : "#00FFFFFF";

    private void OnTextMoreClick(object sender, RoutedEventArgs e)
    {
        PasteTextStyleButton.IsEnabled = _copiedTextStyle is not null;
        ToggleToolPopup(TextMorePopup);
    }

    private async void OnClearTextFormattingClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTextTarget(out var item, out _, out _)) return;
        TextMorePopup.IsOpen = false;
        await ApplyTextFormattingAsync(editor =>
        {
            editor.SelectAll();
            editor.Selection.ClearAllProperties();
            editor.Document.FontFamily = new FontFamily("Microsoft YaHei UI");
            editor.Document.FontSize = RichTextDocumentService.ToDip(16);
            editor.Document.FontWeight = FontWeights.Normal;
            editor.Document.FontStyle = FontStyles.Normal;
            editor.Document.Background = Brushes.Transparent;
            editor.Selection.ApplyPropertyValue(Block.TextAlignmentProperty, TextAlignment.Left);
            editor.Document.Foreground = Brushes.White;
            item.BackgroundColor = "#00FFFFFF";
            if (_visuals.TryGetValue(item.Id, out var visual))
                visual.Border.Background = Brushes.Transparent;
        });
    }

    private void OnTextLinkClick(object sender, RoutedEventArgs e)
    {
        if (_activeTextEditor is null) return;
        var address = PromptForLink();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            BoardStatus.Text = "链接只支持 http 或 https 地址";
            return;
        }
        var selection = _activeTextEditor.Selection;
        if (selection.IsEmpty)
        {
            var run = new Run(address, selection.Start);
            var link = new Hyperlink(run) { NavigateUri = uri };
            AttachHyperlink(link);
            _activeTextEditor.CaretPosition = link.ElementEnd;
        }
        else
        {
            try
            {
                var link = new Hyperlink(selection.Start, selection.End) { NavigateUri = uri };
                AttachHyperlink(link);
            }
            catch (InvalidOperationException)
            {
                BoardStatus.Text = "请选择同一段落中的文字再添加链接";
            }
        }
        _activeTextEditor.Focus();
    }

    private void AttachHyperlink(Hyperlink link)
    {
        link.RequestNavigate += (_, args) =>
        {
            if (args.Uri.Scheme is not ("http" or "https")) return;
            Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
            args.Handled = true;
        };
    }

    private bool TryOpenHyperlink(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Hyperlink { NavigateUri: { } uri } &&
                uri.Scheme is "http" or "https")
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return true;
            }
            current = GetTreeParent(current);
        }
        return false;
    }

    private string? PromptForLink()
    {
        var input = new TextBox { Text = "https://", MinWidth = 300, Margin = new Thickness(0, 10, 0, 12) };
        var dialog = new Window
        {
            Title = "添加超链接", Width = 360, Height = 150,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        var ok = new Button { Content = "确定", Width = 72, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 72, IsCancel = true };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = "链接地址" },
                input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok }
                }
            }
        };
        input.SelectAll();
        input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private string? PickColor(string current, Action<string>? livePreview = null)
    {
        var picker = new CustomColorPickerWindow(current, "选择颜色") { Owner = this };
        if (livePreview is not null)
            picker.ColorChanged += (_, color) => livePreview(color);
        return picker.ShowDialog() == true ? picker.SelectedColorHex : null;
    }

    private void OnDrawingToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value } ||
            !Enum.TryParse<BoardToolMode>(value, out var mode)) return;
        SetToolMode(mode);
    }

    private async void OnDrawingStrokeColorClick(object sender, RoutedEventArgs e)
    {
        await PickDrawingColorAsync(fill: false);
    }

    private async void OnDrawingFillColorClick(object sender, RoutedEventArgs e)
    {
        await PickDrawingColorAsync(fill: true);
    }

    private Task PickDrawingColorAsync(bool fill)
    {
        CloseDrawingPopups();
        var original = fill ? _drawingFillColor : _drawingStrokeColor;
        PickColor(original, color =>
        {
            if (fill) _drawingFillColor = color;
            else _drawingStrokeColor = color;
            UpdateDrawingToolbarState();
        });
        return Task.CompletedTask;
    }

    private void OnDrawingDashClick(object sender, RoutedEventArgs e)
    {
        _drawingDashed = sender is FrameworkElement { Tag: "Dashed" };
        _drawingArrow = sender is FrameworkElement { Tag: "Arrow" };
        if (_drawingArrow && _toolMode is not (BoardToolMode.Pen or BoardToolMode.Line or BoardToolMode.Arrow))
            SetToolMode(BoardToolMode.Pen);
        UpdateDrawingToolbarState();
    }

    private void OnDrawingStyleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingDrawingToolbar || DrawingOpacityText is null) return;
        UpdateDrawingToolbarState();
    }

    private void StartDrawing(Point world, double pressure = 1)
    {
        if (_previewDrawing is not null || _erasing) return;
        _selected.Clear();
        UpdateSelectionVisuals();
        _drawingPoints.Clear();
        _drawingStartWorld = world;
        _gestureSnapshot = Snapshot();
        _erasing = _toolMode == BoardToolMode.Eraser;
        _drawingPoints.Add(new BoardStrokePoint(world.X, world.Y, pressure));
        if (_erasing)
        {
            _eraserGestureRadius = EraserDiameterSlider.Value / (2 * _viewZoom);
            BeginEraserPreview();
            Mouse.Capture(BoardSurface);
            return;
        }
        _previewDrawing = new BoardDrawingItem
        {
            DrawerId = _drawerId,
            Kind = _drawingArrow && _toolMode == BoardToolMode.Pen ? BoardDrawingKind.CurveArrow
                : _drawingArrow && _toolMode == BoardToolMode.Line ? BoardDrawingKind.Arrow : ToolToDrawingKind(_toolMode),
            StrokeColor = _drawingStrokeColor,
            FillColor = _drawingFillColor,
            StrokeThickness = DrawingThicknessSlider.Value,
            StrokeOpacity = DrawingOpacitySlider.Value,
            Dashed = _drawingDashed,
            ZIndex = NextZIndex()
        };
        _previewDrawing.LayerName = BoardLayerNameService.DefaultName(_previewDrawing);
        UpdatePreviewDrawing();
        AddDrawingVisual(_previewDrawing);
        Mouse.Capture(BoardSurface);
        BeginContinuousInteraction();
    }

    private void UpdateDrawing(Point world, double pressure = 1)
    {
        if (_previewDrawing is null && !_erasing) return;
        world = ConstrainDrawingPoint(_toolMode, _drawingStartWorld, world,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (_toolMode is BoardToolMode.Pen or BoardToolMode.Eraser)
        {
            var previous = _drawingPoints[^1];
            var dx = world.X - previous.X;
            var dy = world.Y - previous.Y;
            if (Math.Sqrt(dx * dx + dy * dy) * _viewZoom < 1.5) return;
            _drawingPoints.Add(new BoardStrokePoint(world.X, world.Y, pressure));
        }
        else
        {
            if (_drawingPoints.Count == 1) _drawingPoints.Add(new BoardStrokePoint(world.X, world.Y, pressure));
            else _drawingPoints[^1] = new BoardStrokePoint(world.X, world.Y, pressure);
        }
        if (_previewDrawing is not null) RequestDrawingPreviewUpdate();
        else if (_erasing) RequestEraserPreviewUpdate();
    }

    private static Point ConstrainDrawingPoint(BoardToolMode mode, Point start, Point current, bool constrain)
    {
        if (!constrain) return current;
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (mode is BoardToolMode.Line or BoardToolMode.Arrow)
        {
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < .0001) return current;
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            return new Point(start.X + Math.Cos(angle) * length, start.Y + Math.Sin(angle) * length);
        }
        if (mode is BoardToolMode.Rectangle or BoardToolMode.Ellipse)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new Point(start.X + Math.CopySign(side, dx == 0 ? 1 : dx),
                start.Y + Math.CopySign(side, dy == 0 ? 1 : dy));
        }
        return current;
    }

    private void UpdatePreviewDrawing()
    {
        if (_previewDrawing is null) return;
        SetDrawingGeometry(_previewDrawing, _drawingPoints);
        UpdateItemVisual(_previewDrawing);
    }

    private void RequestDrawingPreviewUpdate()
    {
        if (_drawingRenderPending) return;
        _drawingRenderPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            _drawingRenderPending = false;
            if (_previewDrawing is not null) UpdatePreviewDrawing();
        }));
    }

    private Task CompleteDrawingAsync()
    {
        Mouse.Capture(null);
        EndContinuousInteraction();
        var before = _gestureSnapshot ?? Snapshot();
        _gestureSnapshot = null;
        if (_erasing)
        {
            return ReportDrawingSaveAsync(CompleteEraserPreviewAsync(before));
        }
        if (_previewDrawing is null) return Task.CompletedTask;
        var item = _previewDrawing;
        _previewDrawing = null;
        _drawingRenderPending = false;
        if (_visuals.Remove(item.Id, out var preview)) WorldCanvas.Children.Remove(preview.Border);
        if (item.Kind is not (BoardDrawingKind.Pen or BoardDrawingKind.Highlighter or BoardDrawingKind.CurveArrow) &&
            (_drawingPoints.Count < 2 || Distance(_drawingPoints[0], _drawingPoints[^1]) * _viewZoom < 2))
        {
            _drawingPoints.Clear();
            return Task.CompletedTask;
        }
        if (item.Kind is BoardDrawingKind.Pen or BoardDrawingKind.Highlighter or BoardDrawingKind.CurveArrow)
        {
            var simplified = Simplify(_drawingPoints, Math.Max(.35, 1.1 / _viewZoom));
            _drawingPoints.Clear();
            _drawingPoints.AddRange(simplified);
        }
        SetDrawingGeometry(item, _drawingPoints);
        _drawingPoints.Clear();
        return ReportDrawingSaveAsync(CommitStrokeToSessionAsync(item, before));
    }

    private async Task ReportDrawingSaveAsync(Task save)
    {
        try { await save; }
        catch (Exception error) { BoardStatus.Text = $"笔画保存失败：{error.Message}"; }
    }

    private static bool PathHitsPath(
        IReadOnlyList<BoardStrokePoint> target, IReadOnlyList<BoardStrokePoint> eraser, double radius)
    {
        if (target.Count == 1) return PointHitsPath(target[0], eraser, radius);
        for (var index = 1; index < target.Count; index++)
            if (SegmentHitsPath(target[index - 1], target[index], eraser, radius)) return true;
        return false;
    }

    private static bool SegmentHitsPath(
        BoardStrokePoint a, BoardStrokePoint b,
        IReadOnlyList<BoardStrokePoint> path, double radius)
    {
        if (path.Count == 0) return false;
        if (path.Any(point => DistanceToSegment(point, a, b) <= radius)) return true;
        for (var index = 1; index < path.Count; index++)
            if (SegmentsCross(a, b, path[index - 1], path[index]) ||
                DistanceToSegment(a, path[index - 1], path[index]) <= radius ||
                DistanceToSegment(b, path[index - 1], path[index]) <= radius)
                return true;
        return false;
    }

    private static bool SegmentsCross(BoardStrokePoint a, BoardStrokePoint b, BoardStrokePoint c, BoardStrokePoint d)
    {
        static double Cross(double x1, double y1, double x2, double y2) => x1 * y2 - y1 * x2;
        var denominator = Cross(b.X - a.X, b.Y - a.Y, d.X - c.X, d.Y - c.Y);
        if (Math.Abs(denominator) < .000001) return false;
        var t = Cross(c.X - a.X, c.Y - a.Y, d.X - c.X, d.Y - c.Y) / denominator;
        var u = Cross(c.X - a.X, c.Y - a.Y, b.X - a.X, b.Y - a.Y) / denominator;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    private static bool PointHitsPath(
        BoardStrokePoint point, IReadOnlyList<BoardStrokePoint> path, double radius)
    {
        if (path.Count == 0) return false;
        if (path.Count == 1) return Distance(point, path[0]) <= radius;
        for (var index = 1; index < path.Count; index++)
            if (DistanceToSegment(point, path[index - 1], path[index]) <= radius) return true;
        return false;
    }

    private static double DistanceToSegment(
        BoardStrokePoint point, BoardStrokePoint start, BoardStrokePoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) < .0001 && Math.Abs(dy) < .0001) return Distance(point, start);
        var t = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) /
                           (dx * dx + dy * dy), 0, 1);
        return Distance(point, new BoardStrokePoint(start.X + t * dx, start.Y + t * dy));
    }

    private static double Distance(BoardStrokePoint a, BoardStrokePoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void SetDrawingGeometry(BoardDrawingItem item, IReadOnlyList<BoardStrokePoint> worldPoints)
    {
        if (worldPoints.Count == 0) return;
        var padding = item.Kind is BoardDrawingKind.Arrow or BoardDrawingKind.CurveArrow
            ? ArrowGeometry.Padding(item.StrokeThickness) : Math.Max(2, item.StrokeThickness * 1.8);
        var minX = worldPoints.Min(x => x.X) - padding;
        var minY = worldPoints.Min(x => x.Y) - padding;
        var maxX = worldPoints.Max(x => x.X) + padding;
        var maxY = worldPoints.Max(x => x.Y) + padding;
        item.X = minX;
        item.Y = minY;
        item.Width = Math.Max(4, maxX - minX);
        item.Height = Math.Max(4, maxY - minY);
        var normalized = worldPoints.Select(point => new BoardStrokePoint(
            (point.X - minX) / item.Width,
            (point.Y - minY) / item.Height,
            point.Pressure)).ToArray();
        item.PointsJson = JsonSerializer.Serialize(normalized);
    }

    private static IReadOnlyList<BoardStrokePoint> Simplify(
        IReadOnlyList<BoardStrokePoint> points, double tolerance)
    {
        if (points.Count < 3) return points.ToArray();
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        SimplifyRange(0, points.Count - 1);
        return points.Where((_, index) => keep[index]).ToArray();

        void SimplifyRange(int first, int last)
        {
            if (last <= first + 1) return;
            var maxDistance = 0d;
            var maxIndex = 0;
            for (var index = first + 1; index < last; index++)
            {
                var distance = PerpendicularDistance(points[index], points[first], points[last]);
                if (distance <= maxDistance) continue;
                maxDistance = distance;
                maxIndex = index;
            }
            if (maxDistance <= tolerance) return;
            keep[maxIndex] = true;
            SimplifyRange(first, maxIndex);
            SimplifyRange(maxIndex, last);
        }
    }

    private static double PerpendicularDistance(
        BoardStrokePoint point, BoardStrokePoint lineStart, BoardStrokePoint lineEnd)
    {
        var dx = lineEnd.X - lineStart.X;
        var dy = lineEnd.Y - lineStart.Y;
        if (Math.Abs(dx) < .0001 && Math.Abs(dy) < .0001) return Distance(point, lineStart);
        var t = Math.Clamp(((point.X - lineStart.X) * dx + (point.Y - lineStart.Y) * dy) /
                           (dx * dx + dy * dy), 0, 1);
        return Distance(point, new BoardStrokePoint(lineStart.X + t * dx, lineStart.Y + t * dy));
    }

    private static BoardDrawingKind ToolToDrawingKind(BoardToolMode mode) => mode switch
    {
        BoardToolMode.Line => BoardDrawingKind.Line,
        BoardToolMode.Arrow => BoardDrawingKind.Arrow,
        BoardToolMode.Rectangle => BoardDrawingKind.Rectangle,
        BoardToolMode.Ellipse => BoardDrawingKind.Ellipse,
        _ => BoardDrawingKind.Pen
    };

    private int NextZIndex() => AllElements.Select(x => x.ZIndex).DefaultIfEmpty(-1).Max() + 1;

    private async Task PersistElementsAsync(IEnumerable<BoardElement> elements)
    {
        await _drawingSaveTask;
        var list = elements.DistinctBy(x => x.Id).ToArray();
        await _repository.UpdateItemsAsync(list.OfType<BoardItem>().ToArray());
        await _repository.UpdateTextItemsAsync(list.OfType<BoardTextItem>().ToArray());
        await _repository.UpdateDrawingItemsAsync(list.OfType<BoardDrawingItem>().ToArray());
    }

    private void OnSurfaceStylusDown(object sender, StylusDownEventArgs e)
    {
        if (!IsDrawingTool(_toolMode) || _spaceDown ||
            IsToolPaletteSource(e.OriginalSource as DependencyObject)) return;
        var points = e.GetStylusPoints(BoardSurface);
        if (points.Count == 0) return;
        var point = points[^1];
        UpdateEraserCursor(point.ToPoint(), true);
        StartDrawing(ScreenToWorld(point.ToPoint()), point.PressureFactor);
        e.Handled = true;
    }

    private void OnSurfaceStylusMove(object sender, StylusEventArgs e)
    {
        UpdateEraserCursor(e.GetPosition(BoardSurface), !IsToolPaletteSource(e.OriginalSource as DependencyObject));
        if (_previewDrawing is null && !_erasing) return;
        var points = e.GetStylusPoints(BoardSurface);
        foreach (var point in points)
            UpdateDrawing(ScreenToWorld(point.ToPoint()), point.PressureFactor);
        e.Handled = true;
    }

    private async void OnSurfaceStylusUp(object sender, StylusEventArgs e)
    {
        if (_previewDrawing is null && !_erasing) return;
        await CompleteDrawingAsync();
        e.Handled = true;
    }
}
