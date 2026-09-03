using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ScreenshotCollector.Services;

public static class RichTextDocumentService
{
    public const double PointsToDip = 96d / 72d;

    public static double ToDip(double points) => points * PointsToDip;

    public static double ToPoints(double dip) => dip / PointsToDip;

    public static FlowDocument CreateDefault()
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(6),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = ToDip(16),
            Foreground = Brushes.White
        };
        document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        return document;
    }

    public static string Save(FlowDocument document)
    {
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.Xaml);
        return Convert.ToBase64String(stream.ToArray());
    }

    public static FlowDocument Load(string? data)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(6),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = ToDip(16),
            Foreground = Brushes.White
        };
        if (string.IsNullOrWhiteSpace(data))
        {
            document.Blocks.Add(new Paragraph());
            return document;
        }
        try
        {
            var bytes = Convert.FromBase64String(data);
            using var stream = new MemoryStream(bytes, writable: false);
            new TextRange(document.ContentStart, document.ContentEnd).Load(stream, DataFormats.Xaml);
            return document;
        }
        catch
        {
            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph(new Run("注释内容无法读取")));
            return document;
        }
    }

    public static string PlainText(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd('\r', '\n');

    public static void InsertPlainText(RichTextBox editor, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        editor.Selection.Text = text;
        editor.CaretPosition = editor.Selection.End;
    }
}
