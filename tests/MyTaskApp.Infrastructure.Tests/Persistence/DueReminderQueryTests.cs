using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

public class DueReminderQueryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 17, 15, 0, 0, TimeSpan.FromHours(-3));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReminderThatCameDue_IsReturned()
    {
        await using var db = await Seed(task => Arm(task, Now.AddMinutes(-1)));
        await using var context = db.CreateContext();

        var due = await new DueReminderQuery(context).GetDueAsync(Now, 10, Ct);

        due.Should().ContainSingle();
        due[0].Title.Should().Be("Verificar estoque");
        due[0].Channels.Should().Be(AlertChannels.All);
        due[0].Attempt.Should().Be(0);
    }

    [Fact]
    public async Task AReminderStillInTheFuture_IsLeftAlone()
    {
        await using var db = await Seed(task => Arm(task, Now.AddMinutes(1)));
        await using var context = db.CreateContext();

        (await new DueReminderQuery(context).GetDueAsync(Now, 10, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnarmedReminder_IsLeftAlone()
    {
        await using var db = await Seed(_ => { });
        await using var context = db.CreateContext();

        (await new DueReminderQuery(context).GetDueAsync(Now, 10, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAcknowledgedReminder_IsLeftAlone()
    {
        // Notificado não é atendido — mas atendido é atendido.
        await using var db = await Seed(task =>
        {
            var occurrence = Arm(task, Now.AddMinutes(-1));
            occurrence.AcknowledgeReminder(Now, ReminderAcknowledgement.MarkedSeen);
        });

        await using var context = db.CreateContext();

        (await new DueReminderQuery(context).GetDueAsync(Now, 10, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task AReminderThatAlreadyFired_KeepsComingBackUntilItIsAnswered()
    {
        await using var db = await Seed(task =>
        {
            var occurrence = Arm(task, Now.AddMinutes(-30));
            occurrence.MarkReminderFired(Now.AddMinutes(-30), Now.AddMinutes(-15));
        });

        await using var context = db.CreateContext();
        var due = await new DueReminderQuery(context).GetDueAsync(Now, 10, Ct);

        due.Should().ContainSingle();
        due[0].Attempt.Should().Be(1);
        due[0].WaitingSinceUtc.Should().Be(Now.AddMinutes(-30));
    }

    [Theory]
    [InlineData(TaskItemStatus.Completed)]
    [InlineData(TaskItemStatus.Cancelled)]
    public async Task AnOccurrenceThatIsNoLongerPending_IsLeftAlone(TaskItemStatus status)
    {
        await using var db = await Seed(task =>
        {
            var occurrence = Arm(task, Now.AddMinutes(-1));

            if (status is TaskItemStatus.Completed)
            {
                occurrence.Complete(Now);
            }
            else
            {
                occurrence.Cancel();
            }
        });

        await using var context = db.CreateContext();

        (await new DueReminderQuery(context).GetDueAsync(Now, 10, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheOldestWaitIsReturnedFirst_AndTheLimitIsRespected()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            foreach (var minutes in new[] { 5, 60, 30 })
            {
                var task = NewTask($"Tarefa {minutes}");
                Arm(task, Now.AddMinutes(-minutes));
                write.Tasks.Add(task);
            }

            await write.SaveChangesAsync(Ct);
        }

        await using var context = db.CreateContext();
        var due = await new DueReminderQuery(context).GetDueAsync(Now, 2, Ct);

        due.Should().HaveCount(2);
        due.Select(row => row.Title).Should().ContainInOrder("Tarefa 60", "Tarefa 30");
    }

    [Fact]
    public async Task ALimitOfNothing_AsksTheDatabaseForNothing()
    {
        await using var db = await Seed(task => Arm(task, Now.AddMinutes(-1)));
        await using var context = db.CreateContext();

        (await new DueReminderQuery(context).GetDueAsync(Now, 0, Ct)).Should().BeEmpty();
    }

    private static TaskOccurrence Arm(TaskItem task, DateTimeOffset fireAt)
    {
        var occurrence = task.Occurrences.Single();
        occurrence.ArmReminder(fireAt);
        return occurrence;
    }

    private static TaskItem NewTask(string title = "Verificar estoque") =>
        TaskItem.Create(
            title,
            Now.AddHours(-1),
            schedule: TaskSchedule.On(new DateOnly(2026, 9, 17)),
            reminder: ReminderPolicy.Default);

    private static async Task<TempSqliteDatabase> Seed(Action<TaskItem> arrange)
    {
        var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = NewTask();

        arrange(task);

        await using var write = db.CreateContext();
        write.Tasks.Add(task);
        await write.SaveChangesAsync(Ct);

        return db;
    }
}
