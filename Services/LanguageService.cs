using System.Globalization;

namespace ScreenshotCollector.Services;

public static class LanguageService
{
    public const string SimplifiedChinese = "zh-CN";

    public static string Normalize(string? languageCode) =>
        string.Equals(languageCode, SimplifiedChinese, StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChinese
            : SimplifiedChinese;

    public static void Apply(string? languageCode)
    {
        var culture = CultureInfo.GetCultureInfo(Normalize(languageCode));
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
