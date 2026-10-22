using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A aba Consulta Inteligente (ADR-081) sem janela: o que ela carrega, o que
/// grava a cada escolha, quando "Iniciar consulta" liga e o que ela diz.
/// </summary>
public class TaskConsultationViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);

    internal static readonly Guid TaskId = Guid.CreateVersion7(Now);

    internal static readonly ConsultationTagOption Loja = new(Guid.CreateVersion7(Now), "Loja", "#3366FF", true, 2);

    internal static readonly ConsultationDatabaseOption Copy = new(
        Guid.CreateVersion7(Now), "loja_copia", "loja_copia (ambiente feature/1234)", "Servidor local", "localhost:5432/loja_copia",
        DatabaseEnvironment.Development, ConsultationDatabaseOrigin.TaskEnvironment);

    internal static ConsultationSourceOption Source(string name, ConsultationSourceState state = ConsultationSourceState.Ready, bool selected = true) =>
        new(ConsultationSourceKind.TagDirectory, Guid.CreateVersion7(), name, name.ToLowerInvariant(), $@"C:\Projetos\{name}", null, "main", state, selected);

    internal static TaskConsultationView View(
        IReadOnlyList<ConsultationSourceOption>? sources = null,
        ConsultationDatabaseSelection? database = null,
        IReadOnlyList<string>? problems = null,
        AgentSessionView? active = null,
        IReadOnlyList<ConsultationSessionView>? sessions = null,
        bool toolsAvailable = true,
        string? lastQuestion = null) =>
        new(
            TaskId,
            "Pedido pendente",
            false,
            true,
            Loja.Id,
            Loja.Name,
            [Loja],
            sources ?? [Source("API"), Source("Web")],
            database,
            [Copy],
            null,
            null,
            null,
            lastQuestion,
            problems ?? [],
            [],
            active,
            sessions ?? [])
        {
            Models = [new AgentCliOption("opus", "Opus", ["--model", "opus"])],
            Efforts = [new AgentCliOption("high", "Alto", ["--effort", "high"])],
            ToolsAvailable = toolsAvailable,
        };

    internal static ConsultationDatabaseSelection Selected() =>
        new(Copy.ConnectionId, Copy.DatabaseName, Copy.Label, Copy.ConnectionName, Copy.Server, Copy.Environment, false, null);

    internal static AgentCliStatus Claude(bool installed = true) =>
        new("claude-code", "Claude Code", "claude",
            installed ? new CliDetectionResult { IsInstalled = true, ExecutablePath = @"C:\claude.exe" } : CliDetectionResult.NotInstalled("não"),
            installed ? null : new AgentCliInstallGuide("Instalar", [new AgentCliInstallStep("Rode:", "irm https://claude.ai/install.ps1 | iex")], new Uri("https://example.test")));

    private static AgentSessionView Running(AgentActivity activity = AgentActivity.Working) =>
        new(Guid.CreateVersion7(), TaskId, "claude-code", "Claude Code", @"C:\claude.exe", @"C:\Projetos\API", 4242, Now, null,
            AgentSessionStatus.Running, null, true, activity);

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();

    private TaskConsultationViewModel ViewModel()
    {
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Claude();
        var viewModel = new TaskConsultationViewModel(
            _runner, _confirmation, TestDevelopment.Database(_runner, confirmation: _confirmation), NullLogger<TaskConsultationViewModel>.Instance);
        viewModel.Load(TaskId);
        return viewModel;
    }

    private async Task<TaskConsultationViewModel> LoadedAsync(TaskConsultationView view)
    {
        _runner.ResultsByHandler[typeof(GetTaskConsultationHandler)] = view;
        var viewModel = ViewModel();
        await viewModel.ActivateAsync(CancellationToken.None);
        return viewModel;
    }

    [Fact]
    public async Task Loading_ShowsTheChoices_WithoutSavingAnything()
    {
        var viewModel = await LoadedAsync(View());

        viewModel.SelectedTag.Should().Be(Loja);
        viewModel.Sources.Select(source => source.Name).Should().Equal("API", "Web");
        viewModel.Sources.Should().OnlyContain(source => source.IsSelected);
        viewModel.UsesDatabase.Should().BeFalse();
        viewModel.SelectedModel!.Label.Should().Be("Padrão");
        _runner.Invoked.Should().NotContain(typeof(SaveTaskConsultationHandler));
    }

    [Fact]
    public async Task UncheckingAFolder_SavesRightAway()
    {
        var viewModel = await LoadedAsync(View());
        _runner.ResultsByHandler[typeof(SaveTaskConsultationHandler)] = View(sources: [Source("API"), Source("Web", selected: false)]);

        viewModel.Sources[1].IsSelected = false;
        await Task.Yield();

        _runner.Invoked.Should().Contain(typeof(SaveTaskConsultationHandler));
        viewModel.Sources[1].IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task StartingNeedsAQuestion_AndSaysSo()
    {
        var viewModel = await LoadedAsync(View());

        viewModel.CanStart.Should().BeFalse();
        viewModel.StartBlockedReason.Should().Be("Escreva a pergunta.");

        viewModel.Question = "Por que o pedido 1234 está pendente?";

        viewModel.CanStart.Should().BeTrue();
        viewModel.StartBlockedReason.Should().BeNull();
    }

    [Fact]
    public async Task TheLastQuestion_ComesBack()
    {
        var viewModel = await LoadedAsync(View(lastQuestion: "Por que está pendente?"));

        viewModel.Question.Should().Be("Por que está pendente?");
    }

    [Fact]
    public async Task AProblem_BlocksStarting_WithTheReasonInSight()
    {
        var viewModel = await LoadedAsync(View(problems: ["Escolha pelo menos uma pasta disponível neste computador."]));
        viewModel.Question = "Pergunta";

        viewModel.CanStart.Should().BeFalse();
        viewModel.StartBlockedReason.Should().Contain("pelo menos uma pasta");
        viewModel.Problems.Should().ContainSingle();
    }

    [Fact]
    public async Task WithTheDatabase_AndTheMcpServerOff_ItSaysToTurnItOn()
    {
        _runner.ResultsByHandler[typeof(CheckConsultationDatabaseHandler)] =
            new ConsultationDatabaseCheck(true, "14.11", "mytaskapp_leitura", true, [], false, "Acesso de leitura confirmado.");
        var viewModel = await LoadedAsync(View(database: Selected(), toolsAvailable: false));
        viewModel.Question = "Pergunta";

        viewModel.UsesDatabase.Should().BeTrue();
        viewModel.ShowsMcpHint.Should().BeTrue();
        viewModel.CanStart.Should().BeFalse();
        viewModel.StartBlockedReason.Should().Contain("servidor MCP");
    }

    [Fact]
    public async Task WithoutClaude_TheInstallGuideShows()
    {
        _runner.ResultsByHandler[typeof(GetTaskConsultationHandler)] = View();
        var viewModel = new TaskConsultationViewModel(
            _runner, _confirmation, TestDevelopment.Database(_runner), NullLogger<TaskConsultationViewModel>.Instance);
        _runner.ResultsByHandler[typeof(DetectAgentCliHandler)] = Claude(installed: false);
        viewModel.Load(TaskId);
        await viewModel.ActivateAsync(CancellationToken.None);
        viewModel.Question = "Pergunta";

        viewModel.IsAgentMissing.Should().BeTrue();
        viewModel.InstallGuide!.Steps.Should().ContainSingle();
        viewModel.CanStart.Should().BeFalse();
    }

    [Fact]
    public async Task Starting_OpensTheConsultation_AndShowsItIsOpen()
    {
        var viewModel = await LoadedAsync(View());
        viewModel.Question = "Por que o pedido 1234 está pendente?";
        var running = Running();
        _runner.ResultsByHandler[typeof(StartTaskConsultationHandler)] = running;
        _runner.ResultsByHandler[typeof(GetTaskConsultationHandler)] = View(active: running);

        await viewModel.StartCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Contain(typeof(StartTaskConsultationHandler));
        viewModel.IsSessionActive.Should().BeTrue();
        viewModel.StatusText.Should().Contain("continue a conversa no terminal");
        viewModel.CanStart.Should().BeFalse();
        viewModel.GoToTerminalCommand.CanExecute(null).Should().BeTrue();
        viewModel.CanEditDatabase.Should().BeFalse();
    }

    [Fact]
    public async Task ARefusedStart_ShowsTheReason()
    {
        var viewModel = await LoadedAsync(View());
        viewModel.Question = "Pergunta";
        _runner.FailuresByHandler[typeof(StartTaskConsultationHandler)] =
            new DomainException("O usuário postgres pode alterar dados (é superusuário). Use \"Preparar acesso de leitura\" antes de consultar este banco.");

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().Contain("Preparar acesso de leitura");
    }

    [Fact]
    public async Task AFailedTerminal_IsSaidPlainly()
    {
        var viewModel = await LoadedAsync(View());
        viewModel.Question = "Pergunta";
        _runner.ResultsByHandler[typeof(StartTaskConsultationHandler)] = Running() with
        {
            Status = AgentSessionStatus.Failed,
            FailureReason = "Windows Terminal não encontrado.",
        };

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().Be("Não foi possível iniciar o Claude Code: Windows Terminal não encontrado.");
    }

    [Fact]
    public async Task WaitingForYou_IsTheStatus()
    {
        var viewModel = await LoadedAsync(View(active: Running(AgentActivity.WaitingForUser)));

        viewModel.StatusText.Should().Be("Aguardando você no terminal.");
        viewModel.IsWaitingForYou.Should().BeTrue();
    }

    [Fact]
    public async Task AClosedTerminal_OffersToReopen()
    {
        var ended = new ConsultationSessionView(Guid.CreateVersion7(), "conversa-1", Now, Now.AddMinutes(10), AgentSessionStatus.Exited, null);
        var viewModel = await LoadedAsync(View(sessions: [ended]));

        viewModel.StatusText.Should().Contain("Terminal encerrado");
        viewModel.Sessions.Should().ContainSingle().Which.CanResume.Should().BeTrue();
        viewModel.ResumeCommand.CanExecute(viewModel.Sessions[0]).Should().BeTrue();
    }

    [Fact]
    public async Task PreparingReadAccess_AsksFirst_AndDoesNothingOnNo()
    {
        _runner.ResultsByHandler[typeof(CheckConsultationDatabaseHandler)] =
            new ConsultationDatabaseCheck(true, "14.11", "postgres", false, ["é superusuário"], true, "O usuário postgres pode alterar dados.");
        var viewModel = await LoadedAsync(View(database: Selected()));
        await viewModel.CheckDatabaseCommand.ExecuteAsync(null);

        viewModel.CanPrepareReadAccess.Should().BeTrue();
        await viewModel.PrepareReadAccessCommand.ExecuteAsync(null);

        _confirmation.LastAsked!.Message.Should().Contain("mytaskapp_leitura").And.Contain("loja_copia").And.Contain("só consegue entrar e ler");
        _runner.Invoked.Should().NotContain(typeof(PrepareConsultationReadAccessHandler));
    }

    [Fact]
    public async Task PreparingReadAccess_OnYes_ShowsTheNewCheck()
    {
        _runner.ResultsByHandler[typeof(CheckConsultationDatabaseHandler)] =
            new ConsultationDatabaseCheck(true, "14.11", "postgres", false, ["é superusuário"], true, "O usuário postgres pode alterar dados.");
        _runner.ResultsByHandler[typeof(PrepareConsultationReadAccessHandler)] =
            new ConsultationDatabaseCheck(true, "14.11", "mytaskapp_leitura", true, [], false, "Acesso de leitura confirmado.");
        _confirmation.Answer = true;
        var viewModel = await LoadedAsync(View(database: Selected()));
        await viewModel.CheckDatabaseCommand.ExecuteAsync(null);

        await viewModel.PrepareReadAccessCommand.ExecuteAsync(null);

        viewModel.DatabaseReadOnly.Should().BeTrue();
        viewModel.DatabaseCheckText.Should().Be("Acesso de leitura confirmado.");
        viewModel.CanPrepareReadAccess.Should().BeFalse();
    }

    [Fact]
    public async Task Cloning_AsksFirst_ShowingWhereTo()
    {
        var notCloned = Source("Pagamentos", ConsultationSourceState.NotCloned, selected: false) with { RepositoryUrl = "https://github.com/empresa/pagamentos.git" };
        var viewModel = await LoadedAsync(View(sources: [Source("API"), notCloned]));
        _runner.ResultsByHandler[typeof(CloneConsultationRepositoryHandler)] = @"C:\Projetos\pagamentos";

        var item = viewModel.Sources.Single(source => source.CanClone);
        await viewModel.CloneCommand.ExecuteAsync(item);

        _confirmation.LastAsked!.Message.Should().Contain("https://github.com/empresa/pagamentos.git").And.Contain(@"C:\Projetos\pagamentos");
        _runner.Invoked.Count(type => type == typeof(CloneConsultationRepositoryHandler)).Should().Be(1, "só a pasta de destino foi pedida");
    }

    [Fact]
    public async Task Clearing_AsksFirst()
    {
        var viewModel = await LoadedAsync(View());

        await viewModel.ClearCommand.ExecuteAsync(null);

        _confirmation.LastAsked!.Message.Should().Contain("continuam onde estão");
        _runner.Invoked.Should().NotContain(typeof(ClearTaskConsultationHandler));
    }

    [Fact]
    public async Task TheOldTask_DoesNotOverwriteTheNewOne()
    {
        var viewModel = await LoadedAsync(View());

        viewModel.Load(Guid.CreateVersion7());

        viewModel.Sources.Should().BeEmpty();
        viewModel.View.Should().BeNull();
        viewModel.Question.Should().BeEmpty();
    }
}
