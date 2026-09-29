namespace MyTaskApp.Desktop.Theming;

/// <summary>
/// Um tema que o usuário pode escolher. O <see cref="Id"/> é o que vai para o
/// <c>widget.json</c>: estável, sem acento, e nunca reaproveitado para outra
/// paleta — renomear o tema na tela não pode trocar a escolha de ninguém.
/// </summary>
/// <param name="IsDark">
/// Diz ao Fluent qual variante usar no que a paleta não cobre (barra de
/// título do Windows, rolagem, seletor de cor).
/// </param>
public sealed record AppTheme(
    string Id,
    string Name,
    string Description,
    bool IsDark,
    ThemePalette Palette);
