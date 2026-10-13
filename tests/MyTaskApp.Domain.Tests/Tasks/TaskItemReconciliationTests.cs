using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

/// <summary>
/// O recálculo depois de um estado que chegou de outro computador (ADR-060).
/// O estado incoerente de verdade só nasce no banco, e é testado na
/// Infrastructure; aqui ficam as regras da raiz.
/// </summary>
public class TaskItemReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnACoherentTask_NothingChanges()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        var occurrence = task.Occurrences.Single();
        task.CompleteOccurrence(occurrence.Id, Now.AddHours(1));

        task.ReconcileDerivedState();

        task.ConcludedAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void APendingOccurrenceInTheMainList_KeepsItsReminder()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        var occurrence = task.Occurrences.Single();
        occurrence.ArmReminder(Now.AddHours(2));

        task.ReconcileDerivedState();

        occurrence.Reminder.NextFireAtUtc.Should().Be(Now.AddHours(2));
        task.ConcludedAt.Should().BeNull();
    }

    [Fact]
    public void OutOfTheMainList_NoReminderStaysArmed()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        var occurrence = task.Occurrences.Single();
        task.Archive(Now);
        occurrence.ArmReminder(Now.AddHours(2));

        task.ReconcileDerivedState();

        occurrence.Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public void NotCreatedHere_KeepsItsStoredValue()
    {
        TaskDevelopmentStatus.NotCreatedHere.Should().HaveValue(5, "o valor é gravado no banco e não pode mudar");
    }
}
