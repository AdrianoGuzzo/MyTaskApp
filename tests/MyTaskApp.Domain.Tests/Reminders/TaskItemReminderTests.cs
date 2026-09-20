using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Reminders;

public class TaskItemReminderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 17, 14, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void ATaskCreatedWithoutAPolicy_HasNoReminder()
    {
        // O domínio não conhece o padrão do usuário: quem o aplica é a Application.
        TaskItem.Create("Verificar estoque", Now).Reminder.Should().Be(ReminderPolicy.None);
    }

    [Fact]
    public void ATaskCreatedWithAPolicy_KeepsIt()
    {
        var task = TaskItem.Create("Verificar estoque", Now, reminder: ReminderPolicy.Urgent);

        task.Reminder.Should().Be(ReminderPolicy.Urgent);
    }

    [Fact]
    public void ChangingTheReminder_ReplacesThePolicy()
    {
        var task = TaskItem.Create("Verificar estoque", Now, reminder: ReminderPolicy.Default);

        task.ChangeReminder(ReminderPolicy.None);

        task.Reminder.Should().Be(ReminderPolicy.None);
    }

    [Fact]
    public void ChangingTheReminderToNothing_IsRefused()
    {
        var task = TaskItem.Create("Verificar estoque", Now);

        var change = () => task.ChangeReminder(null!);

        change.Should().Throw<DomainException>().WithMessage("*política de lembrete*");
    }

    [Fact]
    public void TwoTasksSharingAPolicy_EachGetTheirOwnInstance()
    {
        // O EF nao aceita a mesma instancia de tipo owned em dois donos: grava
        // uma e deixa a outra com os valores default, em silencio. A captura
        // rapida aplica a mesma politica a varias tarefas, entao sem a copia a
        // primeira de cada lote nascia com o lembrete desligado.
        var first = TaskItem.Create("comprar pao", Now, reminder: ReminderPolicy.Default);
        var second = TaskItem.Create("ligar dentista", Now, reminder: ReminderPolicy.Default);

        first.Reminder.Should().NotBeSameAs(second.Reminder);
        first.Reminder.Should().NotBeSameAs(ReminderPolicy.Default);
        first.Reminder.Should().Be(ReminderPolicy.Default);
        second.Reminder.Should().Be(ReminderPolicy.Default);
    }

    [Fact]
    public void EditingTheTask_DoesNotTouchTheReminder()
    {
        var task = TaskItem.Create("Verificar estoque", Now, reminder: ReminderPolicy.Urgent);

        task.Update("Conferir estoque", "na segunda", TaskPriority.High);

        task.Reminder.Should().Be(ReminderPolicy.Urgent);
    }
}
