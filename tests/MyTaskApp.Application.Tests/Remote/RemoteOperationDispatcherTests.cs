using System.Text.Json;
using MyTaskApp.Application.Remote;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>O despachante de ponta a ponta (4.3 e 4.5), contra a fila em memória que confere o ciclo de vida.</summary>
public sealed class RemoteOperationDispatcherTests
{
    [Fact]
    public async Task RunsAnAcceptedOperationAndTellsTheServerEachStepInOrder()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Executor.Behavior = async (execution, token) =>
        {
            await execution.EmitAsync(RemoteEventKind.StepStarted, step: "git", cancellationToken: token);
            await execution.EmitAsync(RemoteEventKind.Output, "git version 2.51", cancellationToken: token);
            return RemoteOperationResult.Success("Git e Claude encontrados.");
        };
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));
        Assert.Equal(
            [RemoteOperationStatus.Accepted, RemoteOperationStatus.Running, RemoteOperationStatus.Succeeded],
            kit.Server.StatusesOf(request.OperationId));

        var events = kit.Server.EventsOf(request.OperationId);
        Assert.Equal([1L, 2L, 3L], events.Select(item => item.Seq));
        Assert.Equal([RemoteEventKind.StepStarted, RemoteEventKind.Output, RemoteEventKind.Result], events.Select(item => item.Kind));
        Assert.Equal("Git e Claude encontrados.", events[^1].Text);

        var entry = Assert.Single(kit.Journal.All);
        Assert.False(entry.HasUnreported);
        Assert.Equal("git", entry.Step);
    }

    [Fact]
    public async Task RunsUnderTheRemoteOriginSoTheAuditSaysWhoAsked()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);
        await kit.WaitFinishedAsync(request.OperationId);

        Assert.Equal("Remoto: Pixel 8", kit.Executor.SeenOrigin);
    }

    [Fact]
    public async Task ARejectedRequestNeverRunsAndTheServerLearnsWhy()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Executor.Refusal = RemoteErrorCodes.NotInCatalog;
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, kit.Executor.Runs);
        Assert.Equal(RemoteOperationStatus.Rejected, kit.Server.StatusOf(request.OperationId));
        Assert.Equal(RemoteErrorCodes.NotInCatalog, Assert.Single(kit.Server.Updates[request.OperationId]).Code);
    }

    [Fact]
    public async Task AnUntrustedRequestIsRejectedWithoutTouchingTheJournal()
    {
        await using var kit = new RemoteDispatchKit();
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RemoteOperationStatus.Rejected, kit.Server.StatusOf(request.OperationId));
        Assert.Empty(kit.Journal.All);
        Assert.Equal(0, kit.Executor.Runs);
    }

    [Fact]
    public async Task DoesNotRunWhenTheServerExpiredItBeforeTheAcceptArrived()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var request = kit.Request();
        kit.Queue(request);
        kit.Server.ExpireOnAccept.Add(request.OperationId);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, kit.Executor.Runs);
        var entry = Assert.Single(kit.Journal.All);
        Assert.Equal(RemoteOperationStatus.Expired, entry.Status);
        Assert.False(entry.HasUnreported);
    }

    [Fact]
    public async Task WithoutTheAcceptConfirmedItWaitsAndRunsOnTheNextPoll()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var request = kit.Request();
        var envelope = kit.Queue(request);

        // A rede cai entre a busca e o aceite: o diário diz aceita, o servidor não sabe.
        var admission = await kit.AdmitAsync(envelope);
        Assert.IsType<RemoteAdmission.Admitted>(admission);
        kit.Server.Offline = true;
        await Assert.ThrowsAnyAsync<Exception>(() => kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, kit.Executor.Runs);

        kit.Server.Offline = false;
        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));
        Assert.Equal(1, kit.Executor.Runs);
    }

    [Fact]
    public async Task OfflineWhileRunningTheJournalKeepsEverythingAndTheFlushTellsItInOrder()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Executor.Behavior = async (execution, token) =>
        {
            // A rede cai logo depois do Rodando.
            kit.Server.Offline = true;

            for (var line = 1; line <= 5; line++)
            {
                await execution.EmitAsync(RemoteEventKind.Output, $"linha {line}", cancellationToken: token);
            }

            return RemoteOperationResult.Success("5 linhas.");
        };
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);
        await kit.WaitFinishedAsync(request.OperationId);

        Assert.Equal([RemoteOperationStatus.Accepted, RemoteOperationStatus.Running], kit.Server.StatusesOf(request.OperationId));
        Assert.Empty(kit.Server.EventsOf(request.OperationId));
        Assert.True(Assert.Single(kit.Journal.All).HasUnreported);

        kit.Server.Offline = false;
        await kit.Dispatcher.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RemoteOperationStatus.Succeeded, kit.Server.StatusOf(request.OperationId));
        Assert.Equal(Enumerable.Range(1, 6).Select(seq => (long)seq), kit.Server.EventsOf(request.OperationId).Select(item => item.Seq));
        Assert.False(Assert.Single(kit.Journal.All).HasUnreported);
    }

    [Fact]
    public async Task ACancelFromTheControllerStopsTheRunAndEndsAsCanceled()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Executor.Behavior = async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return RemoteOperationResult.Success();
        };
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        kit.Server.RequestCancel(request.OperationId);
        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RemoteOperationStatus.Canceled, await kit.WaitFinishedAsync(request.OperationId));
        Assert.Equal(RemoteOperationStatus.Canceled, kit.Server.StatusOf(request.OperationId));
    }

    [Fact]
    public async Task ACancelBeforeItStartsEndsItWithoutRunning()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var request = kit.Request();
        kit.Queue(request);
        kit.Server.RequestCancel(request.OperationId);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, kit.Executor.Runs);
        Assert.Equal(RemoteOperationStatus.Canceled, kit.Server.StatusOf(request.OperationId));
    }

    [Fact]
    public async Task TheAgentClosingMidRunIsInterruptedNotCanceled()
    {
        var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Executor.Behavior = async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return RemoteOperationResult.Success();
        };
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await kit.DisposeAsync();

        Assert.Equal(RemoteOperationStatus.Interrupted, kit.Server.StatusOf(request.OperationId));
        Assert.Equal(RemoteErrorCodes.Interrupted, kit.Server.Updates[request.OperationId][^1].Code);
    }

    [Fact]
    public async Task TheRuleOfTheUseCaseEndsAsAFailureWithTheScreenMessage()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Executor.Behavior = (_, _) => throw new DomainException("O comando mudou e precisa ser revisto.");
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RemoteOperationStatus.Failed, await kit.WaitFinishedAsync(request.OperationId));
        var final = kit.Server.Updates[request.OperationId][^1];
        Assert.Equal(RemoteErrorCodes.PolicyRefused, final.Code);
        Assert.Equal("O comando mudou e precisa ser revisto.", final.Summary);
    }

    [Fact]
    public async Task AnUnexpectedErrorDoesNotLeakTheDetailToTheController()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Executor.Behavior = (_, _) => throw new InvalidOperationException(@"C:\Users\alguem\segredo.txt");
        var request = kit.Request();
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RemoteOperationStatus.Failed, await kit.WaitFinishedAsync(request.OperationId));
        var final = kit.Server.Updates[request.OperationId][^1];
        Assert.Equal(RemoteErrorCodes.ExecutionFailed, final.Code);
        Assert.DoesNotContain("segredo", final.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameRequestListedAgainNeverRunsTwice()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var request = kit.Request();
        var envelope = kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);
        await kit.WaitFinishedAsync(request.OperationId);

        // Um servidor que devolve o mesmo envelope como novo (reenvio, ou má-fé).
        kit.Server.Enqueue(request.OperationId, envelope);
        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, kit.Executor.Runs);
    }

    [Fact]
    public async Task ATaskStillComingByTheSyncWakesTheSyncAndRunsWhenItArrives()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var taskId = Guid.CreateVersion7();
        var request = kit.Request(request => request with { TaskId = taskId });
        kit.Queue(request);

        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, kit.Trigger.Signals);
        Assert.Empty(kit.Journal.All);

        kit.Entities.Known.Add(taskId);
        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));
    }

    [Fact]
    public async Task AfterACrashWhatWasRunningIsInterruptedWithTheStep()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var request = kit.Request();
        kit.Queue(request);
        await kit.Dispatcher.PollAsync(TestContext.Current.CancellationToken);
        await kit.WaitFinishedAsync(request.OperationId);

        // Outra, que estava rodando no diário quando o agente caiu.
        var crashed = kit.Request();
        kit.Queue(crashed);
        _ = await kit.Server.ListAgentOperationsAsync(TestContext.Current.CancellationToken);
        await kit.Server.UpdateStatusAsync(crashed.OperationId, new OperationStatusUpdate(RemoteOperationStatus.Accepted), TestContext.Current.CancellationToken);
        await kit.Server.UpdateStatusAsync(crashed.OperationId, new OperationStatusUpdate(RemoteOperationStatus.Running), TestContext.Current.CancellationToken);
        var now = kit.Time.GetUtcNow();
        kit.Journal.Seed(new RemoteJournalEntry(
            crashed.OperationId, kit.Phone.DeviceId, "Pixel 8", crashed.Kind, null, kit.Phone.Sign(crashed).Jws,
            RemoteOperationStatus.Running, RemoteOperationStatus.Running, null, null, "worktree", 1, 0, now, now));

        await kit.Dispatcher.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RemoteOperationStatus.Interrupted, kit.Server.StatusOf(crashed.OperationId));
        var final = kit.Server.Updates[crashed.OperationId][^1];
        Assert.Equal(RemoteErrorCodes.Interrupted, final.Code);
        Assert.Contains("worktree", final.Summary, StringComparison.Ordinal);

        // O que terminou antes da queda não muda.
        Assert.Equal(RemoteOperationStatus.Succeeded, kit.Server.StatusOf(request.OperationId));
    }

    [Theory]
    [InlineData(null, RemoteOperationStatus.Accepted, new[] { RemoteOperationStatus.Accepted })]
    [InlineData(null, RemoteOperationStatus.Rejected, new[] { RemoteOperationStatus.Rejected })]
    [InlineData(null, RemoteOperationStatus.Succeeded, new[] { RemoteOperationStatus.Accepted, RemoteOperationStatus.Running, RemoteOperationStatus.Succeeded })]
    [InlineData(null, RemoteOperationStatus.Canceled, new[] { RemoteOperationStatus.Accepted, RemoteOperationStatus.Canceled })]
    [InlineData(RemoteOperationStatus.Accepted, RemoteOperationStatus.Interrupted, new[] { RemoteOperationStatus.Interrupted })]
    [InlineData(RemoteOperationStatus.Running, RemoteOperationStatus.Running, new RemoteOperationStatus[0])]
    [InlineData(RemoteOperationStatus.Succeeded, RemoteOperationStatus.Running, new RemoteOperationStatus[0])]
    public void TheStatusPathIsTheShortestTheLifecycleLetsTheAgentTake(
        RemoteOperationStatus? reported,
        RemoteOperationStatus target,
        RemoteOperationStatus[] expected)
    {
        Assert.Equal(expected, RemoteOperationDispatcher.StatusPath(reported, target));
    }

    [Fact]
    public void ABatchStaysUnderTheBodyLimitOfTheServer()
    {
        var id = Guid.CreateVersion7();
        var full = new string('x', RemoteLimits.MaxEventTextLength);
        IReadOnlyList<RemoteOperationEvent> pending =
        [
            .. Enumerable.Range(1, RemoteLimits.MaxEventsPerBatch)
                .Select(seq => new RemoteOperationEvent(id, seq, DateTimeOffset.UnixEpoch, RemoteEventKind.Output, Text: full)),
        ];

        var batch = RemoteOperationDispatcher.Fitting(pending);

        Assert.InRange(batch.Count, 1, pending.Count - 1);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(new EventBatch(id, batch), RemoteJson.Options).Length < 1024 * 1024);
        Assert.Equal(pending.Take(batch.Count), batch);
    }
}
