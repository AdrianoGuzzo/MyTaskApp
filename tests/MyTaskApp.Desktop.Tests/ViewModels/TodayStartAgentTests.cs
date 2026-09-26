using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
/// "Abrir Claude Code" do menu da linha (ADR-036): abre o agente num ambiente
/// da tarefa sem abrir a tarefa. Com mais de um ambiente, pergunta qual.
/// </summary>
public class TodayStartAgentTests
{
    private static readonly DateOnly Date = new(2026, 9, 26);

    private readonly FakeUseCaseRunner _runner = new()
    {
        Result = new TodayBoard(Date, [], [], [], [], []),
    };

    private TodayViewModel ViewModel() =>
        new(_runner, new FakeConfirmationDialog(), new FakeClipboardWriter(), TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

    private static TaskWorktree Worktree(string repository = "eco-core") =>
        new(Guid.CreateVersion7(), repository, "feature/x", "origin/main", $@"C:\Projects\{repository}-feature-x");

    private static TodayTask Listed(TaskWorktree[] worktrees, params ActiveAgent[] agents) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Integrar o Claude", TaskPriority.Normal, Date, null, false,
            ActiveAgents: agents.Length == 0 ? null : agents,
            Worktrees: worktrees.Length == 0 ? null : worktrees);

    private static AgentSessionView Session(AgentSessionStatus status, string? failure = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "claude-code", "Claude Code",
            @"C:\claude.exe", @"C:\Projects\eco-core-feature-x", 15432, DateTimeOffset.UtcNow,
            status is AgentSessionStatus.Exited or AgentSessionStatus.Failed ? DateTimeOffset.UtcNow : null,
            status, failure);

    [Fact]
    public async Task WithOneEnvironment_OpensTheAgentThere_AndSaysWhere()
    {
        _runner.ResultsByHandler[typeof(StartAgentSessionHandler)] = Session(AgentSessionStatus.Running);
        var viewModel = ViewModel();

        await viewModel.StartAgentAsync(new TaskRowViewModel(Listed([Worktree()]), false));

        _runner.Invoked.Should().StartWith(typeof(StartAgentSessionHandler)).And.Contain(typeof(GetTodayBoardHandler));
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.StatusMessage.Should().Be("Claude Code aberto em eco-core · feature/x.");
    }

    /// <summary>Com dois ambientes, o comando da linha não escolhe sozinho: quem pergunta é a view.</summary>
    [Fact]
    public async Task WithTwoEnvironments_TheRowCommandDoesNotPickOne()
    {
        var viewModel = ViewModel();

        await viewModel.StartAgentAsync(new TaskRowViewModel(Listed([Worktree(), Worktree("eco-api")]), false));

        _runner.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task ChoosingAnEnvironment_OpensTheAgentInIt()
    {
        _runner.ResultsByHandler[typeof(StartAgentSessionHandler)] = Session(AgentSessionStatus.Running);
        var viewModel = ViewModel();
        var row = new TaskRowViewModel(Listed([Worktree(), Worktree("eco-api")]), false);

        await viewModel.StartAgentInAsync(row.WorktreeChoices[1]);

        _runner.Invoked.Should().StartWith(typeof(StartAgentSessionHandler));
        viewModel.StatusMessage.Should().Be("Claude Code aberto em eco-api · feature/x.");
    }

    /// <summary>Um agente por ambiente: se já há um, a escolha traz o terminal dele em vez de ser recusada.</summary>
    [Fact]
    public async Task AnEnvironmentWithAnAgentAlreadyOpen_BringsItsTerminalForward()
    {
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] =
            new AgentFocusResult(Session(AgentSessionStatus.Running), true);
        var viewModel = ViewModel();
        var worktree = Worktree();
        var row = new TaskRowViewModel(
            Listed([worktree], new ActiveAgent(worktree.DevelopmentId, "Claude Code", "eco-core", "feature/x")), false);

        row.WorktreeChoices[0].Label.Should().Be("eco-core · feature/x (aberto)");

        await viewModel.StartAgentAsync(row);

        _runner.Invoked.Should().Equal(typeof(FocusAgentSessionHandler));
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task WhenTheAgentIsNotInstalled_ShowsTheUseCaseMessage()
    {
        _runner.FailuresByHandler[typeof(StartAgentSessionHandler)] = new DomainException("Claude Code não encontrado.");
        var viewModel = ViewModel();

        await viewModel.StartAgentAsync(new TaskRowViewModel(Listed([Worktree()]), false));

        viewModel.ErrorMessage.Should().Be("Claude Code não encontrado.");
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task WhenSomethingUnexpectedFails_ShowsAFriendlyMessage()
    {
        _runner.FailuresByHandler[typeof(StartAgentSessionHandler)] = new InvalidOperationException("boom");
        var viewModel = ViewModel();

        await viewModel.StartAgentAsync(new TaskRowViewModel(Listed([Worktree()]), false));

        viewModel.ErrorMessage.Should().Be("Não foi possível abrir o Claude Code.");
    }

    [Fact]
    public async Task WhenTheTerminalDoesNotOpen_ShowsWhy()
    {
        _runner.ResultsByHandler[typeof(StartAgentSessionHandler)] =
            Session(AgentSessionStatus.Failed, "Não foi possível abrir o terminal.");
        var viewModel = ViewModel();

        await viewModel.StartAgentAsync(new TaskRowViewModel(Listed([Worktree()]), false));

        viewModel.ErrorMessage.Should().Be("Não foi possível abrir o terminal.");
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task WhenTheAgentQuitsRightAway_SaysSo()
    {
        _runner.ResultsByHandler[typeof(StartAgentSessionHandler)] = Session(AgentSessionStatus.Exited);
        var viewModel = ViewModel();

        await viewModel.StartAgentAsync(new TaskRowViewModel(Listed([Worktree()]), false));

        viewModel.ErrorMessage.Should().Be("O Claude Code encerrou logo ao abrir.");
    }

    /// <summary>O menu de escolha tem um item por ambiente, cada um ligado ao comando com a sua escolha.</summary>
    [AvaloniaFact]
    public void ThePicker_OffersOneItemPerEnvironment()
    {
        var viewModel = ViewModel();
        var row = new TaskRowViewModel(Listed([Worktree(), Worktree("eco-api")]), false);

        var items = TodayView.WorktreeMenu(row, viewModel).Items.OfType<MenuItem>().ToList();

        items.Select(item => item.Header).Should().Equal(
            "Abrir Claude Code: eco-core · feature/x",
            "Abrir Claude Code: eco-api · feature/x");
        items.Should().AllSatisfy(item => item.Command.Should().BeSameAs(viewModel.StartAgentInCommand));
        items.Select(item => item.CommandParameter).Should().Equal(row.WorktreeChoices);
    }
}
