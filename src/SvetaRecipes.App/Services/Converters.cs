using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SvetaRecipes.App.Services;

/// <summary>
/// Number ⇄ text for editable fields. Empty text means "no value" for nullable targets; both "." and "," are accepted
/// as the decimal separator. ConverterParameter is the display format (default "0.###").
/// </summary>
public sealed class NumberConverter : IValueConverter
{
    public static readonly NumberConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => "",
        double d => d.ToString(parameter as string ?? "0.###", culture),
        decimal m => m.ToString(parameter as string ?? "0.00", culture),
        int i => i.ToString(culture),
        _ => value.ToString(),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim().Replace("$", "") ?? "";
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (text.Length == 0)
            return underlying is not null || !targetType.IsValueType ? null : Activator.CreateInstance(targetType);
        var type = underlying ?? targetType;
        if (!double.TryParse(text, NumberStyles.Float, culture, out var d)
            && !double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            return new BindingNotification(new FormatException($"'{text}' is not a number"), BindingErrorType.DataValidationError);
        if (type == typeof(decimal)) return (decimal)d;
        if (type == typeof(int)) return (int)Math.Round(d);
        return d;
    }
}

public static class Converters
{
    public static readonly IValueConverter HexBrush =
        new FuncValueConverter<string?, IBrush>(hex => Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent);

    public static readonly IValueConverter ColorBrush =
        new FuncValueConverter<Color, IBrush>(c => new SolidColorBrush(c));

    public static readonly IValueConverter BoldIf =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    public static readonly IValueConverter Money =
        new FuncValueConverter<decimal?, string>(m => m is { } v ? v.ToString("C2", CultureInfo.CurrentCulture) : "—");

    public static readonly IValueConverter NotEmpty =
        new FuncValueConverter<string?, bool>(s => !string.IsNullOrWhiteSpace(s));
}
