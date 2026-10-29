using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.External;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;
using RemoteCopyRequest = MyTaskApp.Remote.Contracts.DatabaseCopyRequest;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// "Preparar tudo" (E6, T-33): worktree, banco, comandos e Claude num
/// <c>worktree.provision</c>, com os casos de uso de verdade sobre o Git, o
/// PostgreSQL e o Claude falsos. A ordem é a do ADR-060; cada etapa vira um
/// checkpoint, e a falha diz em qual foi.
/// </summary>
public sealed class PrepareEverythingTests : IAsyncDisposable
{
    private const string Command = "dotnet run --project tools/seed -- --banco {database}";

    private readonly DatabaseCopyScenario _scenario = new();
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeTagQuery _tags = new();
    private readonly FakeGitClient _git = new();
    private readonly FakeCommandExecutor _executor = new();
    private readonly FakeHeadlessProvider _claude = new();
    private readonly ScriptedHeadlessRunner _agent = new();
    private readonly List<RemoteOperationEvent> _events = [];
    private readonly List<(RemoteApprovalAsk Ask, int GitCalls)> _asks = [];
    private readonly TaskItem _task = TaskItem.Create("Validar antimicrobianos", DatabaseCopyScenario.Now);
    private readonly TagDirectoryRow _directory = new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "ECO", "#22C55E", "@eco", FakeGitClient.Repository, "Núcleo", null, DefaultBranch: "develop");
    private readonly ServiceProvider _services;

    public PrepareEverythingTests()
    {
        _scenario.Production.AllowRemote(true, DatabaseCopyScenario.Now);
        _scenario.Development.AllowRemote(true, DatabaseCopyScenario.Now);

        // Uma tentativa que falhou antes, com a lista pós-Worktree que usa o banco: a retomada mantém as duas.
        var failed = _task.BeginDevelopment(null, FakeGitClient.Repository, "develop", "feature/validar", FakeGitClient.Repository + "-tentativa", DatabaseCopyScenario.Now);
        _task.SetDevelopmentCommands(failed.Id, [Command], DatabaseCopyScenario.Now);
        _task.MarkDevelopmentFailed(failed.Id, "o agente caiu", DatabaseCopyScenario.Now);
        _tasks.Seed(_task);
        _tags.Directories.Add(_directory);

        var disk = new WorktreeDisk(_git);
        _services = new ServiceCollection()
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>))
            .AddSingleton<TimeProvider>(_scenario.Clock)
            .AddSingleton<ITaskItemRepository>(_tasks)
            .AddSingleton<IUnitOfWork>(_tasks)
            .AddSingleton<ITagQuery>(_tags)
            .AddSingleton<IGitClient>(_git)
            .AddSingleton<IDirectoryProbe>(disk)
            .AddSingleton<IBranchConventionStore>(new FakeBranchConventionStore())
            .AddSingleton<IDevelopmentCommandRepository>(new FakeDevelopmentCommandRepository())
            .AddSingleton<ICommandExecutor>(_executor)
            .AddSingleton<MyTaskApp.Application.DatabaseOperations.ISavedDatabaseRepository>(_scenario.Catalog.SavedDatabases)
            .AddSingleton<ListBranchesHandler>()
            .AddSingleton<GetTaskExternalContextHandler>()
            .AddSingleton<RemoteProvisionPlanner>()
            .AddSingleton<PrepareDevelopmentHandler>()
            .AddSingleton<StartDevelopmentHandler>()
            .AddSingleton<RunDevelopmentCommandsHandler>()
            .AddSingleton<ProvisionEnvironmentHandler>()
            .AddSingleton(_scenario.ValidateHandler())
            .AddSingleton(_scenario.RunHandler())
            .AddSingleton(provider => new CopyDevelopmentDatabaseHandler(
                _tasks,
                _scenario.Catalog.Connections,
                _scenario.Catalog.SavedDatabases,
                _scenario.RunHandler(),
                _tasks,
                _scenario.Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CopyDevelopmentDatabaseHandler>.Instance))
            .AddSingleton(new RemoteDatabaseCopyPlanner(
                _scenario.Catalog.Connections, _scenario.Catalog.CopyProfiles, _scenario.Catalog.AnonymizationProfiles, _tasks))
            .AddSingleton<RemoteDatabaseCopy>()
            .AddSingleton<IAgentSessionRepository>(new FakeAgentSessionRepository())
            .AddSingleton<IAgentProcessTracker>(new FakeAgentProcessTracker())
            .AddSingleton<IDataChangeNotifier>(new DataChangeNotifier())
            .AddSingleton<IHeadlessAgentProvider>(_claude)
            .AddSingleton<IHeadlessAgentRunner>(_agent)
            .AddSingleton<IHeadlessApprovalGateway>(new FakeApprovalGateway())
            .AddSingleton(HeadlessAgentLimits.Default)
            .AddSingleton<HeadlessConversations>()
            .AddSingleton<HeadlessPromptRunner>()
            .AddSingleton<HeadlessAgentPlanner>()
            .AddSingleton<HeadlessSessionStore>()
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .AddSingleton<WorktreeProvisionExecutor>()
            .BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WorktreeProvisionExecutor Executor => _services.GetRequiredService<WorktreeProvisionExecutor>();

    private TaskDevelopment Development => _task.Developments.Single();

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task Everything_InTheOrderOfTheScreen_WithACheckpointPerStep()
    {
        _agent.Script(FakeHeadlessProvider.Text("Li o README."), FakeHeadlessProvider.Done());
        var request = Request(database: true, agent: true);

        (await Executor.CheckAsync(request, Ct)).Should().BeNull();
        var result = await Executor.ExecuteAsync(Execution(request, Allow), Ct);

        result.Succeeded.Should().BeTrue(result.Summary);
        result.Summary.Should().Be(
            "Worktree criado e 1 comando: feature/validar em ECO · Núcleo, a partir de develop. Banco: eco_dev. Claude: Respondeu em 1 turno.");

        var ask = _asks.Should().ContainSingle().Subject;
        ask.Ask.Kind.Should().Be(ApprovalKind.ProductionDatabase);
        ask.GitCalls.Should().Be(0, "a produção é aprovada antes do Git: negar não cria nada (ADR-060)");

        Development.Status.Should().Be(TaskDevelopmentStatus.Ready);
        Development.Database!.Name.Should().Be("eco_dev");
        _executor.Commands.Should().Equal("dotnet run --project tools/seed -- --banco eco_dev");
        _agent.Prompts.Should().Equal("Leia o README e proponha o plano.");

        Checkpoints().Should().Equal(
            WorktreeProvisionExecutor.DatabaseStep,
            WorktreeProvisionExecutor.PrepareStep,
            WorktreeProvisionExecutor.WorktreeStep,
            WorktreeProvisionExecutor.DatabaseStep,
            WorktreeProvisionExecutor.CommandsStep,
            WorktreeProvisionExecutor.AgentStep);

        var data = result.Data!.Value;
        data.GetProperty("developmentId").GetGuid().Should().Be(Development.Id);
        data.GetProperty("database").GetString().Should().Be("eco_dev");
        data.GetProperty("agentSessionId").GetGuid().Should().NotBeEmpty("quem pediu já manda o próximo prompt nessa conversa");
    }

    [Fact]
    public async Task DenyingTheProductionCopy_CreatesNothing()
    {
        var result = await Executor.ExecuteAsync(Execution(Request(database: true, agent: true), _ => new RemoteApprovalResult(RemoteApprovalOutcome.Denied, "Pixel 8")), Ct);

        result.Code.Should().Be(RemoteErrorCodes.ApprovalDenied);
        result.Summary.Should().Be("Banco: A cópia de produção: negado por Pixel 8. Nada foi criado.");
        _git.Calls.Should().BeEmpty();
        Development.Status.Should().Be(TaskDevelopmentStatus.Error, "a tentativa anterior continua como estava");
        _agent.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutClaudeHere_NothingIsCreated()
    {
        _claude.Unavailable = "O Claude Code não está instalado nesta máquina.";

        (await Executor.CheckAsync(Request(agent: true), Ct))!.Summary.Should().Be("O Claude Code não está instalado nesta máquina.");
        var result = await Executor.ExecuteAsync(Execution(Request(agent: true)), Ct);

        result.Summary.Should().Be("Claude: O Claude Code não está instalado nesta máquina. Nada foi criado.");
        _git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedCopy_SaysTheStep_KeepsTheWorktree_AndRunsNoCommand()
    {
        _scenario.Tools.RestoreResult = new MyTaskApp.Application.DatabaseOperations.PgToolRun(1, false, TimeSpan.FromSeconds(1), "pg_restore: erro");
        _agent.Script(FakeHeadlessProvider.Done());

        var result = await Executor.ExecuteAsync(Execution(Request(database: true, agent: true), Allow), Ct);

        result.Succeeded.Should().BeFalse();
        result.Summary.Should().StartWith("Banco: ").And.EndWith("Worktree criado; copie o banco de novo e rode os comandos.");
        Development.Status.Should().Be(TaskDevelopmentStatus.Ready, "o worktree serve sem o banco");
        _executor.Commands.Should().BeEmpty("os comandos esperam o {database}");
        _agent.Prompts.Should().BeEmpty();
        result.Data!.Value.GetProperty("developmentId").GetGuid().Should().Be(Development.Id);
    }

    [Fact]
    public async Task AFailedCommand_StopsBeforeClaude()
    {
        _executor.Script("dotnet run --project tools/seed -- --banco eco_dev", 1);

        var result = await Executor.ExecuteAsync(Execution(Request(database: true, agent: true), Allow), Ct);

        result.Summary.Should().Be("Worktree criado, mas um comando pós-Worktree falhou (dotnet run --project tools/seed -- --banco eco_dev). O worktree fica.");
        _agent.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownClaudeProfile_IsRefusedBeforeAnything()
    {
        var request = Request(agent: true, profile: "admin");

        (await Executor.CheckAsync(request, Ct))!.Summary.Should().Be("Perfil desconhecido nesta máquina: admin.");
    }

    /// <summary>Só os passos que começam, sem repetir a etapa da vez: o que a tela "Operação" mostra como checkpoints.</summary>
    private List<string> Checkpoints()
    {
        var steps = new List<string>();

        foreach (var step in _events.Where(item => item.Kind == RemoteEventKind.StepStarted).Select(item => item.Step!))
        {
            if (steps.Count == 0 || steps[^1] != step)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    private static RemoteApprovalResult Allow(RemoteApprovalAsk ask) => new(RemoteApprovalOutcome.Allowed, "Pixel 8");

    private RemoteOperationRequest Request(bool database = false, bool agent = false, string profile = "edit") => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        RemoteOperationKinds.WorktreeProvision,
        JsonSerializer.SerializeToElement(
            new EnvironmentProvisioningRequest(
                _task.Id,
                _directory.Id,
                null,
                null,
                null,
                WorktreeConflictPolicy.Fail,
                RunPostWorktreeCommands: true,
                database ? new RemoteCopyRequest(_scenario.Production.Id, null, _scenario.Development.Id, null, _scenario.Profile.Id, null) : null,
                agent ? new ClaudeCodeSessionRequest(Guid.Empty, profile, null, null, new PromptRequest(null, "Leia o README e proponha o plano.")) : null),
            RemoteJson.Options),
        _task.Id,
        [_task.Id],
        DatabaseCopyScenario.Now,
        DatabaseCopyScenario.Now.AddMinutes(10));

    private RemoteExecution Execution(RemoteOperationRequest request, Func<RemoteApprovalAsk, RemoteApprovalResult>? approve = null) =>
        new(
            request,
            "Pixel 8",
            (item, _) =>
            {
                lock (_events)
                {
                    _events.Add(item);
                }

                return Task.CompletedTask;
            },
            _scenario.Clock,
            approve is null
                ? null
                : (ask, _) =>
                {
                    _asks.Add((ask, _git.Calls.Count));
                    return Task.FromResult(approve(ask));
                });

    /// <summary>O disco do Git falso: o repositório e os worktrees que ele criou existem.</summary>
    private sealed class WorktreeDisk(FakeGitClient git) : IDirectoryProbe
    {
        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Exists(path));

        public Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Exists(path));

        private bool Exists(string path) =>
            string.Equals(path, FakeGitClient.Repository, StringComparison.OrdinalIgnoreCase)
            || git.Worktrees.Any(worktree => string.Equals(worktree.Path.Replace('/', '\\'), path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
    }
}
