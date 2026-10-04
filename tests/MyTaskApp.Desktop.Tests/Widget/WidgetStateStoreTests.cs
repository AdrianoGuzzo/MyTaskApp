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

    [Fact]
    public void TheWindowAndHudSettingsComeBack_AfterARestart()
    {
        // Configuração salva → app reinicia → configuração restaurada (ADR-048).
        var saved = WidgetState.Default with
        {
            X = 100,
            Y = 80,
            WindowMode = WindowMode.HudCollapsed,
            CloseBehavior = CloseBehavior.Hud,
            StartInHud = true,
            GlobalHotkey = true,
            Hud = new HudSettings
            {
                Position = HudPosition.Custom,
                Size = HudSize.Normal,
                Opacity = 0.85,
                AlwaysOnTop = false,
                UseCollapsed = true,
                X = -1500,
                Y = 40,
                IntroSeen = true,
            },
        };

        NewStore().Save(saved);

        NewStore().Load().Should().Be(saved);
    }

    [Fact]
    public void TheFileReadsLikeTheSettingsScreen()
    {
        // Enum por nome, e o HUD aninhado: quem abrir o widget.json entende.
        NewStore().Save(WidgetState.Default with { CloseBehavior = CloseBehavior.Hud });

        var json = File.ReadAllText(Path.Combine(_directory, "widget.json"));

        json.Should().Contain("\"CloseBehavior\": \"Hud\"")
            .And.Contain("\"Hud\": {")
            .And.Contain("\"Position\": \"TopLeft\"")
            .And.NotContain("Ghost");
    }

    [Fact]
    public void AFileFromBeforeTheHud_KeepsTheOldBehaviour()
    {
        // Quem atualiza não tem nada disso no arquivo: o X continua escondendo
        // na bandeja e a janela abre normal.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "widget.json"),
            """{ "Mode": "Compact", "Width": 360, "Height": 560, "Topmost": true }""");

        var state = NewStore().Load();

        state.WindowMode.Should().Be(WindowMode.Normal);
        state.CloseBehavior.Should().Be(CloseBehavior.Tray);
        state.Topmost.Should().BeTrue();
        state.Hud.Should().Be(HudSettings.Default);
    }

    [Fact]
    public void TheOldPinWithTheDiscreetMode_ReopensAsTheHud()
    {
        // O pino antigo era "fica no canto, por cima, sem atrapalhar" — o HUD.
        // Reabrir como janela grande fixada no topo seria o pior dos dois.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "widget.json"),
            """{ "Width": 360, "Height": 560, "Topmost": true, "Ghost": true }""");

        var state = NewStore().Load();

        state.WindowMode.Should().Be(WindowMode.Hud);
        state.Topmost.Should().BeFalse();
        state.Ghost.Should().BeNull();
    }

    [Fact]
    public void TheDiscreetModeWithoutThePin_IsSimplyDropped()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "widget.json"),
            """{ "Width": 360, "Height": 560, "Ghost": true }""");

        var state = NewStore().Load();

        state.WindowMode.Should().Be(WindowMode.Normal);
        state.Ghost.Should().BeNull();
    }

    [Theory]
    [InlineData("""{ "Hud": { "Opacity": 0.05 } }""", HudSettings.MinOpacity)]
    [InlineData("""{ "Hud": { "Opacity": 3 } }""", 1)]
    [InlineData("""{ "Hud": null }""", HudSettings.DefaultOpacity)]
    public void AHandEditedHud_CannotBecomeInvisible(string json, double opacity)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "widget.json"), json);

        NewStore().Load().Hud.Opacity.Should().Be(opacity);
    }

    [Fact]
    public void UnknownValues_FallBackToTheDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "widget.json"),
            """{ "WindowMode": 9, "CloseBehavior": 7, "Hud": { "Position": 99, "Size": 12 } }""");

        var state = NewStore().Load();

        state.WindowMode.Should().Be(WindowMode.Normal);
        state.CloseBehavior.Should().Be(CloseBehavior.Tray);
        state.Hud.Position.Should().Be(HudPosition.TopLeft);
        state.Hud.Size.Should().Be(HudSize.Compact);
    }
}
