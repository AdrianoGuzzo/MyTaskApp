using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Lifecycle;

/// <summary>
/// Os casos de uso do ciclo de vida (§1, §4, §5, §7). O que se afirma aqui é
/// sempre o par: o que mudou no agregado <b>e</b> o que ficou registrado — sem
/// os dois juntos, o §8 seria uma promessa que ninguém confere.
/// </summary>
public class ChecklistLifecycleHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeTaskAuditLog _audit = new();
    private readonly FakeCurrentUser _user = new("adriano");
    private readonly FakeTimeProvider _time = new(Now);

    private TaskItem Seed(ReminderPolicy? reminder = null)
    {
        var task = TaskItem.Create("Fechar o mês", Now, reminder: reminder);
        _repository.Seed(task);
        return task;
    }

    private ArchiveChecklistHandler Archive() =>
        new(
            _repository,
            _repository,
            _audit,
            _user,
            _time,
            NullLogger<ArchiveChecklistHandler>.Instance);

    private RestoreChecklistHandler Restore() =>
        new(
            _repository,
            _repository,
            _audit,
            _user,
            TestClock.Over(_time),
            _time,
            NullLogger<RestoreChecklistHandler>.Instance);

    private MoveChecklistToTrashHandler Trash() =>
        new(
            _repository,
            _repository,
            _audit,
            _user,
            _time,
            NullLogger<MoveChecklistToTrashHandler>.Instance);

    private RestoreChecklistFromTrashHandler Untrash() =>
        new(
            _repository,
            _repository,
            _audit,
            _user,
            TestClock.Over(_time),
            _time,
            NullLogger<RestoreChecklistFromTrashHandler>.Instance);

    private PurgeChecklistHandler Purge() =>
        new(
            _repository,
            _repository,
            _audit,
            _user,
            _time,
            NullLogger<PurgeChecklistHandler>.Instance);

    [Fact]
    public async Task Archiving_TakesItOutOfTheListAndRecordsWhoDidIt()
    {
        var task = Seed();

        await Archive().HandleAsync(new ArchiveChecklist(task.Id), Ct);

        task.Lifecycle.Should().Be(TaskLifecycle.Archived);
        _repository.SaveCount.Should().Be(1);

        _audit.Last!.Operation.Should().Be(TaskAuditOperation.Archived);
        _audit.Last.Actor.Should().Be(AuditActor.User);
        _audit.Last.ActorName.Should().Be("adriano");
    }

    /// <summary>
    /// A auditoria entra na mesma unidade de trabalho da operação. Quando a
    /// regra recusa, nada é gravado — nem a mudança, nem o registro dela.
    /// </summary>
    [Fact]
    public async Task ArchivingSomethingAlreadyArchived_ChangesNothingAndRecordsNothing()
    {
        var task = Seed();
        await Archive().HandleAsync(new ArchiveChecklist(task.Id), Ct);

        var again = async () => await Archive().HandleAsync(new ArchiveChecklist(task.Id), Ct);

        await again.Should().ThrowAsync<DomainException>();
        _repository.SaveCount.Should().Be(1);
        _audit.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Restoring_BringsItBackAndRearmsTheReminderFromNow()
    {
        var task = Seed(ReminderPolicy.Default);
        var occurrence = task.Occurrences.Single();

        await Archive().HandleAsync(new ArchiveChecklist(task.Id), Ct);
        occurrence.Reminder.NextFireAtUtc.Should().BeNull();

        _time.Advance(TimeSpan.FromHours(2));

        await Restore().HandleAsync(new RestoreChecklist(task.Id), Ct);

        task.Lifecycle.Should().Be(TaskLifecycle.Active);
        occurrence.Reminder.NextFireAtUtc.Should().NotBeNull();

        // Do instante atual, não do que ficou para trás: ressuscitar um horário
        // vencido faria o aviso tocar na hora, do nada.
        occurrence.Reminder.NextFireAtUtc.Should().BeOnOrAfter(_time.GetUtcNow());

        _audit.Operations.Should().Contain(TaskAuditOperation.Restored);
    }

    [Fact]
    public async Task MovingToTrash_KeepsEverythingAndStampsTheDeletion()
    {
        var task = Seed();

        await Trash().HandleAsync(new MoveChecklistToTrash(task.Id), Ct);

        task.Lifecycle.Should().Be(TaskLifecycle.Trashed);
        task.DeletedAt.Should().Be(Now);
        task.DeletedBy.Should().Be("adriano");

        // O registro não saiu do repositório: exclusão reversível não remove.
        _repository.Tasks.Should().ContainSingle();
        _audit.Last!.Operation.Should().Be(TaskAuditOperation.MovedToTrash);
    }

    [Fact]
    public async Task RestoringFromTrash_ReturnsAnArchivedChecklistToTheArchive()
    {
        var task = Seed(ReminderPolicy.Default);
        await Archive().HandleAsync(new ArchiveChecklist(task.Id), Ct);
        await Trash().HandleAsync(new MoveChecklistToTrash(task.Id), Ct);

        await Untrash().HandleAsync(new RestoreChecklistFromTrash(task.Id), Ct);

        task.Lifecycle.Should().Be(TaskLifecycle.Archived);

        // Continua calado: arquivado não cobra atenção.
        task.Occurrences.Single().Reminder.NextFireAtUtc.Should().BeNull();
        _audit.Operations.Should().Contain(TaskAuditOperation.RestoredFromTrash);
    }

    [Fact]
    public async Task RestoringFromTrash_RearmsAChecklistThatWasNeverArchived()
    {
        var task = Seed(ReminderPolicy.Default);
        await Trash().HandleAsync(new MoveChecklistToTrash(task.Id), Ct);

        await Untrash().HandleAsync(new RestoreChecklistFromTrash(task.Id), Ct);

        task.Lifecycle.Should().Be(TaskLifecycle.Active);
        task.Occurrences.Single().Reminder.NextFireAtUtc.Should().NotBeNull();
    }

    /// <summary>
    /// O §10 na camada de aplicação: nenhum caso de uso apaga de vez algo que
    /// ainda está na lista principal, por mais que o chamem direto.
    /// </summary>
    [Fact]
    public async Task PurgingAnActiveChecklist_IsRefusedAndNothingIsRemoved()
    {
        var task = Seed();

        var purge = async () => await Purge().HandleAsync(new PurgeChecklist(task.Id), Ct);

        await purge.Should().ThrowAsync<DomainException>()
            .WithMessage("*arquivado ou na lixeira*");

        _repository.Tasks.Should().ContainSingle();
        _audit.Entries.Should().BeEmpty();
    }

    /// <summary>
    /// O teste que justifica a ausência de chave estrangeira na auditoria: a
    /// linha é escrita com o título em mãos, antes de o checklist deixar de
    /// existir, e continua respondendo depois.
    /// </summary>
    [Fact]
    public async Task Purging_RemovesTheChecklistButKeepsTheTrail()
    {
        var task = Seed();
        await Trash().HandleAsync(new MoveChecklistToTrash(task.Id), Ct);

        await Purge().HandleAsync(new PurgeChecklist(task.Id), Ct);

        _repository.Tasks.Should().BeEmpty();

        var trail = await _audit.GetForTaskAsync(task.Id, Ct);

        trail.Should().Contain(entry => entry.Operation == TaskAuditOperation.PermanentlyDeleted);
        trail.First().TaskTitle.Should().Be("Fechar o mês");
    }

    [Fact]
    public async Task PurgingFromTheArchive_IsAllowedAndSaysSoInTheTrail()
    {
        var task = Seed();
        await Archive().HandleAsync(new ArchiveChecklist(task.Id), Ct);

        await Purge().HandleAsync(new PurgeChecklist(task.Id), Ct);

        _repository.Tasks.Should().BeEmpty();
        _audit.Last!.Details.Should().Contain("arquivo");
    }

    [Fact]
    public async Task AnUnknownChecklist_IsReportedAsABusinessFailure()
    {
        var archive = async () =>
            await Archive().HandleAsync(new ArchiveChecklist(Guid.CreateVersion7()), Ct);

        await archive.Should().ThrowAsync<DomainException>().WithMessage("*não encontrada*");
        _audit.Entries.Should().BeEmpty();
    }
}
