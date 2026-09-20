using Avalonia;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Tests.Widget;

/// <summary>
/// "O widget nunca abre fora da tela" é uma promessa que só quebra em cima da
/// mesa de alguém: desconectar o monitor e reabrir o app. Aqui as telas são
/// retângulos, então o caso dá para testar sem nenhum monitor.
/// </summary>
public class WidgetPlacementTests
{
    private static readonly PixelRect Laptop = new(0, 0, 1920, 1040);

    /// <summary>Segundo monitor à esquerda: coordenadas negativas são normais.</summary>
    private static readonly PixelRect Secondary = new(-2560, 0, 2560, 1400);

    [Fact]
    public void ARectangleAlreadyInsideAScreen_IsLeftAlone()
    {
        var desired = new PixelRect(400, 300, 360, 560);

        WidgetPlacement.Clamp(desired, [Laptop]).Should().Be(desired);
    }

    [Fact]
    public void ARectangleOnADisconnectedScreen_ComesBackToAConnectedOne()
    {
        // Fechou no monitor da esquerda; reabriu só com o notebook.
        var orphan = new PixelRect(-1800, 200, 360, 560);

        var placed = WidgetPlacement.Clamp(orphan, [Laptop]);

        Laptop.Contains(placed).Should().BeTrue();
    }

    [Fact]
    public void WithSeveralScreens_ItStaysOnTheOneItOverlapsMost()
    {
        var onSecondary = new PixelRect(-1800, 200, 360, 560);

        var placed = WidgetPlacement.Clamp(onSecondary, [Laptop, Secondary]);

        placed.Should().Be(onSecondary);
    }

    [Fact]
    public void HangingOffTheRightEdge_IsPushedBackIn()
    {
        var hanging = new PixelRect(1800, 300, 360, 560);

        var placed = WidgetPlacement.Clamp(hanging, [Laptop]);

        placed.Right.Should().Be(Laptop.Right);
        placed.Width.Should().Be(360);
    }

    [Fact]
    public void TallerThanTheScreen_ItShrinksInsteadOfHangingOff()
    {
        // Empurrar sem encolher deixaria a borda de baixo inalcançável, e com
        // ela o botão de redimensionar.
        var small = new PixelRect(0, 0, 800, 400);
        var tall = new PixelRect(0, 0, 360, 560);

        var placed = WidgetPlacement.Clamp(tall, [small]);

        placed.Height.Should().Be(400);
        small.Contains(placed).Should().BeTrue();
    }

    [Fact]
    public void WithoutAnyScreen_ItGivesUpQuietlyInsteadOfThrowing()
    {
        var desired = new PixelRect(10, 10, 360, 560);

        WidgetPlacement.Clamp(desired, []).Should().Be(desired);
    }

    [Fact]
    public void TheFirstRun_LandsNearTheClock()
    {
        var placed = WidgetPlacement.DefaultCorner(Laptop, new PixelSize(360, 560));

        placed.Right.Should().Be(Laptop.Right - WidgetMetrics.ScreenMargin);
        placed.Bottom.Should().Be(Laptop.Bottom - WidgetMetrics.ScreenMargin);
    }

    [Fact]
    public void TheFirstRunOnATinyScreen_StillFits()
    {
        var netbook = new PixelRect(0, 0, 320, 400);

        var placed = WidgetPlacement.DefaultCorner(netbook, new PixelSize(360, 560));

        netbook.Contains(placed).Should().BeTrue();
    }
}
