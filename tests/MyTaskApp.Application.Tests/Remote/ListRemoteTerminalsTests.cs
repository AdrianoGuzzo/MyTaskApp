using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>A lista dos terminais de uma máquina (ADR-099): só dela, com o título da tarefa daqui.</summary>
public sealed class ListRemoteTerminalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IRemoteTerminalsApi _api = Substitute.For<IRemoteTerminalsApi>();

    private readonly FakeTaskItemRepository _tasks = new();

    private readonly Guid _machine = Guid.CreateVersion7();

    [Fact]
    public async Task OnlyThatMachinesSessions_WithTheTaskTitle_AndTheStateInWords()
    {
        var task = TaskItem.Create("Implementar autenticação", Now);
        _tasks.Seed(task);
        var mine = Report(task.Id) with { Activity = RemoteAgentActivity.Working, PermissionsBypassed = true };
        _api.ListTerminalsAsync(Arg.Any<CancellationToken>()).Returns(new RemoteTerminalSessionList(
        [
            new RemoteTerminalSessionView(_machine, mine, Now),
            new RemoteTerminalSessionView(Guid.CreateVersion7(), Report(task.Id), Now),
        ]));

        var list = await Handler().HandleAsync(new ListRemoteTerminals(_machine), Ct);

        list.Problem.Should().BeNull();
        var item = list.Items.Should().ContainSingle().Which;
        item.Title.Should().Be("Implementar autenticação");
        item.State.Should().Be("● Trabalhando");
        item.BypassWarning.Should().NotBeNull("quem segue vê o aviso antes de abrir");
        item.Target(_machine).Should().Be(new RemoteTerminalTarget(_machine, mine.SessionId, task.Id, mine.DevelopmentId!.Value));
    }

    [Fact]
    public async Task ATaskNotSyncedYet_StillShows()
    {
        _api.ListTerminalsAsync(Arg.Any<CancellationToken>()).Returns(new RemoteTerminalSessionList([new RemoteTerminalSessionView(_machine, Report(Guid.CreateVersion7()), Now)]));

        var list = await Handler().HandleAsync(new ListRemoteTerminals(_machine), Ct);

        list.Items.Should().ContainSingle().Which.Title.Should().Be("Tarefa");
    }

    [Fact]
    public async Task AServerWithoutTheRoute_SaysToUpdateIt()
    {
        _api.ListTerminalsAsync(Arg.Any<CancellationToken>()).Returns<RemoteTerminalSessionList>(_ => throw new SyncApiException(SyncFailureKind.NotFound, "não achou"));

        var list = await Handler().HandleAsync(new ListRemoteTerminals(_machine), Ct);

        list.Items.Should().BeEmpty();
        list.Problem.Should().Be(ListRemoteTerminalsHandler.ServerTooOld);
    }

    [Theory]
    [InlineData(RemoteTerminalProcess.Exited, false, RemoteAgentActivity.Working, false, "○ Encerrado")]
    [InlineData(RemoteTerminalProcess.Running, true, RemoteAgentActivity.Working, false, "◐ Esperando resposta")]
    [InlineData(RemoteTerminalProcess.Running, false, RemoteAgentActivity.WaitingForUser, false, "◐ Esperando você")]
    [InlineData(RemoteTerminalProcess.Running, false, RemoteAgentActivity.Unknown, true, "● Rodando · controlado de longe")]
    public void TheState_InWords(RemoteTerminalProcess process, bool dialog, RemoteAgentActivity activity, bool remote, string expected) =>
        ListRemoteTerminalsHandler.StateOf(Report(Guid.Empty) with { Process = process, HasDialog = dialog, Activity = activity, RemotelyControlled = remote })
            .Should().Be(expected);

    private static RemoteTerminalSessionReport Report(Guid taskId) =>
        new(Guid.CreateVersion7(), taskId, Guid.CreateVersion7(), RemoteTerminalProcess.Running, null, RemoteAgentActivity.Unknown, null, false, true, false, false, Now, Now);

    private ListRemoteTerminalsHandler Handler() => new(_api, _tasks);
}
