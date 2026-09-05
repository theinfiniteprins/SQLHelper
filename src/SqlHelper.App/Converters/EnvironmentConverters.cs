using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SqlHelper.App.Converters;

/// <summary>Background pill colour for the environment badge: warm for Production, calm otherwise.</summary>
public sealed class ProdToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
            ? (Brush)Application.Current.Resources["WarnSoftBrush"]
            : (Brush)Application.Current.Resources["AccentSoftBrush"];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ProdToTextBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
            ? (Brush)Application.Current.Resources["WarnBrush"]
            : (Brush)Application.Current.Resources["AccentBrush"];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
