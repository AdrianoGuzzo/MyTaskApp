using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Lifecycle;

/// <summary>
/// A varredura automática (§2, §6). É a única parte do app que arquiva e apaga
/// sem ninguém clicar, então o que estes testes cercam é principalmente o que
/// ela <b>não</b> pode fazer.
/// </summary>
public class LifecycleMaintenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 3, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeTaskAuditLog _audit = new();
    private readonly FakeLifecycleSweepQuery _sweep = new();
    private readonly FakeDataRetentionSettingsStore _settings = new();
    private readonly FakeTimeProvider _time = new(Now);

    private RunLifecycleMaintenanceHandler Handler() =>
        new(
            _sweep,
            _repository,
            _repository,
            _audit,
            _settings,
            _time,
            NullLogger<RunLifecycleMaintenanceHandler>.Instance);

    private TaskItem SeedConcluded(DateTimeOffset concludedAt)
    {
        var task = TaskItem.Create("Fechar o mês", concludedAt.AddDays(-1));
        task.CompleteOccurrence(task.Occurrences.Single().Id, concludedAt);

        _repository.Seed(task);
        _sweep.ReadyToArchive.Add(task.Id);

        return task;
    }

    private TaskItem SeedTrashed(DateTimeOffset deletedAt)
    {
        var task = TaskItem.Create("Coisa antiga", deletedAt.AddDays(-1));
        task.MoveToTrash(deletedAt, "adriano");

        _repository.Seed(task);
        _sweep.ReadyToPurge.Add(task.Id);

        return task;
    }

    private void EnableAutoArchive(int afterDays = 30, int trashDays = 30) =>
        _settings.Policy = new DataRetentionPolicy(true, afterDays, trashDays);

    // ------------------------------------------------------------------
    // Arquivamento automático
    // ------------------------------------------------------------------

    [Fact]
    public async Task WithAutomaticArchivingOff_NothingIsArchivedAndTheQueryIsNotEvenAsked()
    {
        var task = SeedConcluded(Now.AddDays(-90));

        var result = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        result.Archived.Should().Be(0);
        task.IsArchived.Should().BeFalse();
        _sweep.ArchiveCallCount.Should().Be(0);
    }

    /// <summary>
    /// O §2 em uma asserção: o corte é "agora menos o prazo", e o que é
    /// comparado com ele é a data de conclusão.
    /// </summary>
    [Fact]
    public async Task TheCutoffIsCountedBackFromNow_OverTheConclusionDate()
    {
        EnableAutoArchive(afterDays: 30);
        SeedConcluded(Now.AddDays(-45));

        await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        _sweep.ArchiveCutoffAsked.Should().Be(Now.AddDays(-30));
    }

    [Fact]
    public async Task AConcludedChecklistPastTheDeadline_IsArchivedAndRecordedAsAutomatic()
    {
        EnableAutoArchive();
        var task = SeedConcluded(Now.AddDays(-45));

        var result = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        result.Archived.Should().Be(1);
        task.IsArchived.Should().BeTrue();
        task.ArchivedAt.Should().Be(Now);

        _audit.Last!.Operation.Should().Be(TaskAuditOperation.Archived);
        _audit.Last.Actor.Should().Be(AuditActor.System);
        _audit.Last.ActorName.Should().BeNull();
        _audit.Last.Details.Should().Contain("automaticamente");
    }

    /// <summary>
    /// A reconferência em memória, e por que ela existe: entre a consulta e a
    /// escrita o usuário pode ter reaberto o item na tela. Nesse caso a tela
    /// ganha — que é como tem de ser.
    /// </summary>
    [Fact]
    public async Task AChecklistReopenedBetweenTheQueryAndTheWrite_IsLeftAlone()
    {
        EnableAutoArchive();
        var task = SeedConcluded(Now.AddDays(-45));

        task.ReopenOccurrence(task.Occurrences.Single().Id);

        var result = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        result.Archived.Should().Be(0);
        task.IsArchived.Should().BeFalse();
        _audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task AChecklistAlreadyInTheTrash_IsNotArchivedOnTopOfIt()
    {
        EnableAutoArchive();
        var task = SeedConcluded(Now.AddDays(-45));
        task.MoveToTrash(Now.AddDays(-1), "adriano");

        var result = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        result.Archived.Should().Be(0);
        task.ArchivedAt.Should().BeNull();
    }

    /// <summary>
    /// Idempotência (§10): a segunda passagem não encontra mais nada para
    /// fazer, mesmo com a consulta devolvendo o mesmo candidato.
    /// </summary>
    [Fact]
    public async Task RunningTheSweepTwice_ArchivesTheSameChecklistOnlyOnce()
    {
        EnableAutoArchive();
        SeedConcluded(Now.AddDays(-45));

        var first = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);
        var second = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        first.Archived.Should().Be(1);
        second.Archived.Should().Be(0);
        _audit.Entries.Should().ContainSingle();
    }

    // ------------------------------------------------------------------
    // Exclusão definitiva automática
    // ------------------------------------------------------------------

    [Fact]
    public async Task SomethingPastTheTrashRetention_IsPermanentlyDeletedByTheSystem()
    {
        _settings.Policy = new DataRetentionPolicy(false, 30, 30);
        var task = SeedTrashed(Now.AddDays(-31));

        var result = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        result.Purged.Should().Be(1);
        _repository.Tasks.Should().BeEmpty();

        _audit.Last!.Operation.Should().Be(TaskAuditOperation.PermanentlyDeleted);
        _audit.Last.Actor.Should().Be(AuditActor.System);
        _audit.Last.TaskId.Should().Be(task.Id);
        _audit.Last.TaskTitle.Should().Be("Coisa antiga");
    }

    /// <summary>
    /// A reconferência que mais importa: esta é a única operação do app sem
    /// volta. Restaurado entre a consulta e a escrita significa não apagar.
    /// </summary>
    [Fact]
    public async Task SomethingRestoredBetweenTheQueryAndTheWrite_IsNotDeleted()
    {
        _settings.Policy = new DataRetentionPolicy(false, 30, 30);
        var task = SeedTrashed(Now.AddDays(-31));

        task.RestoreFromTrash();

        var result = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        result.Purged.Should().Be(0);
        _repository.Tasks.Should().ContainSingle();
        _audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task SomethingStillInsideTheRetentionWindow_IsNotDeleted()
    {
        _settings.Policy = new DataRetentionPolicy(false, 30, 30);

        // A consulta o devolveu por engano; o handler confere o prazo de novo.
        SeedTrashed(Now.AddDays(-2));

        var result = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        result.Purged.Should().Be(0);
        _repository.Tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task ATickWithNothingToDo_SavesNothing()
    {
        EnableAutoArchive();

        var result = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        result.DidSomething.Should().BeFalse();
        _repository.SaveCount.Should().Be(0);
    }

    /// <summary>
    /// Arquivar 1 e apagar 1 é uma transação só: uma queda no meio não pode
    /// deixar metade do lote aplicada com a outra metade auditada.
    /// </summary>
    [Fact]
    public async Task TheWholeTick_IsASingleUnitOfWork()
    {
        EnableAutoArchive();
        SeedConcluded(Now.AddDays(-45));
        SeedTrashed(Now.AddDays(-31));

        var result = await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        result.Archived.Should().Be(1);
        result.Purged.Should().Be(1);
        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task TheTrashCutoff_FollowsTheConfiguredRetention()
    {
        _settings.Policy = new DataRetentionPolicy(false, 30, 7);

        await Handler().HandleAsync(new RunLifecycleMaintenance(), Ct);

        _sweep.PurgeCutoffAsked.Should().Be(Now.AddDays(-7));
    }
}
