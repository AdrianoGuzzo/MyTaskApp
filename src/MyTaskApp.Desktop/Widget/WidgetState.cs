using MyTaskApp.Desktop.Theming;

namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// O que o widget lembra entre as sessões: onde ele estava, de que tamanho, e
/// como o usuário o deixou. Não é dado de negócio — por isso mora num JSON ao
/// lado do banco, e não numa tabela com migração.
/// </summary>
public sealed record WidgetState
{
    public static readonly WidgetState Default = new();

    /// <summary>Canto superior esquerdo em pixels físicos (<c>Window.Position</c>).</summary>
    public int? X { get; init; }

    public int? Y { get; init; }

    /// <summary>Tamanho do modo expandido, em unidades independentes de DPI.</summary>
    public double Width { get; init; } = WidgetMetrics.DefaultWidth;

    public double Height { get; init; } = WidgetMetrics.DefaultHeight;

    /// <summary>
    /// "Sempre no topo" da janela <b>normal</b>. O HUD tem o seu
    /// (<see cref="HudSettings.AlwaysOnTop"/>): fixar o HUD num canto não pode
    /// deixar a janela grande presa na frente de tudo ao sair dele (ADR-047).
    /// </summary>
    public bool Topmost { get; init; }

    /// <summary>
    /// Legado: o modo discreto, que o pino ligava junto com o "sempre no topo"
    /// até o ADR-047. Só é lido — <see cref="Sanitized"/> converte o pino
    /// antigo em HUD e apaga o campo, que nunca mais é gravado.
    /// </summary>
    public bool? Ghost { get; init; }

    public WidgetMode Mode { get; init; } = WidgetMode.Expanded;

    /// <summary>Como a janela estava ao fechar — reaberto, o app volta assim.</summary>
    public WindowMode WindowMode { get; init; } = WindowMode.Normal;

    /// <summary>Esconder na bandeja continua sendo o padrão: é o que o X sempre fez (ADR-016).</summary>
    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.Tray;

    /// <summary>Abrir sempre como HUD, independentemente de como fechou.</summary>
    public bool StartInHud { get; init; }

    public HudSettings Hud { get; init; } = HudSettings.Default;

    /// <summary>
    /// Ctrl+Shift+Espaço alterna janela e HUD. Desligado por padrão: no Visual
    /// Studio e no VS Code a mesma combinação é "informações de parâmetro", e
    /// um atalho global a roubaria sem aviso.
    /// </summary>
    public bool GlobalHotkey { get; init; }

    /// <summary>Abrir direto na bandeja, sem mostrar o painel.</summary>
    public bool StartHidden { get; init; }

    /// <summary>
    /// O id do tema escolhido, ou "seguir o Windows" (ADR-041). Texto, e não
    /// enum: um tema a mais no catálogo não pode exigir migrar o arquivo.
    /// </summary>
    public string Theme { get; init; } = ThemeCatalog.SystemId;

    /// <summary>
    /// Um arquivo corrompido ou editado à mão não pode deixar o painel com
    /// tamanho zero, nem recolhido para sempre sem forma de voltar.
    /// </summary>
    public WidgetState Sanitized() => Clamped().MigrateGhost();

    /// <summary>
    /// O pino antigo era "fica no canto, por cima, sem me atrapalhar" — que é
    /// o HUD. Quem atualiza com o painel fixado reabre no HUD, e não numa
    /// janela grande presa na frente de tudo.
    /// </summary>
    private WidgetState MigrateGhost() => Ghost switch
    {
        true when Topmost => this with { Ghost = null, Topmost = false, WindowMode = WindowMode.Hud },
        null => this,
        _ => this with { Ghost = null },
    };

    private WidgetState Clamped() => this with
    {
        Width = Math.Clamp(
            double.IsFinite(Width) ? Width : WidgetMetrics.DefaultWidth,
            WidgetMetrics.MinWidth,
            4000),
        Height = Math.Clamp(
            double.IsFinite(Height) ? Height : WidgetMetrics.DefaultHeight,
            WidgetMetrics.MinExpandedHeight,
            4000),
        Mode = Enum.IsDefined(Mode) ? Mode : WidgetMode.Expanded,
        WindowMode = Enum.IsDefined(WindowMode) ? WindowMode : WindowMode.Normal,
        CloseBehavior = Enum.IsDefined(CloseBehavior) ? CloseBehavior : CloseBehavior.Tray,
        Hud = (Hud ?? HudSettings.Default).Sanitized(),
        Theme = ThemeCatalog.Normalize(Theme),
    };
}
