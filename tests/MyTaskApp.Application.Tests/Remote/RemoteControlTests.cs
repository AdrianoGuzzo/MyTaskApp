using System.Text.Json;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>A tela "Operação" do controlador (E3): o que dá para pedir à outra máquina e como vai o pedido.</summary>
public sealed class RemoteControlTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Notebook = Guid.Parse("0199a000-0000-7000-8000-000000000002");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IRemoteOperationsApi _remote = Substitute.For<IRemoteOperationsApi>();

    private readonly FakeTaskItemRepository _tasks = new();

    [Fact]
    public async Task TheEnvironmentsAreNamedByWhatTheSyncBroughtHere()
    {
        var task = TaskItem.Create("Validar antimicrobianos", Now);
        var development = task.BeginDevelopment(null, @"C:\repos\eco", "origin/develop", "feature/ECO-12", @"C:\repos\eco-ECO-12", Now);
        _tasks.Seed(task);

        var tests = new CatalogCommand(Guid.CreateVersion7(), "Testes", CommandScope.Global, null, false, []);
        var build = new CatalogCommand(Guid.CreateVersion7(), "Build", CommandScope.Global, null, true, []);
        var unknown = Guid.CreateVersion7();
        _remote.GetCatalogAsync(Notebook, Arg.Any<CancellationToken>()).Returns(new AgentCatalog("h", [], [tests, build], [], [])
        {
            Environments =
            [
                new CatalogEnvironment(Guid.CreateVersion7(), Guid.CreateVersion7(), [tests.CommandId]),
                new CatalogEnvironment(task.Id, development.Id, [tests.CommandId, build.CommandId, unknown]),
            ],
        });

        var view = await new GetRemoteControlHandler(_remote, _tasks).HandleAsync(new GetRemoteControl(Notebook), Ct);

        var first = view.Environments[0];
        first.TaskTitle.Should().Be("Validar antimicrobianos");
        first.Branch.Should().Be("feature/ECO-12");
        first.Commands.Select(command => command.Name).Should().Equal(["Build", "Testes"], "o id fora do catálogo não vira botão");
        view.Environments[1].TaskTitle.Should().BeNull("a tarefa ainda não chegou aqui: vai por último");
    }

    [Fact]
    public void RunningACommand_SendsOnlyIds_AndTheFilledValues_AndWaitsForTheTask()
    {
        var environment = new RemoteEnvironmentView(Guid.CreateVersion7(), Guid.CreateVersion7(), "Validar", "feature/1", []);
        var command = new CatalogCommand(Guid.CreateVersion7(), "Testes", CommandScope.Global, null, false, [new CatalogParameter("filtro", false, "Unit")]);

        var request = RemoteRequests.RunCommand(Notebook, environment, command, new Dictionary<string, string> { ["filtro"] = " Api ", ["outro"] = " " });

        request.Kind.Should().Be(RemoteOperationKinds.CommandRun);
        request.TaskId.Should().Be(environment.TaskId);
        request.RequiredEntities.Should().Equal([environment.TaskId]);
        var payload = request.Payload.Deserialize<WorktreeExecutionRequest>(RemoteJson.Options)!;
        payload.DevelopmentId.Should().Be(environment.DevelopmentId);
        payload.CommandId.Should().Be(command.CommandId);
        payload.Parameters.Should().Equal(new Dictionary<string, string> { ["filtro"] = "Api" });
    }

    [Fact]
    public void PreparingAndRemoving_NameTheTask_AndPreparingWaitsForIt()
    {
        var environment = new RemoteEnvironmentView(Guid.CreateVersion7(), Guid.CreateVersion7(), "Validar", "feature/1", []);
        var asked = new EnvironmentProvisioningRequest(environment.TaskId, Guid.CreateVersion7(), null, null, null, WorktreeConflictPolicy.Fail, true);

        var provision = RemoteRequests.Provision(Notebook, asked);
        var remove = RemoteRequests.RemoveWorktree(Notebook, environment);

        provision.Kind.Should().Be(RemoteOperationKinds.WorktreeProvision);
        provision.RequiredEntities.Should().Equal([environment.TaskId], "a tarefa recém-criada aqui pode não ter chegado lá");
        provision.Payload.Deserialize<EnvironmentProvisioningRequest>(RemoteJson.Options).Should().Be(asked);
        remove.Kind.Should().Be(RemoteOperationKinds.WorktreeRemove);
        remove.TaskId.Should().Be(environment.TaskId);
        remove.Payload.Deserialize<WorktreeRemovalRequest>(RemoteJson.Options)
            .Should().Be(new WorktreeRemovalRequest(environment.TaskId, environment.DevelopmentId, Force: false));
    }

    [Fact]
    public void TheClaudeRequests_CarryOnlyIdsAndThePrompt()
    {
        var environment = new RemoteEnvironmentView(Guid.CreateVersion7(), Guid.CreateVersion7(), "Validar", "feature/1", []);
        var conversation = Guid.CreateVersion7();

        var start = RemoteRequests.StartAgent(Notebook, environment, "edit-test", "  Rode os testes  ");
        var prompt = RemoteRequests.PromptAgent(Notebook, environment, conversation, "E agora?");
        var stop = RemoteRequests.StopAgent(Notebook, environment, conversation);

        start.Kind.Should().Be(RemoteOperationKinds.AgentStart);
        start.RequiredEntities.Should().Equal([environment.TaskId]);
        start.Payload.Deserialize<ClaudeCodeSessionRequest>(RemoteJson.Options)!.Should().BeEquivalentTo(
            new ClaudeCodeSessionRequest(environment.DevelopmentId, "edit-test", null, null, new PromptRequest(null, "Rode os testes")));
        prompt.Kind.Should().Be(RemoteOperationKinds.AgentPrompt);
        prompt.Payload.Deserialize<PromptRequest>(RemoteJson.Options).Should().Be(new PromptRequest(conversation, "E agora?"));
        stop.Kind.Should().Be(RemoteOperationKinds.AgentStop);
        stop.Payload.Deserialize<AgentStopRequest>(RemoteJson.Options).Should().Be(new AgentStopRequest(conversation));
        new[] { start.TaskId, prompt.TaskId, stop.TaskId }.Should().AllBeEquivalentTo(environment.TaskId);
    }

    [Fact]
    public async Task TheProgressReadsTheStatusFirst_ThenEveryEventUpToIt()
    {
        var id = Guid.CreateVersion7();
        var calls = new List<string>();
        _remote.ListEventsAsync(id, 2, null, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("events");
            return new RemoteEventPage([Event(id, 3), Event(id, 4)], 4, HasMore: true);
        });
        _remote.ListEventsAsync(id, 4, null, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("events");
            return new RemoteEventPage([Event(id, 4), Event(id, 5)], 5, HasMore: false);
        });
        _remote.GetAsync(id, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("status");
            return View(id, RemoteOperationStatus.Succeeded);
        });

        var progress = await new GetRemoteOperationProgressHandler(_remote).HandleAsync(new GetRemoteOperationProgress(id, 2), Ct);

        progress.Events.Select(item => item.Seq).Should().Equal([3, 4, 5], "o repetido não volta duas vezes");
        progress.After.Should().Be(5);
        progress.IsFinished.Should().BeTrue();
        progress.HasMore.Should().BeFalse();
        calls.Should().Equal(["status", "events", "events"], "o agente manda a saída antes do estado final: lido antes, o \"terminou\" já tem toda ela atrás");
    }

    /// <summary>
    /// A saída despejou mais que as páginas de uma leitura: o estado já é final,
    /// mas quem acompanha ainda não pode parar, senão perde o fim.
    /// </summary>
    [Fact]
    public async Task AFinalStatusWithMorePages_IsNotFinishedUntilTheLastPage()
    {
        var id = Guid.CreateVersion7();
        _remote.GetAsync(id, Arg.Any<CancellationToken>()).Returns(View(id, RemoteOperationStatus.Succeeded));
        _remote.ListEventsAsync(id, Arg.Any<long>(), null, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var after = call.ArgAt<long>(1);
                return new RemoteEventPage([Event(id, after + 1)], after + 1, HasMore: after + 1 < 10);
            });
        var handler = new GetRemoteOperationProgressHandler(_remote);

        var first = await handler.HandleAsync(new GetRemoteOperationProgress(id, 0), Ct);
        first.Events.Should().HaveCount(GetRemoteOperationProgressHandler.MaxPages);
        first.HasMore.Should().BeTrue();
        first.IsFinished.Should().BeFalse("sobraram eventos no servidor");

        var after = first.After;
        RemoteOperationProgress last;

        do
        {
            last = await handler.HandleAsync(new GetRemoteOperationProgress(id, after), Ct);
            after = last.After;
        }
        while (!last.IsFinished);

        after.Should().Be(10, "a última linha chega antes do fim");
    }

    private static RemoteOperationEvent Event(Guid id, long seq) =>
        new(id, seq, Now, RemoteEventKind.Output, "stdout", Text: $"linha {seq}");

    internal static RemoteOperationView View(Guid id, RemoteOperationStatus status, string? summary = null) =>
        new(id, Guid.CreateVersion7(), Notebook, Guid.CreateVersion7(), RemoteOperationKinds.CommandRun, null, status, Now, Now.AddMinutes(5), Now, null, null, summary, 0);

    [Fact]
    public void TheConversation_IsTheLastOneTheEventsMention_AndStoppedOnesDoNotTakePrompts()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var operation = Guid.CreateVersion7();

        RemoteOperationEvent Event(long seq, object? data) =>
            new(operation, seq, Now, RemoteEventKind.Result, Data: data is null ? null : JsonSerializer.SerializeToElement(data));

        RemoteOperationTexts.Conversation([Event(1, null)]).Should().BeNull();
        RemoteOperationTexts.Conversation([Event(1, new { agentSessionId = first })]).Should().Be((first, true));
        RemoteOperationTexts.Conversation([Event(1, new { agentSessionId = first }), Event(2, new { agentSessionId = second, canContinue = false })])
            .Should().Be((second, false));
    }

    [Fact]
    public void TheEventLines_AreTheSameOnTheDesktopAndThePhone()
    {
        var operation = Guid.CreateVersion7();

        RemoteOperationTexts.Line(new(operation, 1, Now, RemoteEventKind.Output, Text: "ok")).Should().Be("ok");
        RemoteOperationTexts.Line(new(operation, 2, Now, RemoteEventKind.AgentMessage, Text: "Pronto")).Should().Be("Claude: Pronto");
        RemoteOperationTexts.Line(new(operation, 3, Now, RemoteEventKind.StatusChanged, Status: RemoteOperationStatus.Running)).Should().BeNull();
        RemoteOperationTexts.Line(new(operation, 4, Now, RemoteEventKind.ToolUse, Text: "Read")).Should().Be("» usou Read");
        RemoteOperationTexts.Line(new(operation, 5, Now, RemoteEventKind.Warning, Text: "lento")).Should().Be("» lento");
        RemoteOperationTexts.Line(new(operation, 6, Now, RemoteEventKind.ApprovalRequested, Text: "rm")).Should().Be("» Esperando aprovação: rm");
        RemoteOperationTexts.Line(new(operation, 7, Now, RemoteEventKind.ApprovalResolved, Text: "Permitido")).Should().Be("» Permitido");
        RemoteOperationTexts.Line(new(operation, 8, Now, RemoteEventKind.StepStarted, Text: "Worktree")).Should().Be("» Worktree");
        RemoteOperationTexts.Line(new(operation, 9, Now, RemoteEventKind.StepStarted)).Should().BeNull();
    }

    /// <summary>Cada estado tem a frase dele, nas duas telas; nenhum cai no nome do enum.</summary>
    [Fact]
    public void EveryStatus_HasItsOwnSentence_AndACancelInFlightSaysSo()
    {
        var sentences = Enum.GetValues<RemoteOperationStatus>()
            .Select(status => RemoteOperationTexts.Status(View(Guid.CreateVersion7(), status)))
            .ToList();

        sentences.Should().OnlyHaveUniqueItems();
        sentences.Should().NotContain(Enum.GetNames<RemoteOperationStatus>());
        RemoteOperationTexts.Status(View(Guid.CreateVersion7(), RemoteOperationStatus.Running) with { CancelRequestedAt = Now })
            .Should().Be("Cancelando…");
    }

    [Fact]
    public void ACopyProfile_SaysFromWhereToWhere_AndWarnsWhenTheSourceIsProduction()
    {
        var production = new CatalogDatabase(Guid.CreateVersion7(), "Produção", "CriticalProduction", true, false);
        var local = new CatalogDatabase(Guid.CreateVersion7(), "Local", "Development", true, true);
        var databases = new Dictionary<Guid, CatalogDatabase> { [production.ConnectionId] = production, [local.ConnectionId] = local };

        RemoteOperationTexts.CopyProfile(new(Guid.CreateVersion7(), "Diária", production.ConnectionId, local.ConnectionId, true), databases)
            .Should().Be("Diária (Produção → Local, produção: pede aprovação)");
        RemoteOperationTexts.CopyProfile(new(Guid.CreateVersion7(), "Entre locais", local.ConnectionId, local.ConnectionId, false), databases)
            .Should().Be("Entre locais (Local → Local)");
        RemoteOperationTexts.CopyProfile(new(Guid.CreateVersion7(), "Sumida", Guid.CreateVersion7(), local.ConnectionId, false), databases)
            .Should().Be("Sumida (? → Local)", "a conexão saiu do catálogo depois");
    }
}
