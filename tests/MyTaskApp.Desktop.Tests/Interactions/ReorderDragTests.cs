using Avalonia;
using MyTaskApp.Desktop.Interactions;

namespace MyTaskApp.Desktop.Tests.Interactions;

/// <summary>
/// A aritmética do arrasto, sem tela. É onde moram as decisões que se erram sem
/// perceber: quando um clique vira arrasto, quando uma linha troca de lugar e
/// quando a lista rola sozinha.
/// </summary>
public class ReorderDragTests
{
    /// <summary>Quatro linhas de 24px: centros em 12, 36, 60 e 84.</summary>
    private static readonly double[] Centers = [12, 36, 60, 84];

    [Fact]
    public void ATremorOfTheHand_IsNotADrag()
    {
        ReorderDrag.ShouldStart(new Point(10, 10), new Point(11, 12)).Should().BeFalse();
    }

    [Fact]
    public void PastTheThreshold_TheGestureBecomesADrag()
    {
        ReorderDrag.ShouldStart(new Point(10, 10), new Point(10, 15)).Should().BeTrue();
    }

    /// <summary>
    /// O limiar também vale na horizontal: quem começa a arrastar de lado ainda
    /// está arrastando, e esperar o movimento vertical faria o gesto parecer
    /// travado no começo.
    /// </summary>
    [Fact]
    public void ASidewaysPull_AlsoStartsTheDrag()
    {
        ReorderDrag.ShouldStart(new Point(10, 10), new Point(16, 10)).Should().BeTrue();
    }

    [Fact]
    public void OverItsOwnRow_NothingMoves()
    {
        ReorderDrag.TargetIndex(Centers, current: 1, pointerY: 36).Should().Be(1);
    }

    [Fact]
    public void PastTheCentreOfTheRowBelow_TheLineGoesDownOneStep()
    {
        ReorderDrag.TargetIndex(Centers, current: 1, pointerY: 61).Should().Be(2);
    }

    [Fact]
    public void PastTheCentreOfTheRowAbove_TheLineGoesUpOneStep()
    {
        ReorderDrag.TargetIndex(Centers, current: 1, pointerY: 11).Should().Be(0);
    }

    /// <summary>
    /// Um passo por chamada de propósito: a lista não pula três casas num quadro
    /// só, e o item levantado — que segue o cursor de verdade — dá tempo de a
    /// lista chegar lá.
    /// </summary>
    [Fact]
    public void AFastFlick_StillMovesOneStepAtATime()
    {
        ReorderDrag.TargetIndex(Centers, current: 0, pointerY: 500).Should().Be(1);
    }

    [Fact]
    public void AtTheTop_TheLineDoesNotLeaveTheList()
    {
        ReorderDrag.TargetIndex(Centers, current: 0, pointerY: -200).Should().Be(0);
    }

    [Fact]
    public void AtTheBottom_TheLineDoesNotLeaveTheList()
    {
        ReorderDrag.TargetIndex(Centers, current: 3, pointerY: 900).Should().Be(3);
    }

    /// <summary>
    /// Exatamente sobre o centro do vizinho não troca: é o "maior que" que
    /// impede a lista de tremer quando o cursor para numa divisa.
    /// </summary>
    [Fact]
    public void ExactlyOnTheNeighboursCentre_NothingMoves()
    {
        ReorderDrag.TargetIndex(Centers, current: 1, pointerY: 60).Should().Be(1);
        ReorderDrag.TargetIndex(Centers, current: 1, pointerY: 12).Should().Be(1);
    }

    [Fact]
    public void AnIndexOutsideTheList_IsLeftAlone()
    {
        ReorderDrag.TargetIndex(Centers, current: 9, pointerY: 40).Should().Be(9);
    }

    [Fact]
    public void InTheMiddleOfTheViewport_NothingScrolls()
    {
        ReorderDrag.ScrollStep(150, viewportHeight: 300).Should().Be(0);
    }

    [Fact]
    public void NearTheTopEdge_TheListScrollsUp()
    {
        ReorderDrag.ScrollStep(4, viewportHeight: 300).Should().BeNegative();
    }

    [Fact]
    public void NearTheBottomEdge_TheListScrollsDown()
    {
        ReorderDrag.ScrollStep(297, viewportHeight: 300).Should().BePositive();
    }

    /// <summary>
    /// Encostar na borda não pode fazer a lista disparar: há um teto por quadro.
    /// </summary>
    [Fact]
    public void FarBeyondTheEdge_TheStepIsCapped()
    {
        Math.Abs(ReorderDrag.ScrollStep(-500, viewportHeight: 300))
            .Should().Be(ReorderDrag.AutoScrollMaxStep);

        ReorderDrag.ScrollStep(800, viewportHeight: 300)
            .Should().Be(ReorderDrag.AutoScrollMaxStep);
    }

    [Fact]
    public void WithoutAViewport_NothingScrolls()
    {
        ReorderDrag.ScrollStep(10, viewportHeight: 0).Should().Be(0);
    }
}
