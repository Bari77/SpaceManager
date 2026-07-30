using System.Globalization;
using System.Windows.Data;

namespace SpaceManager.Converters;

public sealed class RatioWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return 0d;

        var width = values[0] is double w ? w : 0d;
        var ratio = values[1] is double r ? r : 0d;
        return Math.Max(0, width * ratio);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
