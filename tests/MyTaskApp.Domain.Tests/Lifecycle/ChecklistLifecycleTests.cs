using MyTaskApp.Domain;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Lifecycle;

/// <summary>
/// O ciclo de vida do checklist (§1, §4, §5, §9, §10). O que estes testes
/// guardam é a regra que o briefing repete de três formas diferentes: arquivar
/// não é excluir, e nada sai do alcance do usuário por um gesto só.
/// </summary>
public class ChecklistLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private static TaskItem NewChecklist() => TaskItem.Create("Fechar o mês", Now);

    [Fact]
    public void ANewChecklist_IsActive()
    {
        NewChecklist().Lifecycle.Should().Be(TaskLifecycle.Active);
    }

    [Fact]
    public void CompletingEveryOccurrence_MakesItCompleted()
    {
        var task = NewChecklist();

        task.CompleteOccurrence(task.Occurrences.Single().Id, Now);

        task.Lifecycle.Should().Be(TaskLifecycle.Completed);
        task.ConcludedAt.Should().Be(Now);
    }

    [Fact]
    public void Archiving_TakesItOutOfTheMainListWithoutLosingAnything()
    {
        var task = NewChecklist();
        var occurrenceId = task.Occurrences.Single().Id;

        task.Archive(Now);

        task.Lifecycle.Should().Be(TaskLifecycle.Archived);
        task.ArchivedAt.Should().Be(Now);

        // Arquivar não é excluir: o item continua lá, com o mesmo id.
        task.Occurrences.Should().ContainSingle()
            .Which.Id.Should().Be(occurrenceId);
    }

    [Fact]
    public void Archiving_SilencesTheReminderSoAGuardedChecklistStopsNagging()
    {
        var task = NewChecklist();
        var occurrence = task.Occurrences.Single();
        occurrence.ArmReminder(Now.AddMinutes(30));

        task.Archive(Now);

        occurrence.Reminder.NextFireAtUtc.Should().BeNull();
    }

    [Fact]
    public void ArchivingTwice_IsRefusedInsteadOfSilentlyMovingTheDate()
    {
        var task = NewChecklist();
        task.Archive(Now);

        var archiveAgain = () => task.Archive(Now.AddDays(1));

        archiveAgain.Should().Throw<DomainException>().WithMessage("*já está arquivado*");
        task.ArchivedAt.Should().Be(Now);
    }

    [Fact]
    public void RestoringFromTheArchive_BringsItBackToTheMainList()
    {
        var task = NewChecklist();
        task.Archive(Now);

        task.RestoreFromArchive();

        task.Lifecycle.Should().Be(TaskLifecycle.Active);
        task.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public void MovingToTrash_RecordsWhenAndWhoWithoutRemovingAnything()
    {
        var task = NewChecklist();

        task.MoveToTrash(Now, "adriano");

        task.Lifecycle.Should().Be(TaskLifecycle.Trashed);
        task.DeletedAt.Should().Be(Now);
        task.DeletedBy.Should().Be("adriano");
        task.Occurrences.Should().ContainSingle();
    }

    [Fact]
    public void MovingToTrash_WithoutAnIdentifiedUser_StillRecordsTheDeletion()
    {
        var task = NewChecklist();

        task.MoveToTrash(Now, "   ");

        task.DeletedAt.Should().Be(Now);
        task.DeletedBy.Should().BeNull();
    }

    /// <summary>
    /// A razão de <c>ArchivedAt</c> e <c>DeletedAt</c> serem campos
    /// independentes: sem isso seria preciso um "estado anterior" para saber
    /// para onde restaurar.
    /// </summary>
    [Fact]
    public void AnArchivedChecklistSentToTrash_GoesBackToTheArchiveWhenRestored()
    {
        var task = NewChecklist();
        task.Archive(Now);
        task.MoveToTrash(Now.AddDays(1), "adriano");

        task.Lifecycle.Should().Be(TaskLifecycle.Trashed);

        task.RestoreFromTrash();

        task.Lifecycle.Should().Be(TaskLifecycle.Archived);
        task.ArchivedAt.Should().Be(Now);
        task.DeletedBy.Should().BeNull();
    }

    [Fact]
    public void TheTrashWinsOverTheArchive_SoTheUserActsWhereTheDeadlineIsRunning()
    {
        var task = NewChecklist();
        task.Archive(Now);
        task.MoveToTrash(Now.AddDays(1), null);

        task.Lifecycle.Should().Be(TaskLifecycle.Trashed);
    }

    [Fact]
    public void ArchivingSomethingInTheTrash_IsRefused()
    {
        var task = NewChecklist();
        task.MoveToTrash(Now, null);

        var archive = () => task.Archive(Now);

        archive.Should().Throw<DomainException>().WithMessage("*lixeira*");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhatLeftTheMainList_IsReadOnly(bool inTrash)
    {
        var task = NewChecklist();
        var occurrenceId = task.Occurrences.Single().Id;

        if (inTrash)
        {
            task.MoveToTrash(Now, null);
        }
        else
        {
            task.Archive(Now);
        }

        var complete = () => task.CompleteOccurrence(occurrenceId, Now);
        var edit = () => task.Update("Outro título", null, TaskPriority.High);

        complete.Should().Throw<DomainException>();
        edit.Should().Throw<DomainException>();
        task.Title.Should().Be("Fechar o mês");
    }

    /// <summary>
    /// A regra do §10 que estrutura todas as outras: nenhum clique isolado leva
    /// um checklist ativo direto para o nada.
    /// </summary>
    [Fact]
    public void AnActiveChecklist_CannotBePermanentlyDeleted()
    {
        var task = NewChecklist();

        var purge = task.EnsurePermanentDeletionIsAllowed;

        purge.Should().Throw<DomainException>().WithMessage("*arquivado ou na lixeira*");
    }

    [Fact]
    public void AnArchivedOrTrashedChecklist_MayBePermanentlyDeleted()
    {
        var archived = NewChecklist();
        archived.Archive(Now);

        var trashed = NewChecklist();
        trashed.MoveToTrash(Now, null);

        archived.Invoking(task => task.EnsurePermanentDeletionIsAllowed()).Should().NotThrow();
        trashed.Invoking(task => task.EnsurePermanentDeletionIsAllowed()).Should().NotThrow();
    }

    [Fact]
    public void RestoringWhatIsNotArchived_IsRefusedInsteadOfDoingNothing()
    {
        var task = NewChecklist();

        var restore = task.RestoreFromArchive;

        restore.Should().Throw<DomainException>().WithMessage("*não está arquivado*");
    }

    [Fact]
    public void RestoringWhatIsNotInTheTrash_IsRefusedInsteadOfDoingNothing()
    {
        var task = NewChecklist();

        var restore = task.RestoreFromTrash;

        restore.Should().Throw<DomainException>().WithMessage("*não está na lixeira*");
    }
}
