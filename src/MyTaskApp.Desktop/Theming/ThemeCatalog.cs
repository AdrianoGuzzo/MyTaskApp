using Avalonia.Media;

namespace MyTaskApp.Desktop.Theming;

/// <summary>
/// Os temas do app (ADR-040). Toda paleta aqui passa pelo teste de contraste
/// (<c>ThemeContrastTests</c>): texto principal em 7:1, todo texto de leitura
/// em 4,5:1 e o que é só forma — ícone, barra, bolinha — em 3:1.
/// </summary>
public static class ThemeCatalog
{
    /// <summary>
    /// Não é um tema: é "siga o Windows". Claro vira <see cref="Paper"/>,
    /// escuro vira <see cref="Charcoal"/>, e alto contraste ligado no sistema
    /// ganha de ambos.
    /// </summary>
    public const string SystemId = "system";

    /// <summary>
    /// O tema de sempre (ADR-017). Carvão, não preto: <c>#000</c> num painel
    /// pequeno e sempre visível vira um buraco na área de trabalho.
    /// </summary>
    public static readonly AppTheme Charcoal = new(
        "charcoal",
        "Carvão",
        "Escuro neutro com destaque índigo. O tema original do app.",
        IsDark: true,
        new ThemePalette
        {
            Canvas = C("#17181C"),
            Surface = C("#1E2025"),
            SurfaceHover = C("#262930"),
            Stroke = C("#2E313A"),
            StrokeSoft = C("#24262D"),
            TextHigh = C("#E9EAEE"),
            TextMid = C("#9AA0AC"),

            // Era #6B7180, 3,6:1 — abaixo do mínimo para a data de 10px.
            TextLow = C("#838996"),

            // Era #6366F1: branco sobre ele dava 4,47:1. A diferença não se vê,
            // e o texto do botão passa a ler.
            Accent = C("#5F63EF"),

            // No escuro o hover fecha em vez de abrir, como no Fluent: abrir
            // tiraria o texto branco do mínimo.
            AccentHover = C("#575ADA"),
            AccentPressed = C("#5053D0"),
            AccentSoft = C("#22233A"),
            AccentLine = C("#3E4180"),
            AccentText = C("#9496F7"),
            OnAccent = C("#FFFFFF"),
            Danger = C("#F87171"),
            DangerSoft = C("#2A1B1E"),
            Caution = C("#FBBF24"),
            CautionSoft = C("#2A2318"),
            Info = C("#60A5FA"),
            Success = C("#34D399"),
        });

    /// <summary>
    /// O claro do app. Fundo cinza-gelo e cartões brancos: a elevação vem da
    /// luz, como no papel, e não de sombra.
    /// </summary>
    public static readonly AppTheme Paper = new(
        "paper",
        "Papel",
        "Claro neutro com destaque índigo. Par do Carvão para o modo claro.",
        IsDark: false,
        new ThemePalette
        {
            Canvas = C("#F4F5F8"),
            Surface = C("#FFFFFF"),
            SurfaceHover = C("#ECEEF3"),
            Stroke = C("#D5D9E2"),
            StrokeSoft = C("#E4E7ED"),
            TextHigh = C("#16181D"),
            TextMid = C("#474D5B"),
            TextLow = C("#646B7A"),
            Accent = C("#4F46E5"),
            AccentHover = C("#4338CA"),
            AccentPressed = C("#3730A3"),
            AccentSoft = C("#E9E8FD"),
            AccentLine = C("#A9A6F3"),
            AccentText = C("#4338CA"),
            OnAccent = C("#FFFFFF"),
            Danger = C("#C81E1E"),
            DangerSoft = C("#FDECEC"),
            Caution = C("#A34A06"),
            CautionSoft = C("#FDF1DE"),
            Info = C("#1D5BD8"),
            Success = C("#067A55"),
        });

    /// <summary>
    /// Claro quente, para ler por muito tempo: menos azul na tela. O destaque
    /// é petróleo porque vermelho, âmbar, azul e verde já têm dono.
    /// </summary>
    public static readonly AppTheme Sepia = new(
        "sepia",
        "Sépia",
        "Claro quente, cor de papel antigo. Cansa menos em sessões longas.",
        IsDark: false,
        new ThemePalette
        {
            Canvas = C("#F3ECDF"),
            Surface = C("#FBF7EF"),
            SurfaceHover = C("#ECE3D2"),
            Stroke = C("#D8CCB6"),
            StrokeSoft = C("#E6DCC8"),
            TextHigh = C("#2B241B"),
            TextMid = C("#584D3F"),
            TextLow = C("#71654F"),
            Accent = C("#0F766E"),
            AccentHover = C("#0C625B"),
            AccentPressed = C("#0A524C"),
            AccentSoft = C("#DDEBE3"),
            AccentLine = C("#8DBDB5"),
            AccentText = C("#0C665F"),
            OnAccent = C("#FFFFFF"),
            Danger = C("#B42318"),
            DangerSoft = C("#F8E1DA"),
            Caution = C("#935000"),
            CautionSoft = C("#F6E6C8"),
            Info = C("#1F58B8"),
            Success = C("#3F7420"),
        });

