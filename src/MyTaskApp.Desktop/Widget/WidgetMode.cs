namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// Quanto do painel fica à mostra. Três degraus, não um contínuo: o usuário
/// escolhe entre trabalhar com a lista, espiar a lista, ou só saber que ela
/// existe.
/// </summary>
public enum WidgetMode
{
    /// <summary>Cabeçalho, progresso, captura e lista.</summary>
    Expanded = 0,

    /// <summary>Cabeçalho e lista. A captura sai de cena.</summary>
    Compact = 1,

    /// <summary>Só a pílula com o resumo. Um clique devolve o painel.</summary>
    Collapsed = 2,
}
