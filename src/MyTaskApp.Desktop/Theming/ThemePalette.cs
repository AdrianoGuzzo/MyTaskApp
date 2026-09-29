using Avalonia.Media;

namespace MyTaskApp.Desktop.Theming;

/// <summary>
/// As cores de um tema, e só elas. Cada propriedade vira um par de recursos
/// <c>Widget{Nome}Color</c> / <c>Widget{Nome}Brush</c> (ADR-041): as telas
/// pedem o papel da cor ("texto de apoio", "perigo"), nunca o hex.
/// </summary>
/// <remarks>
/// Em C#, e não num <c>.axaml</c> por tema: assim o teste de contraste lê a
/// mesma paleta que a tela desenha, sem subir janela.
/// </remarks>
public sealed record ThemePalette
{
    // Superfícies, da mais funda para a mais alta.
    public required Color Canvas { get; init; }

    public required Color Surface { get; init; }

    public required Color SurfaceHover { get; init; }

    public required Color Stroke { get; init; }

    public required Color StrokeSoft { get; init; }

    // Texto: três níveis, e só três.
    public required Color TextHigh { get; init; }

    public required Color TextMid { get; init; }

    public required Color TextLow { get; init; }

    // Destaque: ação principal, progresso, concluído.
    public required Color Accent { get; init; }

    public required Color AccentHover { get; init; }

    public required Color AccentPressed { get; init; }

    public required Color AccentSoft { get; init; }

    /// <summary>Contorno de foco: o destaque apagado, para o campo vazio não gritar.</summary>
    public required Color AccentLine { get; init; }

    /// <summary>
    /// O destaque quando ele é <b>texto</b>. Num tema escuro com letra branca
    /// sobre o botão, a mesma cor não passa de 4,5:1 contra o fundo e contra o
    /// branco ao mesmo tempo — é aritmética, não gosto. Então são duas.
    /// </summary>
    public required Color AccentText { get; init; }

    /// <summary>A letra sobre o destaque: branca nos acentos fechados, escura nos claros.</summary>
    public required Color OnAccent { get; init; }

    // Sinalização. Mesma família de matiz em todo tema: vermelho é perigo,
    // âmbar é atenção, azul é informação, verde é sucesso.
    public required Color Danger { get; init; }

    public required Color DangerSoft { get; init; }

    public required Color Caution { get; init; }

    public required Color CautionSoft { get; init; }

    public required Color Info { get; init; }

    public required Color Success { get; init; }
}
