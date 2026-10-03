using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using SkiaSharp;

namespace MyTaskApp.Desktop.Tests.Theming;

/// <summary>
/// Os ícones do painel são caracteres da área de uso privado da Segoe, que só
/// existe no Windows. Fora dele quem desenha é a fonte embutida — e, se ela não
/// tiver o código, o botão aparece vazio, em silêncio, e só na máquina de quem
/// usa Linux. Os testes rodam no Windows, onde a Segoe sempre responde; por isso
/// a guarda olha a fonte embutida diretamente, e não a tela.
/// </summary>
public partial class IconFontTests
{
    [AvaloniaFact]
    public void TheIconFontFallsBackToTheEmbeddedOne()
    {
        var font = (FontFamily)Avalonia.Application.Current!.FindResource("WidgetIconFont")!;

        // A Segoe responde primeiro: no Windows o painel continua igual.
        font.FamilyNames.First().Should().Be("Segoe Fluent Icons");
        font.FamilyNames.Last().Should().Be("MyTaskApp Icons");
    }

    [AvaloniaFact]
    public void TheEmbeddedFontIsTheFamilyTheUriNames()
    {
        // O nome depois do "#" tem de ser o da tabela name da fonte, senão o
        // Avalonia acha o arquivo e não acha a família.
        using var typeface = LoadEmbeddedFont();

        typeface.FamilyName.Should().Be("MyTaskApp Icons");
    }

    [AvaloniaFact]
    public void EveryIconTheScreensUse_HasAGlyphInTheEmbeddedFont()
    {
        using var typeface = LoadEmbeddedFont();

        var used = IconCodepoints().ToList();

        used.Should().NotBeEmpty("as telas usam ícones da Segoe; se a busca não acha nenhum, ela quebrou");
        used.Where(codepoint => typeface.GetGlyph(codepoint) == 0)
            .Select(codepoint => $"U+{codepoint:X4}")
            .Should().BeEmpty("ícone sem glifo na fonte embutida some fora do Windows — rode scripts/generate-icon-font.py");
    }

    private static SKTypeface LoadEmbeddedFont()
    {
        var uri = new Uri("avares://MyTaskApp/Assets/Fonts/MyTaskAppIcons.ttf");
        using var stream = AssetLoader.Open(uri);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;

        return SKTypeface.FromStream(buffer)
            ?? throw new InvalidOperationException($"{uri} não é uma fonte válida");
    }

    /// <summary>
    /// Todo código da área de uso privado que aparece nas telas e view models:
    /// literal, <c></c> em C# ou <c>&amp;#xE712;</c> em XAML.
    /// </summary>
    private static IEnumerable<int> IconCodepoints() =>
        Directory.EnumerateFiles(DesktopSources(), "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal))
            .SelectMany(file => CodepointsIn(File.ReadAllText(file)))
            .Distinct()
            .Order();

    private static IEnumerable<int> CodepointsIn(string text) =>
        text.Where(IsPrivateUse).Select(character => (int)character)
            .Concat(EscapedCodepoint().Matches(text)
                .Select(match => int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .Where(codepoint => IsPrivateUse((char)codepoint)));

    private static bool IsPrivateUse(char character) => character is >= '' and <= '';

    /// <summary>Sobe do binário até o <c>.slnx</c>, como os testes de packaging.</summary>
    private static string DesktopSources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyTaskApp.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("MyTaskApp.slnx não encontrado"),
            "src",
            "MyTaskApp.Desktop");
    }

    [GeneratedRegex(@"(?:\\u|&#x)([0-9A-Fa-f]{4})")]
    private static partial Regex EscapedCodepoint();
}
