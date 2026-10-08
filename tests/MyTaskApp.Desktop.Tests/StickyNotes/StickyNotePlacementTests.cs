using Avalonia;
using MyTaskApp.Desktop.StickyNotes;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>Onde um post-it pousa — nunca fora da tela, mesmo sem o monitor de onde veio.</summary>
public class StickyNotePlacementTests
{
    private static readonly PixelRect Laptop = new(0, 0, 1920, 1040);

    /// <summary>Segundo monitor à esquerda: coordenadas negativas são normais.</summary>
    private static readonly PixelRect Secondary = new(-2560, 0, 2560, 1400);

    private static readonly PixelSize Size = new(280, 200);

    [Fact]
    public void TheFirstNewNote_LandsInTheTopRightCorner()
    {
        var placed = StickyNotePlacement.ForNew(Laptop, Size, index: 0, scaling: 1);

        placed.Should().Be(new PixelRect(1920 - 280 - 24, 24, 280, 200));
    }

    [Fact]
    public void TheNextNotes_CascadeSoTheyDoNotCoverEachOther()
    {
        var first = StickyNotePlacement.ForNew(Laptop, Size, index: 0, scaling: 1);
        var second = StickyNotePlacement.ForNew(Laptop, Size, index: 1, scaling: 1);

        second.X.Should().Be(first.X - 28);
        second.Y.Should().Be(first.Y + 28);
    }

    [Fact]
    public void TheCascade_StartsOverInsteadOfCrossingTheScreen()
    {
        StickyNotePlacement.ForNew(Laptop, Size, index: StickyNotePlacement.CascadeLength, scaling: 1)
            .Should().Be(StickyNotePlacement.ForNew(Laptop, Size, index: 0, scaling: 1));
    }

    [Fact]
    public void TheMargins_FollowTheScreenScale()
    {
        var size = StickyNotePlacement.ToPixels(280, 200, 1.5);

        var placed = StickyNotePlacement.ForNew(Laptop, size, index: 1, scaling: 1.5);

        size.Should().Be(new PixelSize(420, 300));
        placed.X.Should().Be(1920 - 420 - 36 - 42);
        placed.Y.Should().Be(36 + 42);
    }

    [Fact]
    public void OnATinyScreen_TheNoteShrinksToFit()
    {
        var tiny = new PixelRect(0, 0, 240, 160);

        var placed = StickyNotePlacement.ForNew(tiny, Size, index: 0, scaling: 1);

        tiny.Contains(placed).Should().BeTrue();
    }

    [Fact]
    public void ARestoredNote_StaysWhereItWas()
    {
        var placed = StickyNotePlacement.Restore(new PixelPoint(-1800, 300), Size, [Laptop, Secondary]);

        placed.Should().Be(new PixelRect(-1800, 300, 280, 200));
    }

    [Fact]
    public void ANoteFromAMonitorThatIsGone_ComesBackToAConnectedOne()
    {
        var placed = StickyNotePlacement.Restore(new PixelPoint(-1800, 300), Size, [Laptop]);

        Laptop.Contains(placed).Should().BeTrue();
    }
}
