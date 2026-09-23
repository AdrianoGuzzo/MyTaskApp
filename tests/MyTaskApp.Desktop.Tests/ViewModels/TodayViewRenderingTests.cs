using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Binding de XAML só falha em runtime. Estes testes sobem a janela de verdade
/// (sem display) para garantir que a tela desenha os dados e que o comando do
/// checkbox realmente resolve.
/// </summary>
public class TodayViewRenderingTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);

    private static TodayTask Row(string title, TimeOnly? time = null, TimeSpan? waiting = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, time, false, waiting);

    private static async Task<MainWindow> ShowAsync(TodayBoard board)
    {
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = board },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
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
            .Select(block => block.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToList();

    [AvaloniaFact]
    public async Task Window_ShowsTheHeaderAndTheSectionsWithTheirTasks()
    {
        var window = await ShowAsync(new TodayBoard(
            Date,
            Overdue: [Row("Revisar documentação")],
            Now: [],
            Today: [Row("Deploy", new TimeOnly(15, 30))],
            Unscheduled: [],
            Completed: []));

        var texts = VisibleTexts(window);

        texts.Should().Contain("HOJE — 17/09/2026");
        texts.Should().Contain("ATRASADAS");
        texts.Should().Contain("Revisar documentação");
        texts.Should().Contain("Deploy");
        texts.Should().Contain("15:30");
    }

    [AvaloniaFact]
    public async Task Window_DoesNotRenderHeadersForEmptySections()
    {
        var window = await ShowAsync(new TodayBoard(
            Date, [], [], [Row("Deploy", new TimeOnly(15, 30))], [], []));

        VisibleTexts(window).Should().NotContain("ATRASADAS").And.NotContain("CONCLUÍDAS");
    }

    [AvaloniaFact]
    public async Task EmptyDay_ShowsTheReassuringMessage()
    {
        var window = await ShowAsync(new TodayBoard(Date, [], [], [], [], []));

        VisibleTexts(window).Should().Contain("Nada para hoje. Aproveite.");
    }

    [AvaloniaFact]
    public async Task LongTitle_KeepsTheWholeTextInTheToolTip()
    {
        // Na coluna estreita o título sai cortado com "…". O balão é o único
        // caminho de volta ao texto inteiro — se o Tip sumir, some com ele.
        const string title =
            "Revisar o documento de arquitetura e conferir com a plataforma se a " +
            "migração do banco cabe na janela de manutenção do próximo sábado";

        var window = await ShowAsync(new TodayBoard(
            Date, [], [], [Row(title)], [], []));

        var titleBlock = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(block => block.Classes.Contains("taskTitle"));

        ToolTip.SetIsOpen(titleBlock, true);
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var tip = ToolTip.GetTip(titleBlock).Should().BeOfType<ToolTip>().Subject;
        tip.GetVisualDescendants().OfType<TextBlock>()
            .Should().Contain(block => block.Text == title);

        ToolTip.SetIsOpen(titleBlock, false);
    }

    [AvaloniaFact]
    public async Task AttentionLabel_AlsoCarriesTheWholeTextInTheToolTip()
    {
        var waiting = TimeSpan.FromMinutes(40);
        var expected = TaskRowViewModel.DescribeWait(waiting);

        var window = await ShowAsync(new TodayBoard(
            Date, [], [], [Row("Deploy", new TimeOnly(15, 30), waiting)], [], []));

        var label = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(block => block.Text == expected);

        ToolTip.GetTip(label).Should().Be(expected);
    }

    [AvaloniaFact]
    public async Task EachRowCheckbox_IsWiredToTheToggleCommand()
    {
        // O binding usa um caminho com cast até o DataContext do UserControl;
        // se ele quebrar, o checkbox vira enfeite e nada avisa.
        var window = await ShowAsync(new TodayBoard(
            Date, [], [], [Row("Deploy", new TimeOnly(15, 30))], [], []));

        var checkBox = window.GetVisualDescendants().OfType<CheckBox>().Single();

        checkBox.Command.Should().NotBeNull();
        checkBox.CommandParameter.Should().BeOfType<TaskRowViewModel>();
    }
}
