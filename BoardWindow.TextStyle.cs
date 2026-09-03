using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ScreenshotCollector;

public partial class BoardWindow
{
    private sealed record TextStyleSnapshot(Dictionary<DependencyProperty, object> Properties, string Background);
    // In-app style clipboard, shared across boards without replacing copied text/images.
    private static TextStyleSnapshot? _copiedTextStyle;
    private static readonly DependencyProperty[] StyleProperties =
    [
        TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.FontWeightProperty,
        TextElement.FontStyleProperty, TextElement.FontStretchProperty, TextElement.ForegroundProperty,
        TextElement.BackgroundProperty, Inline.TextDecorationsProperty, Block.TextAlignmentProperty
    ];

    private void OnCopyTextStyleClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTextTarget(out var item, out var editor, out var editing)) return;
        var start = editing ? editor.Selection.Start : editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var sample = new TextRange(start, start.GetNextInsertionPosition(LogicalDirection.Forward) ?? start);
        var properties = new Dictionary<DependencyProperty, object>();
        foreach (var property in StyleProperties)
        {
            var value = editing ? editor.Selection.GetPropertyValue(property) : DependencyProperty.UnsetValue;
            if (value == DependencyProperty.UnsetValue) value = sample.GetPropertyValue(property);
            if (value == DependencyProperty.UnsetValue) continue;
            properties[property] = value is Freezable freezable ? freezable.CloneCurrentValue() : value;
        }
        _copiedTextStyle = new TextStyleSnapshot(properties, item.BackgroundColor);
        PasteTextStyleButton.IsEnabled = true;
        TextMorePopup.IsOpen = false;
        BoardStatus.Text = "已复制文字样式";
    }

    private async void OnPasteTextStyleClick(object sender, RoutedEventArgs e) => await PasteTextStyleAsync();

    private async Task PasteTextStyleAsync()
    {
        var style = _copiedTextStyle;
        if (style is null || !TryGetTextTarget(out var item, out _, out _)) return;
        TextMorePopup.IsOpen = false;
        await ApplyTextFormattingAsync(editor =>
        {
            editor.BeginChange();
            try
            {
                foreach (var (property, value) in style.Properties)
                    editor.Selection.ApplyPropertyValue(property, value is Freezable freezable ? freezable.CloneCurrentValue() : value);
                item.BackgroundColor = style.Background;
                if (_visuals.TryGetValue(item.Id, out var visual))
                    visual.Border.Background = ParseBrush(style.Background, Brushes.Transparent);
            }
            finally { editor.EndChange(); }
        });
        BoardStatus.Text = "已粘贴文字样式";
    }
}
