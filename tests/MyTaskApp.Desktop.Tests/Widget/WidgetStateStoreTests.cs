using MyTaskApp.Desktop.Theming;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Tests.Widget;

/// <summary>
/// O arquivo que lembra onde o painel estava. O que importa aqui é o caminho
/// de degradação: nada disso pode impedir o app de abrir.
/// </summary>
public class WidgetStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MyTaskApp.WidgetState." + Guid.CreateVersion7().ToString("N"));

    private WidgetStateStore NewStore() =>
        new(NullLogger<WidgetStateStore>.Instance, _directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WithoutAFile_ItAnswersTheDefault()
    {
        NewStore().Load().Should().Be(WidgetState.Default);
    }

    [Fact]
    public void WhatIsSavedComesBack()
    {
        var saved = WidgetState.Default with
        {
            X = 1200,
            Y = 640,
            Width = 380,
            Height = 620,
            Topmost = true,
            Mode = WidgetMode.Compact,
            StartHidden = true,
        };

        NewStore().Save(saved);

        NewStore().Load().Should().Be(saved);
    }

    [Fact]
    public void SavingCreatesTheFolderItNeeds()
    {
        NewStore().Save(WidgetState.Default);

        File.Exists(Path.Combine(_directory, "widget.json")).Should().BeTrue();
    }

    [Fact]
    public void ACorruptFile_DegradesToTheDefaultInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "widget.json"), "{ isto não é json");

        NewStore().Load().Should().Be(WidgetState.Default);
    }

    [Fact]
    public void AFileEditedByHand_CannotProduceAZeroSizedPanel()
    {
        // Um zero aqui deixaria o painel invisível e sem forma de recuperar.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "widget.json"),
            """{ "Width": 0, "Height": 0 }""");

        var state = NewStore().Load();

        state.Width.Should().Be(WidgetMetrics.MinWidth);
        state.Height.Should().Be(WidgetMetrics.MinExpandedHeight);
    }

    [Fact]
    public void AnUnknownModeFallsBackToTheFullPanel()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "widget.json"),
            """{ "Mode": "Expanded", "Width": 360, "Height": 560 }""");

        NewStore().Load().Mode.Should().Be(WidgetMode.Expanded);
    }

    [Fact]
    public void TheChosenThemeComesBack()
    {
        NewStore().Save(WidgetState.Default with { Theme = "sepia" });

        NewStore().Load().Theme.Should().Be("sepia");
    }

    [Fact]
    public void AFileFromBeforeThemes_FollowsWindows()
    {
        // Quem atualiza o app tem um widget.json sem "Theme". Seguir o sistema
        // é o padrão novo — e no Windows escuro dá o mesmo Carvão de antes.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "widget.json"),
            """{ "Mode": "Expanded", "Width": 360, "Height": 560 }""");

        NewStore().Load().Theme.Should().Be(ThemeCatalog.SystemId);
    }

    [Theory]
    [InlineData("""{ "Theme": "tema-removido" }""")]
    [InlineData("""{ "Theme": null }""")]
    public void AThemeThatNoLongerExists_FollowsWindows(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "widget.json"), json);

        NewStore().Load().Theme.Should().Be(ThemeCatalog.SystemId);
    }
}

