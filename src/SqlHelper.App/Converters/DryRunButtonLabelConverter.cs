using System.Globalization;
using System.Windows.Data;

namespace SqlHelper.App.Converters;

public sealed class DryRunButtonLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "▶ Dry run" : "▶ Run for real";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
