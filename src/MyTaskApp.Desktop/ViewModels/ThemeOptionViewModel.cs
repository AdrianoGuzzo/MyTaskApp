using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyTaskApp.Desktop.Theming;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Um item do menu "Tema": nome, descrição e uma amostra desenhada com as
/// cores do próprio tema. A amostra é o que faz a escolha ser por
/// reconhecimento — ninguém lembra o que "Nórdico" quer dizer, mas vê.
/// </summary>
public sealed partial class ThemeOptionViewModel : ObservableObject
{
    private readonly Action<string> _select;

    [ObservableProperty]
    private bool _isSelected;

    private ThemeOptionViewModel(
        string id,
        string name,
        string description,
        Color swatchStart,
        Color swatchEnd,
        Color swatchAccent,
        Action<string> select)
    {
        Id = id;
        Name = name;
        Description = description;
        SwatchStart = new ImmutableSolidColorBrush(swatchStart);
        SwatchEnd = new ImmutableSolidColorBrush(swatchEnd);
        SwatchAccent = new ImmutableSolidColorBrush(swatchAccent);
        _select = select;
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    /// <summary>Metade esquerda da amostra: o fundo do painel.</summary>
    public IBrush SwatchStart { get; }

    /// <summary>Metade direita: o cartão. No "Automático", o tema escuro.</summary>
    public IBrush SwatchEnd { get; }

    /// <summary>O ponto no meio: a cor de destaque.</summary>
    public IBrush SwatchAccent { get; }

    /// <summary>
    /// "Seguir o Windows" desenha meio Papel, meio Carvão: diz o que faz sem
    /// precisar ler.
    /// </summary>
    public static ThemeOptionViewModel ForSystem(Action<string> select) => new(
        ThemeCatalog.SystemId,
        "Automático",
        "Segue o modo do Windows: Papel no claro, Carvão no escuro, e alto contraste se ele estiver ligado no sistema.",
        ThemeCatalog.Paper.Palette.Canvas,
        ThemeCatalog.Charcoal.Palette.Canvas,
        ThemeCatalog.Charcoal.Palette.Accent,
        select);

    public static ThemeOptionViewModel For(AppTheme theme, Action<string> select) => new(
        theme.Id,
        theme.Name,
        theme.Description,
        theme.Palette.Canvas,
        theme.Palette.Surface,
        theme.Palette.Accent,
        select);

    [RelayCommand]
    private void Select() => _select(Id);
}
