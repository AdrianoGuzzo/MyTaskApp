using Avalonia;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Tests.Widget;

/// <summary>
/// Onde o HUD pousa (ADR-047). Telas são retângulos em pixels físicos, como no
/// <see cref="WidgetPlacementTests"/>: dá para testar monitor à esquerda com
/// coordenadas negativas e escala de 150% sem monitor nenhum.
/// </summary>
public class HudPlacementTests
{
    /// <summary>Barra de tarefas embaixo: a área de trabalho não vai até 1080.</summary>
    private static readonly PixelRect Laptop = new(0, 0, 1920, 1040);

    private static readonly PixelRect Secondary = new(-2560, 0, 2560, 1400);

    private static readonly PixelSize Card = new(260, 180);

    private const int Margin = 12;

    private static PixelRect Place(HudPosition position, PixelRect? area = null, PixelSize? size = null) =>
        HudPlacement.Resolve(position, area ?? Laptop, size ?? Card, Margin, custom: null, [Laptop, Secondary]);

    [Fact]
    public void TopLeft_IsTheDefault_AndKeepsTheMargin()
    {
        HudSettings.Default.Position.Should().Be(HudPosition.TopLeft);

        Place(HudPosition.TopLeft).TopLeft.Should().Be(new PixelPoint(12, 12));
    }

    [Fact]
    public void TopRight_HugsTheRightEdge()
    {
        var rect = Place(HudPosition.TopRight);

        rect.Right.Should().Be(Laptop.Right - Margin);
        rect.Y.Should().Be(Margin);
    }

    [Fact]
    public void BottomLeft_SitsAboveTheTaskbar()
    {
        var rect = Place(HudPosition.BottomLeft);

        rect.X.Should().Be(Margin);
        rect.Bottom.Should().Be(Laptop.Bottom - Margin);
    }

    [Fact]
    public void BottomRight_HugsBothEdges()
    {
        var rect = Place(HudPosition.BottomRight);

        rect.Right.Should().Be(Laptop.Right - Margin);
        rect.Bottom.Should().Be(Laptop.Bottom - Margin);
    }

    [Theory]
    [InlineData(HudPosition.CenterLeft, 12)]
    [InlineData(HudPosition.CenterRight, 1920 - 12 - 260)]
    public void TheCentredOnes_AreVerticallyCentred(HudPosition position, int x)
    {
        var rect = Place(position);

        rect.X.Should().Be(x);
        rect.Y.Should().Be((Laptop.Height - Card.Height) / 2);
    }

    [Fact]
    public void AnchoredAtTheBottom_GrowingKeepsTheBottomEdge()
    {
        // É chamado de novo a cada mudança de altura: o HUD com mais tarefas
        // cresce para cima, em vez de descer para trás da barra de tarefas.
        var small = Place(HudPosition.BottomRight, size: new PixelSize(260, 120));
        var tall = Place(HudPosition.BottomRight, size: new PixelSize(260, 400));

        tall.Bottom.Should().Be(small.Bottom);
        tall.Y.Should().BeLessThan(small.Y);
    }

    [Fact]
    public void OnTheSecondaryScreen_ItUsesThatScreensCorner()
    {
        // "O monitor que está sendo usado": quem chama passa a área da tela da
        // janela, e o canto é desse monitor — coordenadas negativas incluídas.
        var rect = Place(HudPosition.TopLeft, area: Secondary);

        rect.TopLeft.Should().Be(new PixelPoint(-2560 + Margin, Margin));
    }

    [Fact]
    public void TheMarginFollowsTheScale()
    {
        HudPlacement.MarginFor(1).Should().Be(12);
        HudPlacement.MarginFor(1.5).Should().Be(18);
        HudPlacement.MarginFor(2).Should().Be(24);

        // Escala absurda não vira margem absurda.
        HudPlacement.MarginFor(double.NaN).Should().Be(12);
        HudPlacement.MarginFor(0).Should().Be(12);
    }

    [Fact]
    public void ACustomPoint_IsKeptWhereTheUserDroppedIt()
    {
        var rect = HudPlacement.Resolve(
            HudPosition.Custom, Laptop, Card, Margin, new PixelPoint(700, 300), [Laptop]);

        rect.TopLeft.Should().Be(new PixelPoint(700, 300));
    }

    [Fact]
    public void ACustomPointOnAScreenThatIsGone_ComesBack()
    {
        // Arrastado para o monitor da esquerda, reaberto só com o notebook.
        var rect = HudPlacement.Resolve(
            HudPosition.Custom, Laptop, Card, Margin, new PixelPoint(-1800, 300), [Laptop]);

        Laptop.Contains(rect).Should().BeTrue();
    }

    [Fact]
    public void CustomWithoutAPoint_FallsBackToTopLeft()
    {
        HudPlacement.Resolve(HudPosition.Custom, Laptop, Card, Margin, custom: null, [Laptop])
            .TopLeft.Should().Be(new PixelPoint(Margin, Margin));
    }

    [Fact]
    public void OnATinyScreen_ItStillFits()
    {
        var tiny = new PixelRect(0, 0, 240, 160);

        var rect = Place(HudPosition.BottomRight, area: tiny);

        tiny.Contains(rect).Should().BeTrue();
    }
}
