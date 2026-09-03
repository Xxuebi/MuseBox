using System.Windows.Media;
using System.Xml.Linq;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class SceneFontService
{
    public static IReadOnlyList<string> MissingFonts(SceneDocument document)
    {
        var installed = Fonts.SystemFontFamilies.SelectMany(f => f.FamilyNames.Values.Append(f.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in document.Texts.Where(t => t.DocumentData.Length > 0))
        {
            SceneValidation.ValidateRichText(text.DocumentData);
            using var stream = new MemoryStream(Convert.FromBase64String(text.DocumentData));
            foreach (var name in XDocument.Load(stream).Descendants().Attributes("FontFamily").Select(a => a.Value))
                if (!name.Split(',').Any(f => installed.Contains(f.Trim()))) missing.Add(name);
        }
        return missing.OrderBy(n => n).ToArray();
    }
}
