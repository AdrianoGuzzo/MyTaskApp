using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O prazo desenhado (ADR-050). Binding de XAML falha em runtime, e uma linha
/// de prazo que não aparece — ou um botão de "+1 dia" sem comando — passaria
/// pela build sem ninguém notar.
/// </summary>
public class DeadlineRenderingTests
{
    private static readonly DateOnly Date = new(2026, 10, 6);
    private static readonly TaskDeadline Friday = new(new DateOnly(2026, 10, 9), new TimeOnly(18, 0));

    private static TaskDeadlineView View(DeadlineSeverity severity, string label) =>
        new(Friday, DeadlineStatus.DueSoon, severity, TimeSpan.FromHours(30), label, "sex 09/10 18:00", "1 dia e 6 horas restantes");

    private static TodayTask Task(string title, TaskDeadlineView? deadline) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false,
            Deadline: deadline, NextAction: deadline is null ? null : "Criar endpoint POST /diets");

    private static async Task<MainWindow> ShowListAsync(TodayBoard board)
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

    private static StackPanel DeadlineLine(Visual window) =>
        window.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "DeadlineLine");

    [AvaloniaFact]
    public async Task ARowWithADeadline_DrawsTheLabelUnderTheTitle_WithItsSeverityClass()
    {
        var window = await ShowListAsync(
            new TodayBoard(Date, [], [], [], [], [])
            {
                Deadlines = [Task("Integração Jira", View(DeadlineSeverity.Attention, "ATENÇÃO · vence amanhã às 18:00"))],
            });

        var line = DeadlineLine(window);

        line.IsVisible.Should().BeTrue();
        line.Classes.Should().Contain("attention");
        line.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)
            .Should().Contain("ATENÇÃO · vence amanhã às 18:00");
        ToolTip.GetTip(line).Should().BeOfType<string>()
            .Which.Should().Contain("Próxima ação: Criar endpoint POST /diets");
    }

    [AvaloniaFact]
    public async Task ARowWithoutDeadline_DrawsNothingExtra()
    {
        var window = await ShowListAsync(new TodayBoard(Date, [], [], [], [Task("Comprar pão", null)], []));

        DeadlineLine(window).IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public void TheTaskWindow_ShowsTheDeadlineCard_WithEveryCommandResolved()
    {
        var runner = new FakeUseCaseRunner();
        var deadline = new TaskDeadlineViewModel(runner, NullLogger<TaskDeadlineViewModel>.Instance);
        var notes = new TaskNotesViewModel(
            runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(new FakeUseCaseRunner()),
            NullLogger<TaskNotesViewModel>.Instance,
            deadline: deadline);

        notes.Load(new TaskRowViewModel(Task("Implementar módulo", View(DeadlineSeverity.Urgent, "URGENTE")), isCompleted: false));
        deadline.BeginEdit();

        var window = new TaskNotesWindow(notes, new FakeConfirmationDialog());
        window.Show();
        window.UpdateLayout();

        var section = window.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "DeadlineSection");
        var texts = section.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        // Só os botões do card (todos "small"): os do calendário, do relógio e
        // do NumericUpDown são partes dos templates deles, sem comando.
        var buttons = section.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && button.Classes.Contains("small"))
            .ToList();

        section.IsVisible.Should().BeTrue();
        texts.Should().Contain("sex 09/10 18:00").And.Contain("· 1 dia e 6 horas restantes");
        buttons.Select(button => button.Content).Should().Contain(["Alterar", "Remover", "Amanhã", "Final da semana", "Aplicar"]);
        buttons.Should().AllSatisfy(button => button.Command.Should().NotBeNull());
    }

    [AvaloniaFact]
    public void TheDeadlineAlert_DrawsWhatIsDue_AndEveryButtonHasACommand()
    {
        var viewModel = new DeadlineAlertViewModel(new FakeUseCaseRunner(), NullLogger<DeadlineAlertViewModel>.Instance);
        viewModel.Show(new DeadlineAlert(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Implementar integração Jira",
            "Prazo amanhã",
            "Vence amanhã às 18:00.",
            DeadlineAlertStage.OneDay,
            DeadlineSeverity.Attention,
            IsUrgent: false));

        var window = new DeadlineAlertWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

        texts.Should().Contain(["Prazo amanhã", "Implementar integração Jira", "Vence amanhã às 18:00."]);
        buttons.Select(button => button.Content).Should().Equal("Abrir", "+1 dia", "Adiar 1 h", "OK");
        buttons.Should().AllSatisfy(button => button.Command.Should().NotBeNull());
    }
}
