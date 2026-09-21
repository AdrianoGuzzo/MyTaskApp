using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Lifecycle;

/// <summary>
/// A data de conclusão do checklist (§2). É ela que o arquivamento automático
/// usa como base — e não a data de criação —, então ela precisa ser verdade a
/// cada transição, não só na primeira.
/// </summary>
public class ChecklistConclusionTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Finished = new(2026, 9, 21, 16, 40, 0, TimeSpan.Zero);

    private static TaskItem NewChecklist() => TaskItem.Create("Conferir notas", Created);

    [Fact]
    public void APendingChecklist_HasNoConclusionDate()
    {
        NewChecklist().ConcludedAt.Should().BeNull();
    }

    /// <summary>
    /// O ponto do §2: o prazo conta da conclusão. Um checklist criado em janeiro
    /// e concluído em setembro começa a contar em setembro.
    /// </summary>
    [Fact]
    public void ConcludingStampsTheCompletionDate_NotTheCreationDate()
    {
        var task = NewChecklist();

        task.CompleteOccurrence(task.Occurrences.Single().Id, Finished);

        task.ConcludedAt.Should().Be(Finished);
        task.CreatedAt.Should().Be(Created);
    }

    [Fact]
    public void ReopeningClearsTheConclusion_SoTheArchivingClockStartsOver()
    {
        var task = NewChecklist();
        var occurrenceId = task.Occurrences.Single().Id;

        task.CompleteOccurrence(occurrenceId, Finished);
        task.ReopenOccurrence(occurrenceId);

        task.ConcludedAt.Should().BeNull();
        task.Lifecycle.Should().Be(TaskLifecycle.Active);
    }

    /// <summary>
    /// Cancelada não é concluída. Um checklist do qual se desistiu não tem data
    /// de conclusão, e por isso nunca é arquivado sozinho — arquivar o que foi
    /// abandonado continua sendo escolha de quem abandonou.
    /// </summary>
    [Fact]
    public void ACancelledChecklist_HasNoConclusionDateAndIsNeverAutoArchived()
    {
        var task = NewChecklist();

        task.CancelOccurrence(task.Occurrences.Single().Id);

        task.ConcludedAt.Should().BeNull();
        task.Lifecycle.Should().Be(TaskLifecycle.Active);
    }

    [Fact]
    public void ReschedulingAPendingChecklist_DoesNotConcludeIt()
    {
        var task = NewChecklist();

        task.RescheduleOccurrence(
            task.Occurrences.Single().Id,
            TaskSchedule.On(new DateOnly(2026, 9, 30)));

        task.ConcludedAt.Should().BeNull();
    }
}
