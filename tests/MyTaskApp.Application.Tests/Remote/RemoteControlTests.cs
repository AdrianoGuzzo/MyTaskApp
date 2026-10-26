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
    public async Task TheProgressReadsEveryNewEvent_AndTheStatusAfterThem()
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
        calls.Should().Equal(["events", "events", "status"]);
    }

    private static RemoteOperationEvent Event(Guid id, long seq) =>
        new(id, seq, Now, RemoteEventKind.Output, "stdout", Text: $"linha {seq}");

    internal static RemoteOperationView View(Guid id, RemoteOperationStatus status, string? summary = null) =>
        new(id, Guid.CreateVersion7(), Notebook, Guid.CreateVersion7(), RemoteOperationKinds.CommandRun, null, status, Now, Now.AddMinutes(5), Now, null, null, summary, 0);
}
