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

/// <summary>O fundo de uma etiqueta: a cor dela bem clara, para o nome continuar lendo por cima.</summary>
public sealed class SoftHexColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is string hex && Color.TryParse(hex, out var color) ? color : Colors.Gray).WithAlpha(0.15f);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Uma cor do tema escolhida por um valor do domínio, lida na hora da
/// conversão. As linhas da lista não usam gatilho para cor: no Android, o
/// gatilho que sai numa linha reciclada devolvia a cor do tema de antes de
/// uma troca. Com conversor, a troca de tema relê a lista e cada linha pega
/// a cor nova.
/// </summary>
public abstract class ThemeColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = Key(value);
        var color = key is not null
            && Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var found) == true
            && found is Color themed
                ? themed
                : Colors.Transparent;

        return targetType == typeof(Brush) ? new SolidColorBrush(color) : color;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>O nome da cor na paleta (<c>Danger</c>, <c>Caution</c>…); <c>null</c> = transparente.</summary>
    protected abstract string? Key(object? value);
}

/// <summary>A faixa da prioridade: urgente em perigo, alta em atenção, o resto sem faixa.</summary>
public sealed class PriorityColorConverter : ThemeColorConverter
{
    protected override string? Key(object? value) => value switch
    {
        Domain.Tasks.TaskPriority.Urgent => "Danger",
        Domain.Tasks.TaskPriority.High => "Caution",
        _ => null,
    };
}

/// <summary>O prazo: perto em atenção, urgente e vencido em perigo, longe no texto de apoio.</summary>
public sealed class DeadlineColorConverter : ThemeColorConverter
{
    protected override string? Key(object? value) => value switch
    {
        Domain.Deadlines.DeadlineSeverity.Attention => "Caution",
        Domain.Deadlines.DeadlineSeverity.Urgent or Domain.Deadlines.DeadlineSeverity.Overdue => "Danger",
        _ => "TextMid",
    };
}

/// <summary>
/// O miolo da bolinha da sincronização (ADR-063): sincronizado e para revisar
/// em sucesso, pendente em atenção, sincronizando em informação, atenção em
/// perigo. Sem conexão, pausada e só neste celular são só o anel.
/// </summary>
public sealed class SyncDotFillConverter : ThemeColorConverter
{
    protected override string? Key(object? value) => value switch
    {
        Application.Sync.SyncIndicatorKind.Pending => "Caution",
        Application.Sync.SyncIndicatorKind.Syncing => "Info",
        Application.Sync.SyncIndicatorKind.Attention => "Danger",
        Application.Sync.SyncIndicatorKind.Synced or Application.Sync.SyncIndicatorKind.Review => "Success",
        _ => null,
    };
}

/// <summary>O anel da bolinha: atenção sem conexão e para revisar, apagado quando parada de propósito.</summary>
public sealed class SyncDotRingConverter : ThemeColorConverter
{
    protected override string? Key(object? value) => value switch
    {
        Application.Sync.SyncIndicatorKind.Offline or Application.Sync.SyncIndicatorKind.Review => "Caution",
        Application.Sync.SyncIndicatorKind.Paused or Application.Sync.SyncIndicatorKind.LocalOnly => "TextLow",
        _ => null,
    };
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
