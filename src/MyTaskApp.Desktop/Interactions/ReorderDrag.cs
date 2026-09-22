using Avalonia;

namespace MyTaskApp.Desktop.Interactions;

/// <summary>
/// A aritmética do arrasto de reordenação (ADR-022), separada do gesto.
/// </summary>
/// <remarks>
/// Mora aqui, e não no code-behind, porque é a única parte do arrasto que dá
/// para testar sem tela — e é onde estão as decisões que se erra sem perceber:
/// quando um clique vira arrasto, quando uma linha troca de lugar e quando a
/// lista deve rolar sozinha. O resto (captura de ponteiro, o item levantado, o
/// pouso) só falha de um jeito que nenhum teste headless capturaria.
/// </remarks>
internal static class ReorderDrag
{
    /// <summary>
    /// Folga antes de um clique virar arrasto. Sem ela, quem só quer encostar na
    /// alça levanta a linha com um pixel de tremor de mão.
    /// </summary>
    public const double StartThreshold = 4d;

    /// <summary>Faixa junto à borda da lista que puxa a rolagem.</summary>
    public const double AutoScrollEdge = 24d;

    /// <summary>Teto da rolagem por quadro, em unidades de layout.</summary>
    public const double AutoScrollMaxStep = 12d;

    public static bool ShouldStart(Point grab, Point pointer) =>
        Math.Abs(pointer.Y - grab.Y) >= StartThreshold
        || Math.Abs(pointer.X - grab.X) >= StartThreshold;

    /// <summary>
    /// Para onde a linha vai. Devolve <paramref name="current"/> quando nada
    /// muda.
    /// </summary>
    /// <param name="centers">
    /// O centro vertical de cada linha, na ordem em que elas estão agora.
    /// </param>
    /// <remarks>
    /// Troca <b>um passo por chamada</b>, comparando só com o centro do vizinho
    /// de cima e o do de baixo. Um passo de cada vez não oscila quando o cursor
    /// para em cima de uma divisa, e mantém a animação legível como uma
    /// sequência de trocas simples. Num movimento rápido ele se recupera em
    /// poucos quadros, porque quem segue o cursor de verdade é o item levantado
    /// — a lista embaixo só precisa chegar lá antes de o usuário soltar.
    /// </remarks>
    public static int TargetIndex(IReadOnlyList<double> centers, int current, double pointerY)
    {
        if (current < 0 || current >= centers.Count)
        {
            return current;
        }

        if (current > 0 && pointerY < centers[current - 1])
        {
            return current - 1;
        }

        if (current < centers.Count - 1 && pointerY > centers[current + 1])
        {
            return current + 1;
        }

        return current;
    }

    /// <summary>
    /// Quanto rolar neste quadro. Positivo desce; zero fora das faixas de borda.
    /// </summary>
    public static double ScrollStep(double pointerY, double viewportHeight)
    {
        if (viewportHeight <= 0)
        {
            return 0;
        }

        if (pointerY < AutoScrollEdge)
        {
            return -Step(AutoScrollEdge - pointerY);
        }

        var fromBottom = viewportHeight - pointerY;

        return fromBottom < AutoScrollEdge ? Step(AutoScrollEdge - fromBottom) : 0;

        // Quanto mais fundo na faixa, mais rápido — e nunca além do teto, senão
        // encostar na borda faria a lista disparar.
        static double Step(double depth) =>
            Math.Min(AutoScrollMaxStep, Math.Max(1d, depth / AutoScrollEdge * AutoScrollMaxStep));
    }
}
