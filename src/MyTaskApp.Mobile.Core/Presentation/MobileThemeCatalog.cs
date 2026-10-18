namespace MyTaskApp.Mobile.Core.Presentation;

/// <summary>
/// As cores de um tema no celular, com os mesmos nomes do <c>ThemePalette</c>
/// do desktop (ADR-041): cada uma vira um recurso de mesmo nome na tela, e as
/// telas pedem o papel da cor, nunca o hex. Em texto, e não em
/// <c>Color</c>, para o núcleo não depender do MAUI.
/// </summary>
public sealed record MobilePalette
{
    public required string Canvas { get; init; }

    public required string Surface { get; init; }

    public required string SurfaceHover { get; init; }

    public required string Stroke { get; init; }

    public required string StrokeSoft { get; init; }

    public required string TextHigh { get; init; }

    public required string TextMid { get; init; }

    public required string TextLow { get; init; }

    public required string Accent { get; init; }

    public required string AccentHover { get; init; }

    public required string AccentPressed { get; init; }

    public required string AccentSoft { get; init; }

    public required string AccentLine { get; init; }

    public required string AccentText { get; init; }

    public required string OnAccent { get; init; }

    public required string Danger { get; init; }

    public required string DangerSoft { get; init; }

    public required string Caution { get; init; }

    public required string CautionSoft { get; init; }

    public required string Info { get; init; }

    public required string Success { get; init; }

    /// <summary>
    /// Nome e hex de cada cor: o que a casca põe nos recursos. Lista escrita, e
    /// não reflexão, para o corte de código do iOS não levar nenhuma.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Entries() =>
    [
        new(nameof(Canvas), Canvas),
        new(nameof(Surface), Surface),
        new(nameof(SurfaceHover), SurfaceHover),
        new(nameof(Stroke), Stroke),
        new(nameof(StrokeSoft), StrokeSoft),
        new(nameof(TextHigh), TextHigh),
        new(nameof(TextMid), TextMid),
        new(nameof(TextLow), TextLow),
        new(nameof(Accent), Accent),
        new(nameof(AccentHover), AccentHover),
        new(nameof(AccentPressed), AccentPressed),
        new(nameof(AccentSoft), AccentSoft),
        new(nameof(AccentLine), AccentLine),
        new(nameof(AccentText), AccentText),
        new(nameof(OnAccent), OnAccent),
        new(nameof(Danger), Danger),
        new(nameof(DangerSoft), DangerSoft),
        new(nameof(Caution), Caution),
        new(nameof(CautionSoft), CautionSoft),
        new(nameof(Info), Info),
        new(nameof(Success), Success),
    ];
}

/// <summary>Um tema que dá para escolher no celular; o <see cref="Id"/> é o mesmo do desktop.</summary>
/// <param name="IsDark">Diz ao sistema qual variante usar no que a paleta não cobre: teclado, barra de status, seletores.</param>
public sealed record MobileTheme(string Id, string Name, string Description, bool IsDark, MobilePalette Palette);

/// <summary>
/// Os sete temas do desktop (<c>ThemeCatalog</c>, ADR-041), com as mesmas
/// cores — um teste do desktop compara uma a uma, então o contraste medido lá
/// vale aqui. A escolha fica só neste celular, como o <c>widget.json</c> do
/// desktop fica só no computador.
/// </summary>
public static class MobileThemeCatalog
{
    /// <summary>Não é um tema: é "siga o celular". Claro vira Papel, escuro vira Carvão.</summary>
    public const string SystemId = "system";

    public static readonly MobileTheme Charcoal = new(
        "charcoal",
        "Carvão",
        "Escuro neutro com destaque índigo. O tema original do app.",
        IsDark: true,
        new MobilePalette
        {
            Canvas = "#17181C",
            Surface = "#1E2025",
            SurfaceHover = "#262930",
            Stroke = "#2E313A",
            StrokeSoft = "#24262D",
            TextHigh = "#E9EAEE",
            TextMid = "#9AA0AC",
            TextLow = "#838996",
            Accent = "#5F63EF",
            AccentHover = "#575ADA",
            AccentPressed = "#5053D0",
            AccentSoft = "#22233A",
            AccentLine = "#3E4180",
            AccentText = "#9496F7",
            OnAccent = "#FFFFFF",
            Danger = "#F87171",
            DangerSoft = "#2A1B1E",
            Caution = "#FBBF24",
            CautionSoft = "#2A2318",
            Info = "#60A5FA",
            Success = "#34D399",
        });

    public static readonly MobileTheme Paper = new(
        "paper",
        "Papel",
        "Claro neutro com destaque índigo. Par do Carvão para o modo claro.",
        IsDark: false,
        new MobilePalette
        {
            Canvas = "#F4F5F8",
            Surface = "#FFFFFF",
            SurfaceHover = "#ECEEF3",
            Stroke = "#D5D9E2",
            StrokeSoft = "#E4E7ED",
            TextHigh = "#16181D",
            TextMid = "#474D5B",
            TextLow = "#646B7A",
            Accent = "#4F46E5",
            AccentHover = "#4338CA",
            AccentPressed = "#3730A3",
            AccentSoft = "#E9E8FD",
            AccentLine = "#A9A6F3",
            AccentText = "#4338CA",
            OnAccent = "#FFFFFF",
            Danger = "#C81E1E",
            DangerSoft = "#FDECEC",
            Caution = "#A34A06",
            CautionSoft = "#FDF1DE",
            Info = "#1D5BD8",
            Success = "#067A55",
        });

