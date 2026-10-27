using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// <c>command.run</c> (E3, T-20) com o caminho do comando rápido de verdade e
/// o shell falso: só o liberado roda, escondido, com a saída ao vivo, e o
/// "pedir confirmação" vira aprovação remota.
/// </summary>
public sealed class CommandRunExecutorTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\ecossistema-core-feature-validacao";

    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeDevelopmentCommandRepository _commands = new();
    private readonly FakeTagQuery _tagQuery = new();
    private readonly FakeCommandExecutionRepository _executions = new();
    private readonly FakeCommandExecutor _shell = new();
    private readonly FakeAgentProcessTracker _processes = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly TaskItem _task = TaskItem.Create("Implementar validação de antimicrobianos", Now);
    private readonly Guid _tagId = Guid.CreateVersion7();
    private readonly Guid _directoryId = Guid.CreateVersion7();
    private readonly List<RemoteOperationEvent> _events = [];
    private readonly ServiceProvider _services;

    private readonly DevelopmentCommand _tests;

    public CommandRunExecutorTests()
    {
        _tasks.Seed(_task);
        _task.SetTags([_tagId]);
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/develop", "feature/validacao", Worktree, Now);
        _task.MarkDevelopmentReady(_task.Developments[0].Id, Now);
        _disk.Existing.Add(Worktree);

        _tests = _commands.Seed(
            "@testes",
            "dotnet test --filter {filtro}",
            settings: new DevelopmentCommandSettings(
                "Testes",
                CommandMode.Terminal,
                Parameters: [new CommandParameterSpec("filtro", "Filtro", CommandParameterType.Text, "Unit", false)]));
        _tests.AllowRemote(true);

        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_time)
            .AddSingleton<ITaskItemRepository>(_tasks)
            .AddSingleton<IDevelopmentCommandRepository>(_commands)
            .AddSingleton<ISavedDatabaseRepository>(new FakeSavedDatabaseRepository())
            .AddSingleton<ITagQuery>(_tagQuery)
            .AddSingleton<ICommandExecutionRepository>(_executions)
            .AddSingleton<IUnitOfWork>(_executions)
            .AddSingleton<ICommandExecutor>(_shell)
            .AddSingleton<ITerminalCommandLauncher>(new FakeTerminalCommandLauncher(_processes, Now))
            .AddSingleton<IAgentProcessTracker>(_processes)
            .AddSingleton<ICommandExecutionWatcher>(new FakeCommandExecutionWatcher())
            .AddSingleton<IDirectoryProbe>(_disk)
            .AddSingleton<ILogger<RunQuickCommandHandler>>(NullLogger<RunQuickCommandHandler>.Instance)
            .AddScoped<RunQuickCommandHandler>()
            .AddScoped<GetQuickCommandsHandler>()
            .AddScoped<PrepareQuickCommandHandler>()
            .AddScoped<RemoteCommandPlanner>()
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .AddSingleton<CommandRunExecutor>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CommandRunExecutor Executor => _services.GetRequiredService<CommandRunExecutor>();

    private Guid DevelopmentId => _task.Developments[0].Id;

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    [Fact]
    public async Task AnAllowedCommandRunsHiddenInTheWorktreeWithTheOutputLive()
    {
        _shell.Script("dotnet test --filter Integration", 0, ["Compilando…", "Aprovado! 12 testes"], ["aviso: lento"]);
        var request = Request(new Dictionary<string, string> { ["filtro"] = "Integration" });

        (await Executor.CheckAsync(request, Ct)).Should().BeNull();
        var result = await Executor.ExecuteAsync(Execution(request), Ct);

        result.Succeeded.Should().BeTrue(result.Summary);
        result.Summary.Should().Be("\"Testes\" (filtro = Integration) terminou bem.");
        result.Data!.Value.GetProperty("exitCode").GetInt32().Should().Be(0);

        // De terminal na tela, escondido de longe: não há quem veja a janela.
        _shell.Requests.Should().ContainSingle().Which.Should().Be(
            new CommandExecutionRequest("dotnet test --filter Integration", Worktree, CommandRunExecutor.MaxTimeout));
        _executions.Executions.Should().ContainSingle().Which.Mode.Should().Be(CommandMode.Execute);

        var output = _events.Where(item => item.Kind == RemoteEventKind.Output).ToList();
        output.Select(item => (item.Step, item.Text)).Should().Equal(
            (CommandRunExecutor.StandardOutput, "Compilando…\nAprovado! 12 testes"),
            (CommandRunExecutor.StandardError, "aviso: lento"));
    }

    [Fact]
    public async Task ACommandNotAllowedHereNeverRuns()
    {
        _tests.AllowRemote(false);
        var request = Request();

        (await Executor.CheckAsync(request, Ct))!.Summary.Should().Be("Este comando não está liberado para rodar de longe nesta máquina.");
        await FluentActions.Awaiting(() => Executor.ExecuteAsync(Execution(request), Ct)).Should().ThrowAsync<DomainException>();
        _shell.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TheAllowanceIsReadAgainWhenItRuns()
    {
        var request = Request();
        (await Executor.CheckAsync(request, Ct)).Should().BeNull();

        // Entre o aceite e a execução, alguém desligou aqui.
        _tests.AllowRemote(false);

        await FluentActions.Awaiting(() => Executor.ExecuteAsync(Execution(request), Ct)).Should().ThrowAsync<DomainException>();
        _shell.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Unit; rm -rf /")]
    [InlineData("Unit & calc")]
    [InlineData("Unit | more")]
    [InlineData("$(whoami)")]
    [InlineData("%PATH%")]
    [InlineData("\"Unit\"")]
    [InlineData("Unit > saida.txt")]
    public async Task ATextValueWithWhatTheShellReadsAsCodeIsRefused(string value)
    {
        var refusal = (await Executor.CheckAsync(Request(new Dictionary<string, string> { ["filtro"] = value }), Ct))!.Summary;

        refusal.Should().StartWith("Filtro não pode ter");
        _shell.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AParameterTheCommandDoesNotHaveIsRefused()
    {
        var refusal = (await Executor.CheckAsync(Request(new Dictionary<string, string> { ["porta"] = "80" }), Ct))!.Summary;

        refusal.Should().Be("\"Testes\" não tem o parâmetro porta.");
    }

    [Fact]
    public async Task AButtonWithTheTextChangedInTheDirectoryDoesNotRunFromAfar()
    {
        Bind(_tests, commandOverride: "dotnet test --filter {filtro}; deploy.ps1");

        var refusal = (await Executor.CheckAsync(Request(), Ct))!.Summary;

        refusal.Should().Contain("de longe, só roda como foi liberado");
    }

    [Fact]
    public async Task AButtonAsItWasAllowedRunsAsTheButton()
    {
        var binding = Bind(_tests);

        var result = await Executor.ExecuteAsync(Execution(Request()), Ct);

        result.Succeeded.Should().BeTrue(result.Summary);
        _executions.Executions.Single().TagDirectoryCommandId.Should().Be(binding);
    }

    [Fact]
    public async Task AskForConfirmationBecomesARemoteApprovalAndADenyRunsNothing()
    {
        var confirm = _commands.Seed("@limpar", "git clean -fdx", settings: new DevelopmentCommandSettings("Limpar", RequiresConfirmation: true));
        confirm.AllowRemote(true);
        RemoteApprovalAsk? asked = null;
        var request = Request(command: confirm.Id);

        var result = await Executor.ExecuteAsync(
            Execution(request, ask => { asked = ask; return new RemoteApprovalResult(RemoteApprovalOutcome.Denied, "Pixel 8"); }),
            Ct);

        asked!.Kind.Should().Be(ApprovalKind.CommandConfirmation);
        asked.Summary.Should().Be("Rodar \"Limpar\", a pedido de Pixel 8.");
        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be(RemoteErrorCodes.ApprovalDenied);
        _shell.Requests.Should().BeEmpty();

        var allowed = await Executor.ExecuteAsync(
            Execution(request, _ => new RemoteApprovalResult(RemoteApprovalOutcome.Allowed, "Pixel 8")),
            Ct);

        allowed.Succeeded.Should().BeTrue(allowed.Summary);
        _shell.Commands.Should().Equal("git clean -fdx");
    }

    [Fact]
    public async Task ACancelStopsTheShellAndEndsAsCanceled()
    {
        _shell.Hanging.Add("dotnet test --filter Unit");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var running = Executor.ExecuteAsync(Execution(Request()), cancel.Token);
        await _shell.HangStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cancel.CancelAsync();

        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
        _executions.Executions.Single().Status.Should().Be(CommandExecutionStatus.Stopped);
    }

    [Fact]
    public async Task AFailureSaysTheExitCode()
    {
        _shell.Script("dotnet test --filter Unit", 1, errors: ["1 teste falhou"]);

        var result = await Executor.ExecuteAsync(Execution(Request()), Ct);

        result.Succeeded.Should().BeFalse();
        result.Code.Should().Be(RemoteErrorCodes.ExecutionFailed);
        result.Summary.Should().Be("\"Testes\" saiu com código 1.");
    }

    [Fact]
    public async Task TheOutputGoesMaskedAndStopsAtTheCeiling()
    {
        var line = new string('x', 4000);
        _shell.Script(
            "dotnet test --filter Unit",
            0,
            ["Server=db;Password=segredo-de-verdade;", .. Enumerable.Repeat(line, CommandRunExecutor.MaxOutputBytes / line.Length + 10)]);

        var result = await Executor.ExecuteAsync(Execution(Request()), Ct);

        var output = _events.Where(item => item.Kind == RemoteEventKind.Output).ToList();
        output[0].Text.Should().NotContain("segredo-de-verdade");
        output.Sum(item => item.Text!.Length).Should().BeLessThanOrEqualTo(CommandRunExecutor.MaxOutputBytes);
        output.Should().OnlyContain(item => item.Text!.Length <= RemoteLimits.MaxEventTextLength);
        _events.Should().ContainSingle(item => item.Kind == RemoteEventKind.Warning);
        result.Data!.Value.GetProperty("outputTruncated").GetBoolean().Should().BeTrue();
    }

    private RemoteOperationRequest Request(IReadOnlyDictionary<string, string>? values = null, Guid? command = null) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        RemoteOperationKinds.CommandRun,
        JsonSerializer.SerializeToElement(
            new WorktreeExecutionRequest(DevelopmentId, command ?? _tests.Id, values ?? new Dictionary<string, string>()),
            RemoteJson.Options),
        _task.Id,
        [],
        Now,
        Now.AddMinutes(10));

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
            _time,
            approve is null ? null : (ask, _) => Task.FromResult(approve(ask)));

    private Guid Bind(DevelopmentCommand command, string? commandOverride = null)
    {
        var binding = new TagDirectoryCommandRow(
            Guid.CreateVersion7(), _directoryId, command.Id, command.Alias, command.Name, command.Command,
            0, true, commandOverride, null);
        _tagQuery.CommandDirectories.Add(new CommandDirectoryRow(
            _directoryId, _tagId, "ECO CORE", "@ecossistema-core", FakeGitClient.Repository, [binding]));
        return binding.Id;
    }
}
