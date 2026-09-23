using Avalonia.Media;
using Avalonia.Media.Immutable;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Uma etiqueta pronta para desenhar: bolinha, pílula ou item do seletor. O
/// pincel sai daqui, e não de um conversor no XAML, para a cor e o contraste do
/// texto serem decididos num lugar só.
/// </summary>
public sealed class TagChipViewModel
{
    // Imutáveis de propósito: não são AvaloniaObject, então nascem em qualquer
    // thread — inclusive nos testes de ViewModel, que não sobem a UI.
    private static readonly IBrush DarkText = new ImmutableSolidColorBrush(Color.Parse("#111318"));

    private static readonly IBrush LightText = new ImmutableSolidColorBrush(Colors.White);

    public TagChipViewModel(Guid id, string name, string colorHex)
    {
        Id = id;
        Name = name;
        ColorHex = TagColor.Normalize(colorHex);
        Fill = new ImmutableSolidColorBrush(Color.Parse(ColorHex));
        Foreground = TextOn(ColorHex);
    }

    /// <summary>Preto ou branco sobre a cor, o que contrastar mais (WCAG).</summary>
    public static IBrush TextOn(string colorHex) =>
        TagColor.PrefersDarkText(colorHex) ? DarkText : LightText;

    public Guid Id { get; }

    public string Name { get; }

    public string ColorHex { get; }

    public IBrush Fill { get; }

    /// <summary>Preto ou branco, o que contrastar mais com a cor (WCAG).</summary>
    public IBrush Foreground { get; }

    /// <summary>O seletor dono desta pílula; é por ele que o "×" sabe de qual linha remover.</summary>
    public TaskTagsViewModel? Owner { get; init; }
}
