using System.Globalization;
using System.Windows.Data;

namespace ScreenshotCollector.Services;

public sealed class SwitchValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var enabled = value is true;
        return enabled && double.TryParse(
            parameter?.ToString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var active)
            ? active
            : 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
