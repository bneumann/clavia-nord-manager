using System.Globalization;
using Avalonia.Data.Converters;

namespace NordSampleManager.ViewModels;

/// <summary>Converts a string value to bool by comparing it to ConverterParameter.</summary>
public sealed class StringEqualityConverter : IValueConverter
{
    public static readonly StringEqualityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s == parameter as string;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : null;
}
