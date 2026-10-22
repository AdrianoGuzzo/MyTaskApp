using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Consultations;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A aba Consulta Inteligente na janela da tarefa de verdade (ADR-081). Pega
/// o que só quebra na tela: a aba que não aparece, um botão sem comando, um
/// cartão amarrado no lugar errado.
/// </summary>
public class TaskConsultationRenderingTests
{
    private static async Task<(TaskNotesWindow Window, TaskNotesViewModel ViewModel, FakeUseCaseRunner Runner)> ShowAsync(TaskConsultationView view)
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
        runner.ResultsByHandler[typeof(GetTaskConsultationHandler)] = view;
        runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = TaskConsultationViewModelTests.Claude();
        runner.ResultsByHandler[typeof(CheckConsultationDatabaseHandler)] =
            new ConsultationDatabaseCheck(true, "14.11", "mytaskapp_leitura", true, [], false, "Acesso de leitura confirmado.");

        var confirmation = new FakeConfirmationDialog();
        var consultation = new TaskConsultationViewModel(
            runner, confirmation, TestDevelopment.Database(runner), NullLogger<TaskConsultationViewModel>.Instance);

        var viewModel = new TaskNotesViewModel(
            runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(runner, timeProvider: new FakeTimeProvider()),
            NullLogger<TaskNotesViewModel>.Instance,
            consultation: consultation);

        viewModel.Load(TaskNotesAliasTests.Row());

        var window = new TaskNotesWindow(viewModel, confirmation);
        window.Show();
        Settle(window);

        viewModel.SelectedTabIndex = TaskNotesViewModel.ConsultationTab;
        await consultation.ActivateAsync(CancellationToken.None);
        Settle(window);

        return (window, viewModel, runner);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static IEnumerable<string?> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text);

    [AvaloniaFact]
    public async Task TheTab_IsTheFourth_AndShowsTheFiveCards()
    {
        var (window, _, _) = await ShowAsync(TaskConsultationViewModelTests.View());

        Named<TabItem>(window, "ConsultationTabItem").IsVisible.Should().BeTrue();
        Named<TabItem>(window, "ConsultationTabItem").IsSelected.Should().BeTrue();

        var section = Named<StackPanel>(window, "ConsultationSection");
        Texts(section).Should().Contain(["1. Assunto", "2. Projeto", "3. Dados", "4. Preparar", "5. Perguntar"]);
        Texts(section).Should().Contain(["API", "Web"]);
    }

    [AvaloniaFact]
    public async Task Start_TurnsOn_WhenThereIsAQuestion()
    {
        var (window, viewModel, _) = await ShowAsync(TaskConsultationViewModelTests.View());
        var start = Named<Button>(window, "StartConsultationButton");

        start.IsEffectivelyEnabled.Should().BeFalse();
        Named<TextBlock>(window, "StartBlockedText").Text.Should().Be("Escreva a pergunta.");

        Named<TextBox>(window, "QuestionBox").Text = "Por que o pedido 1234 está pendente?";
        Settle(window);

        start.IsEffectivelyEnabled.Should().BeTrue();
        viewModel.Consultation!.Question.Should().Be("Por que o pedido 1234 está pendente?");
    }

    [AvaloniaFact]
    public async Task AFolderNotCloned_OffersToClone()
    {
        var notCloned = TaskConsultationViewModelTests.Source("Pagamentos", ConsultationSourceState.NotCloned, selected: false)
            with { RepositoryUrl = "https://github.com/empresa/pagamentos.git" };

        var (window, _, _) = await ShowAsync(TaskConsultationViewModelTests.View(sources: [TaskConsultationViewModelTests.Source("API"), notCloned]));

        var list = Named<ItemsControl>(window, "ConsultationSources");
        Texts(list).Should().Contain("Não clonado neste computador");
        list.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible).Select(button => button.Content)
            .Should().Contain("Clonar…");
    }

    [AvaloniaFact]
    public async Task WithTheDatabase_ItShowsTheServer_AndTheCheck_NeverTheUser()
    {
        var (window, _, _) = await ShowAsync(TaskConsultationViewModelTests.View(database: TaskConsultationViewModelTests.Selected()));

        Named<RadioButton>(window, "DatabaseOption").IsChecked.Should().BeTrue();
        Named<TextBlock>(window, "DatabaseServer").Text.Should().Contain("localhost:5432/loja_copia");
        Named<TextBlock>(window, "DatabaseCheckText").Text.Should().Be("Acesso de leitura confirmado.");
        Named<TextBlock>(window, "McpHint").IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task WithTheMcpServerOff_ItSaysHowToTurnItOn()
    {
        var (window, _, _) = await ShowAsync(
            TaskConsultationViewModelTests.View(database: TaskConsultationViewModelTests.Selected(), toolsAvailable: false));

        Named<TextBlock>(window, "McpHint").IsEffectivelyVisible.Should().BeTrue();
        Named<Button>(window, "OpenMcpButton").IsEffectivelyVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task AnOpenConsultation_OffersTheTerminal()
    {
        var running = new AgentSessionView(Guid.CreateVersion7(), TaskConsultationViewModelTests.TaskId, "claude-code", "Claude Code",
            @"C:\claude.exe", @"C:\Projetos\API", 4242, DateTimeOffset.UtcNow, null, MyTaskApp.Domain.Agents.AgentSessionStatus.Running, null);

        var (window, _, _) = await ShowAsync(TaskConsultationViewModelTests.View(active: running));

        Named<Button>(window, "GoToTerminalButton").IsEffectivelyVisible.Should().BeTrue();
        Named<TextBlock>(window, "ConsultationStatus").Text.Should().Contain("terminal");
    }
}
