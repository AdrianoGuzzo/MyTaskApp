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

    public bool Topmost { get; init; }

    /// <summary>
    /// Modo discreto: sem moldura, sem cabeçalho, só as tarefas sobre a área de
    /// trabalho. Preferência, e não geometria — por isso mora aqui e não vira
    /// um quarto <see cref="WidgetMode"/>.
    /// </summary>
    public bool Ghost { get; init; }

    public WidgetMode Mode { get; init; } = WidgetMode.Expanded;

    /// <summary>Abrir direto na bandeja, sem mostrar o painel.</summary>
    public bool StartHidden { get; init; }

    /// <summary>
    /// Um arquivo corrompido ou editado à mão não pode deixar o painel com
    /// tamanho zero, nem recolhido para sempre sem forma de voltar.
    /// </summary>
    public WidgetState Sanitized() => this with
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
    };
}
