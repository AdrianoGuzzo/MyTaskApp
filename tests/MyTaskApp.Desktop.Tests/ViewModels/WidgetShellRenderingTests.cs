using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A moldura do widget desenhada de verdade. Mesma razão dos outros testes de
/// tela: binding de XAML só falha em runtime, e aqui o cabeçalho liga em
/// <c>#Shell.Chrome</c> — um caminho que o compilador aceita e que só quebra
/// quando alguém abre o app.
/// </summary>
public class WidgetShellRenderingTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);

    private static TodayTask Row(
        string title,
        TaskPriority priority = TaskPriority.Normal) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, priority, Date, null, false);

    private static async Task<MainWindow> ShowAsync(TodayBoard board)
    {
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = board },
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Visual window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsVisible && !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => block.Text!)
            .ToList();

    private static IReadOnlyList<Border> PriorityBars(Visual window) =>
        window.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("priority"))
            .ToList();

    [AvaloniaFact]
    public async Task TheHeaderShowsTheBrandTheDateAndTheProgress()
    {
        var window = await ShowAsync(new TodayBoard(
            Date, [], [], [Row("Deploy")], [], [Row("Relatório")]));

        var texts = VisibleTexts(window);

        texts.Should().Contain("Checklist");
        texts.Should().Contain("HOJE — 17/09/2026");
        texts.Should().Contain("1 de 2 concluídas");
    }

    [AvaloniaFact]
    public async Task TheProgressBarTracksTheBoard()
    {
        var window = await ShowAsync(new TodayBoard(
            Date, [], [], [Row("Deploy")], [], [Row("Relatório")]));

        var bar = window.GetVisualDescendants().OfType<ProgressBar>().Single();

        bar.Value.Should().Be(50);
        bar.IsVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task AnEmptyDay_HidesTheProgressBar()
    {
        var window = await ShowAsync(new TodayBoard(Date, [], [], [], [], []));

        window.GetVisualDescendants().OfType<ProgressBar>().Single()
            .IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task ThePriorityBar_OnlyLightsUpAboveNormal()
    {
        var window = await ShowAsync(new TodayBoard(
            Date,
            [],
            [],
            [
                Row("Corrigir produção", TaskPriority.Urgent),
                Row("Revisar PR", TaskPriority.High),
                Row("Atualizar documentação"),
            ],
            [],
            []));

        var bars = PriorityBars(window);

        bars.Should().HaveCount(3);
        bars[0].Classes.Should().Contain("urgent");
        bars[1].Classes.Should().Contain("high");
        bars[2].Classes.Should().NotContain("urgent").And.NotContain("high");
    }

    [AvaloniaFact]
    public async Task Collapsed_ThePillShowsWhatIsLeftAndHidesTheList()
    {
        var window = await ShowAsync(new TodayBoard(
            Date, [], [], [Row("Deploy"), Row("Revisar")], [], []));

        window.Chrome.UseMode("collapsed");
        window.UpdateLayout();

        VisibleTexts(window).Should().Contain("2 tarefas pendentes");

        // A lista continua montada; ela apenas não ocupa mais a tela.
        window.GetVisualDescendants().OfType<TodayView>().Single()
            .IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Compact_DropsTheCaptureBoxButKeepsItInTheTree()
    {
        // Os testes de captura contam exatamente um TextBox na janela: se o
        // modo compacto o removesse do visual tree, eles quebrariam junto.
        var window = await ShowAsync(new TodayBoard(Date, [], [], [Row("Deploy")], [], []));

        window.Chrome.UseMode("compact");
        window.UpdateLayout();

        var capture = window.GetVisualDescendants().OfType<TextBox>().Single();

        capture.Should().NotBeNull();
        window.GetVisualDescendants().OfType<TodayView>().Single()
            .Classes.Should().Contain("compact");
        window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Name == "CaptureCard")
            .IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task TheChromeButtons_AreWiredToTheirCommands()
    {
        var window = await ShowAsync(new TodayBoard(Date, [], [], [], [], []));

        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("chrome"))
            .ToList();

        buttons.Should().HaveCount(3);
        buttons.Where(button => button.Command is not null).Should().HaveCount(2);
    }

    [AvaloniaFact]
    public async Task TheChromeButtons_ReceiveInputInsideTheTitleBar()
    {
        // Os três botões vivem dentro do Border marcado como TitleBar. "None"
        // quer dizer "invisível ao hit-test do chrome": o clique subiria para a
        // área de arrasto e o botão ficaria morto na máquina do usuário —
        // apertar o alfinete só sacudiria a janela. Quem recebe clique dentro da
        // barra é "User".
        //
        // O headless não faz hit-test não-cliente, então o papel não tem efeito
        // nenhum aqui: é por isso que este teste olha o papel declarado, e não
        // simula um clique. Um clique simulado passaria com "None" e com "User".
        var window = await ShowAsync(new TodayBoard(Date, [], [], [], [], []));

        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("chrome"))
            .ToList();

        buttons.Should().HaveCount(3);
        buttons.Should().OnlyContain(button =>
            WindowDecorationProperties.GetElementRole(button)
                == WindowDecorationsElementRole.User);
    }

    [AvaloniaFact]
    public async Task TheTopmostToggle_ReachesTheWindow()
    {
        var window = await ShowAsync(new TodayBoard(Date, [], [], [], [], []));

        window.Topmost.Should().BeFalse();

        window.Chrome.ToggleTopmost();

        window.Topmost.Should().BeTrue();
    }

    [AvaloniaFact]
    public void WithoutAStore_TheWindowStillOpensAndForgetsEverything()
    {
        // É como os testes headless a constroem, e como o designer a carrega.
        var window = new MainWindow();

        window.Show();
        window.PersistNow();

        window.Chrome.Mode.Should().Be(MyTaskApp.Desktop.Widget.WidgetMode.Expanded);
    }
}
