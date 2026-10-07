namespace MyTaskApp.Desktop.Widget;

/// <summary>Os modificadores do <c>RegisterHotKey</c>, com os mesmos valores do Windows.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
}

/// <summary>
/// Uma combinação de teclas que o app registra no sistema (ADR-048, ADR-054).
/// O <see cref="Id"/> é o que vai para o <c>widget.json</c>: estável, sem
/// acento, como o id do tema.
/// </summary>
/// <param name="RegistrationId">
/// O id do <c>RegisterHotKey</c>. Um por propósito, e não por combinação: o
/// <c>WM_HOTKEY</c> diz qual id disparou, e cada atalho escuta só o seu.
/// </param>
/// <param name="VirtualKey">O código de tecla virtual do Windows.</param>
public sealed record HotkeyGesture(
    string Id,
    int RegistrationId,
    HotkeyModifiers Modifiers,
    uint VirtualKey,
    string Label)
{
    /// <summary>"Sem atalho" no <c>widget.json</c>.</summary>
    public const string Off = "off";

    private const int ToggleHudRegistration = 0x4D54;
    private const int NewNoteRegistration = 0x4D55;
    private const uint VkSpace = 0x20;
    private const uint VkN = 0x4E;

    /// <summary>Alterna janela e HUD (ADR-048). Nasce desligado: no VS Code é "informações de parâmetro".</summary>
    public static HotkeyGesture ToggleHud { get; } = new(
        "ctrl-shift-space",
        ToggleHudRegistration,
        HotkeyModifiers.Control | HotkeyModifiers.Shift,
        VkSpace,
        "Ctrl+Shift+Espaço");

    /// <summary>
    /// Novo post-it de qualquer lugar (ADR-054). O padrão, e ligado: nenhum dos
    /// programas de todo dia de quem programa o usa. Ctrl+Shift+N seria o óbvio,
    /// mas é "nova janela" no VS Code, "janela anônima" no navegador e "nova
    /// pasta" no Explorer — um atalho global o roubaria dos três.
    /// </summary>
    public static HotkeyGesture NewNoteCtrlAltN { get; } = new(
        "ctrl-alt-n",
        NewNoteRegistration,
        HotkeyModifiers.Control | HotkeyModifiers.Alt,
        VkN,
        "Ctrl+Alt+N");

    /// <summary>Para quem prefere a combinação de dentro do app também fora dele, sabendo o que ela rouba.</summary>
    public static HotkeyGesture NewNoteCtrlShiftN { get; } = new(
        "ctrl-shift-n",
        NewNoteRegistration,
        HotkeyModifiers.Control | HotkeyModifiers.Shift,
        VkN,
        "Ctrl+Shift+N");

    public static IReadOnlyList<HotkeyGesture> NewNoteChoices { get; } = [NewNoteCtrlAltN, NewNoteCtrlShiftN];

    public static string DefaultNewNote => NewNoteCtrlAltN.Id;

    public static HotkeyGesture? FindNewNote(string? id) =>
        NewNoteChoices.FirstOrDefault(gesture => gesture.Id == id);

    /// <summary>Um id desconhecido — arquivo editado à mão, atalho removido — volta ao padrão.</summary>
    public static string NormalizeNewNote(string? id) =>
        id == Off || FindNewNote(id) is not null ? id! : DefaultNewNote;
}
