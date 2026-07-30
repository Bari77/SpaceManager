using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SpaceManager.Converters;

public sealed class IndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var depth = value is int d ? d : 0;
        return new Thickness(depth * 18, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
