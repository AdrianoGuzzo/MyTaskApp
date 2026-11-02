using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using MyTaskApp.Desktop.Terminals;
using SkiaSharp;

namespace MyTaskApp.Desktop.Tests.Terminals;

/// <summary>
/// O terminal do agente desenha na Cascadia Mono embutida, a do Windows
/// Terminal, que nem todo Windows tem instalada. Ela precisa ser de passo fixo:
/// numa fonte proporcional cada letra estreita sobra na célula do "M". O
/// headless não desenha com fonte de verdade; por isso a guarda olha os
/// arquivos embutidos.
/// </summary>
public sealed class TerminalFontTests
{
    [AvaloniaFact]
    public void TheTerminal_AsksForTheEmbeddedCascadiaMonoFirst()
    {
        TerminalView.DefaultFontFamily.FamilyNames.First().Should().Be("Cascadia Mono");
        new TerminalView().FontFamily.Should().Be(TerminalView.DefaultFontFamily);
    }

    [AvaloniaTheory]
    [InlineData("Regular", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright)]
    [InlineData("Bold", SKFontStyleWeight.Bold, SKFontStyleSlant.Upright)]
    [InlineData("Italic", SKFontStyleWeight.Normal, SKFontStyleSlant.Italic)]
    [InlineData("BoldItalic", SKFontStyleWeight.Bold, SKFontStyleSlant.Italic)]
    public void EveryStyle_IsTheSameMonospacedFamily(string file, SKFontStyleWeight weight, SKFontStyleSlant slant)
    {
        using var typeface = Load($"CascadiaMono-{file}.ttf");

        typeface.FamilyName.Should().Be("Cascadia Mono", "o nome depois do \"#\" tem de ser o da tabela name, senão o Avalonia não acha a família");
        typeface.FontWeight.Should().Be((int)weight);
        typeface.FontSlant.Should().Be(slant);

        using var font = new SKFont(typeface, 13);
        font.MeasureText("i").Should().Be(font.MeasureText("M"), "passo fixo: cada letra ocupa uma célula");
    }

    private static SKTypeface Load(string file)
    {
        var uri = new Uri($"avares://MyTaskApp/Assets/Fonts/{file}");
        using var stream = AssetLoader.Open(uri);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;

        return SKTypeface.FromStream(buffer)
            ?? throw new InvalidOperationException($"{uri} não é uma fonte válida");
    }
}