    public static readonly MobileTheme Sepia = new(
        "sepia",
        "Sépia",
        "Claro quente, cor de papel antigo. Cansa menos em sessões longas.",
        IsDark: false,
        new MobilePalette
        {
            Canvas = "#F3ECDF",
            Surface = "#FBF7EF",
            SurfaceHover = "#ECE3D2",
            Stroke = "#D8CCB6",
            StrokeSoft = "#E6DCC8",
            TextHigh = "#2B241B",
            TextMid = "#584D3F",
            TextLow = "#71654F",
            Accent = "#0F766E",
            AccentHover = "#0C625B",
            AccentPressed = "#0A524C",
            AccentSoft = "#DDEBE3",
            AccentLine = "#8DBDB5",
            AccentText = "#0C665F",
            OnAccent = "#FFFFFF",
            Danger = "#B42318",
            DangerSoft = "#F8E1DA",
            Caution = "#935000",
            CautionSoft = "#F6E6C8",
            Info = "#1F58B8",
            Success = "#3F7420",
        });

    public static readonly MobileTheme Nordic = new(
        "nordic",
        "Nórdico",
        "Escuro frio em ardósia azulada, com destaque gelo.",
        IsDark: true,
        new MobilePalette
        {
            Canvas = "#1C212B",
            Surface = "#232A36",
            SurfaceHover = "#2B3342",
            Stroke = "#374154",
            StrokeSoft = "#2C3443",
            TextHigh = "#ECEFF4",
            TextMid = "#AEB8CA",
            TextLow = "#8A96AB",
            Accent = "#88C0D0",
            AccentHover = "#9ECDDB",
            AccentPressed = "#72AFC1",
            AccentSoft = "#243844",
            AccentLine = "#3F6676",
            AccentText = "#88C0D0",
            OnAccent = "#16202A",
            Danger = "#EE9199",
            DangerSoft = "#352329",
            Caution = "#EBCB8B",
            CautionSoft = "#342F26",
            Info = "#9BB5EC",
            Success = "#A3BE8C",
        });

    public static readonly MobileTheme Plum = new(
        "plum",
        "Ameixa",
        "Escuro aconchegante em tons de ameixa, com destaque magenta.",
        IsDark: true,
        new MobilePalette
        {
            Canvas = "#1A1620",
            Surface = "#221C29",
            SurfaceHover = "#2B2433",
            Stroke = "#3A3044",
            StrokeSoft = "#2D2536",
            TextHigh = "#F1ECF5",
            TextMid = "#B5A9C2",
            TextLow = "#948AA3",
            Accent = "#E27BF0",
            AccentHover = "#EA96F4",
            AccentPressed = "#D160E2",
            AccentSoft = "#3A2243",
            AccentLine = "#6E3F7B",
            AccentText = "#E896F3",
            OnAccent = "#1F0F25",
            Danger = "#FF8A8A",
            DangerSoft = "#361D24",
            Caution = "#F5C451",
            CautionSoft = "#33291C",
            Info = "#7FB4FF",
            Success = "#5FD9A5",
        });

    public static readonly MobileTheme Ponta = new(
        "ponta",
        "Verde Pecuária",
        "Escuro em verde-petróleo profundo, com destaque verde-limão.",
        IsDark: true,
        new MobilePalette
        {
            Canvas = "#104544",
            Surface = "#154E44",
            SurfaceHover = "#1E5B45",
            Stroke = "#2D6F44",
            StrokeSoft = "#1E5B45",
            TextHigh = "#FFFFFF",
            TextMid = "#C5DBD1",
            TextLow = "#A3C2B5",
            Accent = "#7CBB3B",
            AccentHover = "#A2D152",
            AccentPressed = "#5EA341",
            AccentSoft = "#24573A",
            AccentLine = "#4F9543",
            AccentText = "#A2D152",
            OnAccent = "#0A2F2E",
            Danger = "#FF9999",
            DangerSoft = "#3B2630",
            Caution = "#F6CC5E",
            CautionSoft = "#3A3424",
            Info = "#8CC8FF",
            Success = "#6EE7B7",
        });

    public static readonly MobileTheme HighContrast = new(
        "high-contrast",
        "Alto contraste",
        "Preto e branco com destaque amarelo. Máxima legibilidade.",
        IsDark: true,
        new MobilePalette
        {
            Canvas = "#000000",
            Surface = "#0A0A0A",
            SurfaceHover = "#1F1F1F",
            Stroke = "#A0A0A0",
            StrokeSoft = "#6E6E6E",
            TextHigh = "#FFFFFF",
            TextMid = "#E0E0E0",
            TextLow = "#C4C4C4",
            Accent = "#FFD400",
            AccentHover = "#FFE14D",
            AccentPressed = "#E6BF00",
            AccentSoft = "#2E2600",
            AccentLine = "#FFD400",
            AccentText = "#FFD400",
            OnAccent = "#000000",
            Danger = "#FF7070",
            DangerSoft = "#2E0A0A",
            Caution = "#FFA24C",
            CautionSoft = "#2E1A05",
            Info = "#6CC4FF",
            Success = "#5CF28A",
        });

    /// <summary>A ordem do desktop: claros primeiro, depois escuros, e o de acessibilidade por último.</summary>
    public static IReadOnlyList<MobileTheme> All { get; } =
        [Paper, Sepia, Charcoal, Nordic, Plum, Ponta, HighContrast];

    /// <summary>Um id que não existe mais volta para "seguir o celular", e não para um tema qualquer.</summary>
    public static string Normalize(string? choice) =>
        choice is not null && (choice == SystemId || Find(choice) is not null)
            ? choice
            : SystemId;

    public static MobileTheme? Find(string id) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.Ordinal));

    /// <summary>O tema que vai para a tela: a escolha explícita, ou o par claro/escuro do sistema.</summary>
    public static MobileTheme Resolve(string? choice, bool systemIsDark) =>
        Find(Normalize(choice)) ?? (systemIsDark ? Charcoal : Paper);
}
