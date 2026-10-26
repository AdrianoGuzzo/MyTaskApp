using MyTaskApp.Application.Remote;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>Cada operação remota de uma tarefa deixa uma linha na trilha dela (T-21), com quem pediu e como acabou.</summary>
public sealed class RemoteOperationAuditTests
{
    [Fact]
    public async Task AFinishedOperationLeavesALineWithWhoAskedAndHowItEnded()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var task = TaskWith(kit);
        var request = kit.Request(request => request with { TaskId = task.Id });
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));

        var line = Assert.Single(kit.Audit.Entries);
        Assert.Equal(task.Id, line.TaskId);
        Assert.Equal(TaskAuditOperation.RemoteOperation, line.Operation);
        Assert.Equal(AuditActor.User, line.Actor);
        Assert.Equal("Remoto: Pixel 8", line.ActorName);
        Assert.Equal("Ver o ambiente: terminou bem — Tudo certo.", line.Details);
    }

    [Fact]
    public async Task ARefusalAlsoLeavesALineWithTheReason()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var task = TaskWith(kit);
        kit.Executor.Refusal = RemoteErrorCodes.PolicyRefused;
        kit.Executor.RefusalSummary = "Este comando não está liberado para rodar de longe nesta máquina.";
        var request = kit.Request(request => request with { TaskId = task.Id });
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, kit.Executor.Runs);
        Assert.Equal(
            "Ver o ambiente: recusada nesta máquina — Este comando não está liberado para rodar de longe nesta máquina.",
            Assert.Single(kit.Audit.Entries).Details);
    }

    [Fact]
    public async Task WithoutATaskThereIsNoTrailToWriteOn()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));

        Assert.Empty(kit.Audit.Entries);
    }

    [Theory]
    [InlineData(RemoteOperationKinds.CommandRun, RemoteOperationStatus.Canceled, null, "Rodar comando: cancelada por quem pediu.")]
    [InlineData(RemoteOperationKinds.WorktreeProvision, RemoteOperationStatus.Interrupted, "O agente parou durante \"worktree\".", "Preparar o ambiente: interrompida nesta máquina — O agente parou durante \"worktree\".")]
    public void TheLineSaysWhatAndHow(string kind, RemoteOperationStatus status, string? summary, string expected) =>
        Assert.Equal(expected, RecordRemoteOperationHandler.Describe(kind, status, summary));

    private static TaskItem TaskWith(RemoteDispatchKit kit)
    {
        var task = TaskItem.Create("Validar antimicrobianos", kit.Time.GetUtcNow());
        kit.Tasks.Seed(task);
        kit.Entities.Known.Add(task.Id);
        return task;
    }
}
