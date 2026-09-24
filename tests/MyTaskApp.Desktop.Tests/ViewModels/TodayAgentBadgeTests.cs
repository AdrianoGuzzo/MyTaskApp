using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O selo "● Claude Code" da linha leva ao terminal do agente sem abrir a
/// tarefa (ADR-030). Com um agente por repositório, o selo conta e pergunta qual
/// (ADR-031).
/// </summary>
public class TodayAgentBadgeTests
{
    private static readonly DateOnly Date = new(2026, 9, 23);

    private readonly FakeUseCaseRunner _runner = new()
    {
        Result = new TodayBoard(Date, [], [], [], [], []),
    };

    private TodayViewModel ViewModel() =>
        new(_runner, new FakeConfirmationDialog(), new FakeClipboardWriter(), TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

    private static TodayTask Listed(string title = "Integrar o Claude", int agents = 1) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false,
            ActiveAgents: [.. Enumerable.Range(0, agents).Select(index => new ActiveAgent(
                Guid.CreateVersion7(), "Claude Code", index == 0 ? "eco-core" : "eco-api", "feature/x"))]);

    private static AgentFocusResult Focus(AgentSessionStatus status, bool focused) =>
        new(new AgentSessionView(Guid.CreateVersion7(), Guid.CreateVersion7(), "claude-code", "Claude Code",
                @"C:\claude.exe", @"C:\Projects\worktree", 15432, DateTimeOffset.UtcNow,
                status == AgentSessionStatus.Exited ? DateTimeOffset.UtcNow : null, status, null),
            focused);

    [Fact]
    public async Task Clicking_BringsTheTerminalForward_Silently()
    {
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] = Focus(AgentSessionStatus.Running, true);
        var viewModel = ViewModel();

        await viewModel.FocusAgentAsync(new TaskRowViewModel(Listed(), false));

        _runner.Invoked.Should().Equal(typeof(FocusAgentSessionHandler));
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public void OneAgentPerRepository_TheBadgeCountsThem_AndTheTipNamesThem()
    {
        var row = new TaskRowViewModel(Listed(agents: 2), false);

        row.HasActiveAgent.Should().BeTrue();
        row.AgentLabel.Should().Be("● Claude Code ×2");
        row.AgentTip.Should().Contain("eco-core · feature/x").And.Contain("eco-api · feature/x");
        new TaskRowViewModel(Listed(), false).AgentLabel.Should().Be("● Claude Code");
    }

    [Fact]
    public async Task ChoosingAnAgentFromTheMenu_FocusesIt()
    {
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] = Focus(AgentSessionStatus.Running, true);
        var viewModel = ViewModel();
        var row = new TaskRowViewModel(Listed(agents: 2), false);

        await viewModel.FocusAgentOfAsync(row.Agents[1]);

        _runner.Invoked.Should().Equal(typeof(FocusAgentSessionHandler));
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Clicking_WhenTheWindowIsNotFound_SaysWhereToLook()
    {
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] = Focus(AgentSessionStatus.Running, false);
        var viewModel = ViewModel();

        await viewModel.FocusAgentAsync(new TaskRowViewModel(Listed(), false));

        viewModel.ErrorMessage.Should().Contain("barra de tarefas");
    }

    /// <summary>
    /// O selo estava desatualizado: o quadro é recarregado para ele sumir, e o
    /// aviso vem depois da recarga para não ser apagado por ela.
    /// </summary>
    [Fact]
    public async Task Clicking_OnAnAgentThatAlreadyExited_ReloadsAndSaysSo()
    {
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] = Focus(AgentSessionStatus.Exited, false);
        var viewModel = ViewModel();

        await viewModel.FocusAgentAsync(new TaskRowViewModel(Listed(), false));

        _runner.Invoked.Should().Contain(typeof(GetTodayBoardHandler));
        viewModel.StatusMessage.Should().Be("O Claude Code desta tarefa já foi encerrado.");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Clicking_WhenTheUseCaseRefuses_ShowsItsMessage()
    {
        _runner.FailuresByHandler[typeof(FocusAgentSessionHandler)] =
            new DomainException("Esta tarefa não tem sessão de agente.");
        var viewModel = ViewModel();

        await viewModel.FocusAgentAsync(new TaskRowViewModel(Listed(), false));

        viewModel.ErrorMessage.Should().Be("Esta tarefa não tem sessão de agente.");
    }

    /// <summary>
    /// O clique é um handler de code-behind ligado por nome no XAML: se o nome
    /// mudar de um lado só, o selo continua desenhado e não faz nada.
    /// </summary>
    [AvaloniaFact]
    public async Task ASingleClickOnTheBadge_FocusesTheAgent()
    {
        _runner.Result = new TodayBoard(Date, [], [], [Listed()], [], []);
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] = Focus(AgentSessionStatus.Running, true);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var badge = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(block => block.Name == "AgentBadge");

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var centre = new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2);
        var point = badge.TranslatePoint(centre, window)!.Value;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        _runner.Invoked.Should().Contain(typeof(FocusAgentSessionHandler));
    }

    /// <summary>Com dois agentes, o clique pergunta qual — e não escolhe um por conta própria.</summary>
    [AvaloniaFact]
    public async Task AClickOnTheBadgeOfTwoAgents_AsksWhich_InsteadOfFocusing()
    {
        _runner.Result = new TodayBoard(Date, [], [], [Listed(agents: 2)], [], []);
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] = Focus(AgentSessionStatus.Running, true);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var badge = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(block => block.Name == "AgentBadge");

        badge.Text.Should().Be("● Claude Code ×2");

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var centre = new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2);
        var point = badge.TranslatePoint(centre, window)!.Value;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        _runner.Invoked.Should().NotContain(typeof(FocusAgentSessionHandler));
    }
}
