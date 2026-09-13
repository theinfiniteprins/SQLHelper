using System.Globalization;
using System.Windows.Data;

namespace SqlHelper.App.Converters;

/// <summary>True when every bound value is equal to the first — e.g. "is this line part of the current change".</summary>
public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2)
        {
            return false;
        }

        object first = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (!Equals(first, values[i]))
            {
                return false;
            }
        }

        return true;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
