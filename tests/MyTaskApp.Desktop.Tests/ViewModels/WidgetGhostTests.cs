using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Desktop.Widget;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Modo discreto: a moldura some, o fundo vira buraco e sobram as tarefas sobre
/// a área de trabalho. É o modo mais fácil de quebrar sem perceber — some
/// cabeçalho, some fundo, e com eles somem a barra de arrasto, o acesso ao menu
/// e o alvo do clique. Cada uma dessas perdas tem de ser compensada em outro
/// lugar, e é isso que estes testes cobram.
/// </summary>
public class WidgetGhostTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);

    private static TodayTask Row(string title) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false);

    private static async Task<MainWindow> ShowAsync(params TodayTask[] pending)
    {
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = new TodayBoard(Date, [], [], [.. pending], [], []) },
            new FakeConfirmationDialog(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Settle(window);

        return window;
    }

    /// <summary>Ver <c>WidgetPinTests.Settle</c>: o render forçado é o que faz o
    /// <c>InputHitTest</c> enxergar o layout novo depois da troca de modo.</summary>
    private static void Settle(MainWindow window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static Border Shell(Visual window) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("widgetShell"));

    private static Border TitleBar(Visual window) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("titleBar"));

    private static Border CaptureCard(Visual window) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Name == "CaptureCard");

    /// <summary>
    /// O fundo da linha tem BrushTransition de 120ms: ler a propriedade viva
    /// devolve um valor no meio da animação, que muda conforme o relógio de
    /// render avançou. O valor de base é o que o estilo pediu, sem a transição.
    /// </summary>
    private static byte BackgroundAlpha(Border border) =>
        (border.GetBaseValue(Border.BackgroundProperty).GetValueOrDefault() as ISolidColorBrush)
            ?.Color.A ?? 0;

    private static Button AddButton(Visual window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("ghostAdd"));

    private static Button PinButton(Visual window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("ghostChrome")
                && !button.Classes.Contains("ghostAdd"));

    [AvaloniaFact]
    public async Task ThePin_TurnsTheGhostOnWithIt()
    {
        // "Quando o pino estiver ativo ele fica o mais discreto possível": um
        // clique só entrega os dois.
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        Settle(window);

        window.Topmost.Should().BeTrue();
        window.Chrome.IsGhost.Should().BeTrue();
        window.Chrome.IsGhostActive.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task UnpinningTakesTheGhostWithIt()
    {
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        window.Chrome.ToggleTopmost();
        Settle(window);

        window.Topmost.Should().BeFalse();
        window.Chrome.IsGhost.Should().BeFalse();
        TitleBar(window).IsEffectivelyVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheGhostIsSeparableFromThePin()
    {
        // Ligados por padrão, mas não amarrados: dá para ficar fixado com a
        // moldura de volta, e discreto sem estar fixado.
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        window.Chrome.ToggleGhost();
        Settle(window);

        window.Topmost.Should().BeTrue();
        window.Chrome.IsGhost.Should().BeFalse();

        window.Chrome.ToggleGhost();
        Settle(window);

        window.Chrome.IsGhost.Should().BeTrue();
        window.Topmost.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheGhost_DropsTheHeaderAndTheFrame()
    {
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleGhost();
        Settle(window);

        TitleBar(window).IsEffectivelyVisible.Should().BeFalse();

        var shell = Shell(window);

        // O fundo vira buraco, mas continua recebendo ponteiro: é o que permite
        // arrastar por área vazia.
        shell.Classes.Should().Contain("ghost");
        BackgroundAlpha(shell).Should().Be(0);
        shell.BorderThickness.Should().Be(default(Thickness));

        WindowDecorationProperties.GetElementRole(shell)
            .Should().Be(WindowDecorationsElementRole.TitleBar);
    }

    [AvaloniaFact]
    public async Task TheGhost_KeepsTheItemsAndDropsEverythingElse()
    {
        // "Mostrando somente os itens": a tarefa continua desenhada, o cabeçalho
        // de seção e a dica de teclado não.
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleGhost();
        Settle(window);

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => block.Text!)
            .ToList();

        texts.Should().Contain("Deploy");
        texts.Should().NotContain("Checklist");
        texts.Should().NotContain("Enter adiciona · Shift+Enter quebra linha");
    }

    [AvaloniaFact]
    public async Task TheGhost_GivesEachRowItsOwnPillSoTheTextSurvivesAnyBackground()
    {
        // Sem fundo no painel, texto claro sobre janela clara sumiria. A pílula
        // por linha é o que mantém a lista legível sobre qualquer coisa.
        var window = await ShowAsync(Row("Deploy"));

        var row = window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("row"));

        // Fora do modo discreto a linha é um buraco: quem dá fundo é o painel.
        BackgroundAlpha(row).Should().Be(0);

        window.Chrome.ToggleGhost();
        Settle(window);

        // Dentro dele, translúcida: cobre o suficiente para o texto sobreviver a
        // um fundo claro sem virar um bloco opaco na frente da tela.
        BackgroundAlpha(row).Should().BeInRange(1, 254);
    }

    [AvaloniaFact]
    public async Task TheGhost_HasNoCaptureUntilThePlusAsksForIt()
    {
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleGhost();
        Settle(window);

        CaptureCard(window).IsEffectivelyVisible.Should().BeFalse();
        AddButton(window).IsEffectivelyVisible.Should().BeTrue();

        window.Chrome.ToggleCapture();
        Settle(window);

        CaptureCard(window).IsEffectivelyVisible.Should().BeTrue();

        // E o mesmo botão fecha.
        window.Chrome.ToggleCapture();
        Settle(window);

        CaptureCard(window).IsEffectivelyVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task TheGhost_KeepsThePinNextToThePlus_AsTheWayBack()
    {
        // Sem cabeçalho, o alfinete ao lado do "+" é o único caminho na janela
        // para desfazer o modo. Se ele sumir, sobra a bandeja — que ninguém
        // encontra sozinho.
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        Settle(window);

        var pin = PinButton(window);

        pin.IsEffectivelyVisible.Should().BeTrue();
        AddButton(window).IsEffectivelyVisible.Should().BeTrue();

        // Acesa: é ela que desfaz o modo, então não pode parecer desligada.
        pin.Classes.Should().Contain("on");

        // E fora da área de arrasto, senão clicar nela só sacudiria a janela.
        WindowDecorationProperties.GetElementRole(pin)
            .Should().Be(WindowDecorationsElementRole.User);
    }

    [AvaloniaFact]
    public async Task ThePinNextToThePlus_TakesTheFrameBack()
    {
        // A viagem de volta completa, pelo comando que o botão dispara.
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        Settle(window);

        var pin = PinButton(window);

        pin.Command.Should().BeSameAs(window.Chrome.ToggleTopmostCommand);

        pin.Command!.Execute(pin.CommandParameter);
        Settle(window);

        window.Topmost.Should().BeFalse();
        window.Chrome.IsGhost.Should().BeFalse();
        TitleBar(window).IsEffectivelyVisible.Should().BeTrue();

        // E os flutuantes somem junto com o modo.
        pin.IsEffectivelyVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task ThePlusButton_StaysOutOfTheDragArea()
    {
        // Sem isto o "+" ficaria dentro da área de arrasto do painel e clicar
        // nele apenas sacudiria a janela — exatamente o que acontecia com os
        // botões do cabeçalho quando o papel era "None".
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleGhost();
        Settle(window);

        WindowDecorationProperties.GetElementRole(AddButton(window))
            .Should().Be(WindowDecorationsElementRole.User);
    }

    [AvaloniaFact]
    public async Task OpeningTheCaptureFromThePlus_PutsTheCursorInIt()
    {
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleGhost();
        Settle(window);

        window.Chrome.ToggleCapture();
        Settle(window);

        window.GetVisualDescendants().OfType<TextBox>().Single()
            .IsFocused.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task LeavingTheGhost_ClosesTheCaptureItOpened()
    {
        // Voltar para a moldura com a caixa aberta deixaria duas capturas na
        // tela: a do cabeçalho e a do modo discreto.
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleGhost();
        window.Chrome.ToggleCapture();
        Settle(window);

        window.Chrome.IsCaptureOpen.Should().BeTrue();

        window.Chrome.ToggleGhost();
        Settle(window);

        window.Chrome.IsCaptureOpen.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Collapsed_IgnoresTheGhost_BecauseThePillWouldBeAllThatIsLeft()
    {
        // Recolhido o cabeçalho é a única coisa desenhada. Deixar o modo
        // discreto escondê-lo apagaria o widget da tela, sem forma de voltar a
        // não ser pela bandeja.
        var window = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleGhost();
        window.Chrome.UseMode("collapsed");
        Settle(window);

        window.Chrome.IsGhost.Should().BeTrue();
        window.Chrome.IsGhostActive.Should().BeFalse();
        TitleBar(window).IsEffectivelyVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public void TheGhostSurvivesARestart()
    {
        var store = new StubStore(WidgetState.Default with { Ghost = true, Topmost = true });
        var window = new MainWindow();

        window.Attach(store);
        window.Show();
        Settle(window);

        window.Chrome.IsGhost.Should().BeTrue();
        window.Topmost.Should().BeTrue();
        TitleBar(window).IsEffectivelyVisible.Should().BeFalse();
    }

    private sealed class StubStore(WidgetState state) : IWidgetStateStore
    {
        public WidgetState Load() => state;

        public void Save(WidgetState value)
        {
        }
    }
}
