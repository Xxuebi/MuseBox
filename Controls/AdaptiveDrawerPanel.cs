using System.Windows;
using System.Windows.Controls;

namespace ScreenshotCollector.Controls;

// Equal-width cards fill each row; a finite viewport width drives wrapping.
public sealed class AdaptiveDrawerPanel : Panel
{
    public static readonly DependencyProperty CollectionProgressProperty = DependencyProperty.Register(
        nameof(CollectionProgress), typeof(double), typeof(AdaptiveDrawerPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));
    public double CollectionProgress { get => (double)GetValue(CollectionProgressProperty); set => SetValue(CollectionProgressProperty, value); }
    public static readonly DependencyProperty RevealProgressProperty = DependencyProperty.RegisterAttached(
        "RevealProgress", typeof(double), typeof(AdaptiveDrawerPanel),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static void SetRevealProgress(DependencyObject element, double value) => element.SetValue(RevealProgressProperty, value);
    public static double GetRevealProgress(DependencyObject element) => Math.Clamp((double)element.GetValue(RevealProgressProperty), 0, 1);
    private const double MinimumCardWidth = 142;
    private const double Gap = 10;

    internal static double ExtentHeight(double width, int count, double collectionProgress)
    {
        if (count <= 0) return 0;
        var layout = LayoutFor(width, collectionProgress);
        var rows = (count + layout.Columns - 1) / layout.Columns;
        return rows * layout.Height + (rows - 1) * Gap;
    }

    private static (int Columns, double Width, double Height) LayoutFor(double width, double collectionProgress)
    {
        width = double.IsFinite(width) ? Math.Max(1, width) : MinimumCardWidth;
        var columns = Math.Max(1, (int)Math.Floor((width + Gap) / (MinimumCardWidth + Gap)));
        var cardWidth = Math.Max(1, (width - Gap * (columns - 1)) / columns);
        // Preview content is card width minus 26; vertical chrome consumes 66.
        var normalHeight = Math.Round((cardWidth - 26) * .72 + 66);
        // 36px footer + 14px border/padding: the footer does not jump when the preview collapses.
        return (columns, cardWidth, normalHeight + (50 - normalHeight) * Math.Clamp(collectionProgress, 0, 1));
    }

    private (int Columns, double Width, double Height) Layout(double width) => LayoutFor(width, CollectionProgress);

    private double[] RowProgress(int columns)
    {
        var rows = new double[(InternalChildren.Count + columns - 1) / columns];
        for (var i = 0; i < InternalChildren.Count; i++)
            rows[i / columns] = Math.Max(rows[i / columns], GetRevealProgress(InternalChildren[i]));
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = Layout(availableSize.Width);
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(layout.Width, layout.Height));
        var rows = RowProgress(layout.Columns);
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : layout.Width,
            rows.Select((progress, index) => progress * (layout.Height + (index == 0 ? 0 : Gap))).Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = Layout(finalSize.Width);
        var rows = RowProgress(layout.Columns);
        var y = 0d;
        for (var row = 0; row < rows.Length; row++)
        {
            if (row > 0) y += Gap * rows[row];
            for (var col = 0; col < layout.Columns; col++)
            {
                var index = row * layout.Columns + col;
                if (index >= InternalChildren.Count) break;
                var child = InternalChildren[index];
                child.Arrange(new Rect(col * (layout.Width + Gap), y, layout.Width, layout.Height * GetRevealProgress(child)));
            }
            y += rows[row] * layout.Height;
        }
        return finalSize;
    }
}