    /// <summary>
    /// Escuro frio, ardósia azulada, na linha do Nord — com os tons
    /// clareados até o texto e a sinalização passarem do mínimo.
    /// </summary>
    public static readonly AppTheme Nordic = new(
        "nordic",
        "Nórdico",
        "Escuro frio em ardósia azulada, com destaque gelo.",
        IsDark: true,
        new ThemePalette
        {
            Canvas = C("#1C212B"),
            Surface = C("#232A36"),
            SurfaceHover = C("#2B3342"),
            Stroke = C("#374154"),
            StrokeSoft = C("#2C3443"),
            TextHigh = C("#ECEFF4"),
            TextMid = C("#AEB8CA"),
            TextLow = C("#8A96AB"),
            Accent = C("#88C0D0"),
            AccentHover = C("#9ECDDB"),
            AccentPressed = C("#72AFC1"),
            AccentSoft = C("#243844"),
            AccentLine = C("#3F6676"),
            AccentText = C("#88C0D0"),
            OnAccent = C("#16202A"),
            Danger = C("#EE9199"),
            DangerSoft = C("#352329"),
            Caution = C("#EBCB8B"),
            CautionSoft = C("#342F26"),
            Info = C("#9BB5EC"),
            Success = C("#A3BE8C"),
        });

    /// <summary>
    /// Escuro com um fundo de ameixa e destaque magenta. Magenta, e não rosa:
    /// rosa ficaria perto demais do vermelho de perigo.
    /// </summary>
    public static readonly AppTheme Plum = new(
        "plum",
        "Ameixa",
        "Escuro aconchegante em tons de ameixa, com destaque magenta.",
        IsDark: true,
        new ThemePalette
        {
            Canvas = C("#1A1620"),
            Surface = C("#221C29"),
            SurfaceHover = C("#2B2433"),
            Stroke = C("#3A3044"),
            StrokeSoft = C("#2D2536"),
            TextHigh = C("#F1ECF5"),
            TextMid = C("#B5A9C2"),
            TextLow = C("#948AA3"),
            Accent = C("#E27BF0"),
            AccentHover = C("#EA96F4"),
            AccentPressed = C("#D160E2"),
            AccentSoft = C("#3A2243"),
            AccentLine = C("#6E3F7B"),
            AccentText = C("#E896F3"),
            OnAccent = C("#1F0F25"),
            Danger = C("#FF8A8A"),
            DangerSoft = C("#361D24"),
            Caution = C("#F5C451"),
            CautionSoft = C("#33291C"),
            Info = C("#7FB4FF"),
            Success = C("#5FD9A5"),
        });

    /// <summary>
    /// Para baixa visão e luz forte. Preto de verdade aqui é o ponto, e as
    /// bordas passam de 3:1 — no resto dos temas elas são só sugestão.
    /// Destaque amarelo, o mesmo recurso do alto contraste do Windows.
    /// </summary>
    public static readonly AppTheme HighContrast = new(
        "high-contrast",
        "Alto contraste",
        "Preto e branco com destaque amarelo. Máxima legibilidade.",
        IsDark: true,
        new ThemePalette
        {
            Canvas = C("#000000"),
            Surface = C("#0A0A0A"),
            SurfaceHover = C("#1F1F1F"),
            Stroke = C("#A0A0A0"),
            StrokeSoft = C("#6E6E6E"),
            TextHigh = C("#FFFFFF"),
            TextMid = C("#E0E0E0"),
            TextLow = C("#C4C4C4"),
            Accent = C("#FFD400"),
            AccentHover = C("#FFE14D"),
            AccentPressed = C("#E6BF00"),
            AccentSoft = C("#2E2600"),
            AccentLine = C("#FFD400"),
            AccentText = C("#FFD400"),
            OnAccent = C("#000000"),
            Danger = C("#FF7070"),
            DangerSoft = C("#2E0A0A"),
            Caution = C("#FFA24C"),
            CautionSoft = C("#2E1A05"),
            Info = C("#6CC4FF"),
            Success = C("#5CF28A"),
        });

    /// <summary>Claros primeiro, depois escuros, e o de acessibilidade por último.</summary>
    public static IReadOnlyList<AppTheme> All { get; } =
        [Paper, Sepia, Charcoal, Nordic, Plum, HighContrast];

    /// <summary>
    /// Um id que não existe mais — tema removido, arquivo editado à mão —
    /// volta para "seguir o Windows", e não para um tema qualquer.
    /// </summary>
    public static string Normalize(string? choice) =>
        choice is not null && (choice == SystemId || Find(choice) is not null)
            ? choice
            : SystemId;

    public static AppTheme? Find(string id) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// O tema que vai para a tela. Escolha explícita ganha de tudo — inclusive
    /// do alto contraste do Windows: quem escolheu um tema viu como ele fica.
    /// </summary>
    public static AppTheme Resolve(string? choice, SystemAppearance system)
    {
        if (Find(Normalize(choice)) is { } chosen)
        {
            return chosen;
        }

        if (system.HighContrast)
        {
            return HighContrast;
        }

        return system.IsDark ? Charcoal : Paper;
    }

    private static Color C(string hex) => Color.Parse(hex);
}

/// <summary>O que o sistema operacional diz sobre a aparência.</summary>
/// <param name="IsDark">Modo escuro do Windows ("Escolha o modo do app").</param>
/// <param name="HighContrast">Um tema de contraste do Windows está ligado.</param>
public readonly record struct SystemAppearance(bool IsDark, bool HighContrast);
