using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O card do agente na aba Desenvolvimento (ADR-030): o que ele mostra em cada
/// situação e quais casos de uso ele pede.
/// </summary>
public class AgentSessionViewModelTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 23, 21, 42, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\eco-core-feature-123";

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeClipboardWriter _clipboard = new();
    private readonly FakeShellLauncher _shell = new();
    private readonly Guid _taskId = Guid.CreateVersion7();

    private static readonly AgentCliInstallGuide Guide = new(
        "Instalar o Claude Code no Windows",
        [new AgentCliInstallStep("No PowerShell:", "irm https://claude.ai/install.ps1 | iex")],
        new Uri("https://docs.claude.com/en/docs/claude-code/setup"));

    private static AgentCliStatus Installed() =>
        new("claude-code", "Claude Code", "claude",
            new CliDetectionResult { IsInstalled = true, ExecutablePath = @"C:\claude.exe", Version = "2.1.4" },
            null);

    private static AgentCliStatus Missing() =>
        new("claude-code", "Claude Code", "claude", CliDetectionResult.NotInstalled("Claude Code não encontrado."), Guide);

    private AgentSessionView Session(
        AgentSessionStatus status,
        int? processId = 15432,
        DateTimeOffset? ended = null,
        string? failure = null) =>
        new(Guid.CreateVersion7(), _taskId, "claude-code", "Claude Code", @"C:\claude.exe", Worktree,
            processId, Started, ended, status, failure);

    private AgentSessionViewModel Create(bool isReadOnly = false)
    {
        var viewModel = new AgentSessionViewModel(_runner, _clipboard, _shell, NullLogger<AgentSessionViewModel>.Instance);
        viewModel.Load(_taskId, Guid.CreateVersion7(), isReadOnly);
        return viewModel;
    }

    private static string Clock(DateTimeOffset instant) => instant.ToLocalTime().ToString("HH:mm");

    [Fact]
    public async Task ATaskWithoutSession_OffersToStartTheAgent()
    {
        _runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Installed();
        var viewModel = Create();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.State.Should().Be(AgentPanelState.Idle);
        viewModel.StatusText.Should().Be("Nenhuma sessão ativa.");
        viewModel.StartLabel.Should().Be("Iniciar Claude Code");
        viewModel.ShowStart.Should().BeTrue();
        viewModel.StartCommand.CanExecute(null).Should().BeTrue();
        viewModel.FocusCommand.CanExecute(null).Should().BeFalse();
        viewModel.VersionText.Should().Be("Versão 2.1.4");
    }

    [Fact]
    public async Task ARunningSession_ShowsThePid_AndOffersTheTerminal_NotANewAgent()
    {
        _runner.ResultsByHandler[typeof(GetTaskAgentSessionHandler)] = Session(AgentSessionStatus.Running);
        var viewModel = Create();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.State.Should().Be(AgentPanelState.Running);
        viewModel.StatusText.Should().Be("● Em execução");
        viewModel.ProcessText.Should().Be("PID 15432");
        viewModel.StartedText.Should().Be($"Iniciado às {Clock(Started)}");
        viewModel.WorkingDirectory.Should().Be(Worktree);
        viewModel.ShowStart.Should().BeFalse();
        viewModel.FocusCommand.CanExecute(null).Should().BeTrue();
        _runner.Invoked.Should().NotContain(typeof(DetectAgentCliHandler));
    }

    [Fact]
    public async Task AFinishedSession_ShowsWhenItRan_AndCanStartAgain()
    {
        _runner.ResultsByHandler[typeof(GetTaskAgentSessionHandler)] =
            Session(AgentSessionStatus.Exited, ended: Started.AddMinutes(35));
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Installed();
        var viewModel = Create();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.State.Should().Be(AgentPanelState.Finished);
        viewModel.StatusText.Should().Be("○ Finalizado");
        viewModel.RangeText.Should().Be($"{Clock(Started)} → {Clock(Started.AddMinutes(35))}");
        viewModel.FocusCommand.CanExecute(null).Should().BeFalse();
        viewModel.StartCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task WithoutTheAgentInstalled_ShowsTheGuide_AndNoStartButton()
    {
        _runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Missing();
        var viewModel = Create();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.State.Should().Be(AgentPanelState.NotInstalled);
        viewModel.StatusText.Should().Be("Claude Code não encontrado.");
        viewModel.InstallGuide.Should().Be(Guide);
        viewModel.ShowStart.Should().BeFalse();
        viewModel.StartCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Start_RunsTheUseCase_AndShowsTheRunningSession()
    {
        _runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Installed();
        _runner.ResultsByHandler[typeof(StartAgentSessionHandler)] = Session(AgentSessionStatus.Running);
        var viewModel = Create();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.StartCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Contain(typeof(StartAgentSessionHandler));
        viewModel.State.Should().Be(AgentPanelState.Running);
        viewModel.ProcessText.Should().Be("PID 15432");
        viewModel.Message.Should().BeNull();
    }

    [Fact]
    public async Task Start_WhenTheAgentExitsRightAway_SaysSo()
    {
        _runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Installed();
        _runner.ResultsByHandler[typeof(StartAgentSessionHandler)] =
            Session(AgentSessionStatus.Exited, ended: Started);
        var viewModel = Create();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.State.Should().Be(AgentPanelState.Finished);
        viewModel.Message.Should().Be("O Claude Code encerrou logo ao abrir.");
    }

    [Fact]
    public async Task Start_WhenTheTerminalDoesNotOpen_ShowsTheReason()
    {
        _runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Installed();
        _runner.ResultsByHandler[typeof(StartAgentSessionHandler)] =
            Session(AgentSessionStatus.Failed, processId: null, ended: Started, failure: "O executável não foi encontrado.");
        var viewModel = Create();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.State.Should().Be(AgentPanelState.Failed);
        viewModel.FailureReason.Should().Be("O executável não foi encontrado.");
        viewModel.StartCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>Desinstalado com o app aberto: a recusa vira as instruções.</summary>
    [Fact]
    public async Task Start_RefusedBecauseTheAgentIsGone_ShowsTheGuide()
    {
        _runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        _runner.Enqueue<DetectAgentCliHandler>(Installed(), Missing());
        _runner.FailuresByHandler[typeof(StartAgentSessionHandler)] = new DomainException("Claude Code não encontrado.");
        var viewModel = Create();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.Message.Should().Be("Claude Code não encontrado.");
        viewModel.State.Should().Be(AgentPanelState.NotInstalled);
    }

    [Fact]
    public async Task Start_RefusedForAnotherReason_KeepsTheCard_AndShowsWhy()
    {
        _runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Installed();
        _runner.FailuresByHandler[typeof(StartAgentSessionHandler)] = new DomainException("A pasta do worktree não existe mais.");
        var viewModel = Create();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.Message.Should().Be("A pasta do worktree não existe mais.");
        viewModel.State.Should().Be(AgentPanelState.Idle);
    }

    [Fact]
    public async Task Focus_BringsTheTerminal_WithoutStartingAnything()
    {
        _runner.ResultsByHandler[typeof(GetTaskAgentSessionHandler)] = Session(AgentSessionStatus.Running);
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] =
            new AgentFocusResult(Session(AgentSessionStatus.Running), true);
        var viewModel = Create();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.FocusCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Contain(typeof(FocusAgentSessionHandler));
        _runner.Invoked.Should().NotContain(typeof(StartAgentSessionHandler));
        viewModel.Message.Should().BeNull();
    }

    [Fact]
    public async Task Focus_OnAnAgentThatAlreadyExited_TurnsTheCardFinished()
    {
        _runner.ResultsByHandler[typeof(GetTaskAgentSessionHandler)] = Session(AgentSessionStatus.Running);
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] =
            new AgentFocusResult(Session(AgentSessionStatus.Exited, ended: Started.AddMinutes(5)), false);
        var viewModel = Create();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.FocusCommand.ExecuteAsync(null);

        viewModel.State.Should().Be(AgentPanelState.Finished);
        viewModel.Message.Should().Be("O Claude Code deste ambiente já foi encerrado.");
    }

    [Fact]
    public async Task Focus_WithoutFindingTheWindow_SaysWhichProcessToLookFor()
    {
        _runner.ResultsByHandler[typeof(GetTaskAgentSessionHandler)] = Session(AgentSessionStatus.Running);
        _runner.ResultsByHandler[typeof(FocusAgentSessionHandler)] =
            new AgentFocusResult(Session(AgentSessionStatus.Running), false);
        var viewModel = Create();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.FocusCommand.ExecuteAsync(null);

        viewModel.State.Should().Be(AgentPanelState.Running);
        viewModel.Message.Should().Contain("PID 15432");
    }

    [Fact]
    public async Task AReadOnlyTask_DoesNotStartAgents_ButReachesTheOpenOne()
    {
        _runner.ResultsByHandler[typeof(GetTaskAgentSessionHandler)] = Session(AgentSessionStatus.Running);
        var viewModel = Create(isReadOnly: true);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.ShowStart.Should().BeFalse();
        viewModel.FocusCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ALoadFailure_IsShown_NotThrown()
    {
        _runner.FailuresByHandler[typeof(GetTaskAgentSessionHandler)] = new InvalidOperationException("banco");
        var viewModel = Create();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.Message.Should().Be("Não foi possível verificar o agente de IA desta tarefa.");
        viewModel.State.Should().Be(AgentPanelState.Idle);
    }

    [Fact]
    public async Task TheInstallCommand_CanBeCopied_AndTheDocsOpened()
    {
        _runner.Enqueue<GetTaskAgentSessionHandler>([null]);
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Missing();
        var viewModel = Create();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.CopyCommand.ExecuteAsync("irm https://claude.ai/install.ps1 | iex");
        await viewModel.OpenInstallDocsCommand.ExecuteAsync(null);

        _clipboard.Written.Should().Equal("irm https://claude.ai/install.ps1 | iex");
        _shell.OpenedUris.Should().Equal(Guide.DocumentationUrl);
    }
}
