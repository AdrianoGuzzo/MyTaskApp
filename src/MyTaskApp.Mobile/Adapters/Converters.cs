using System.Globalization;

namespace MyTaskApp.Mobile.Adapters;

/// <summary>A cor de uma etiqueta (<c>#RRGGBB</c>), como o desktop desenha.</summary>
public sealed class HexColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex, out var color) ? color : Colors.Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Concluída aparece riscada.</summary>
public sealed class StrikeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TextDecorations.Strikethrough : TextDecorations.None;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InvertedBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Mostra quando há valor: texto não vazio, lista não vazia, objeto não nulo.</summary>
public sealed class HasValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => false,
        string text => !string.IsNullOrWhiteSpace(text),
        System.Collections.ICollection collection => collection.Count > 0,
        _ => true,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
