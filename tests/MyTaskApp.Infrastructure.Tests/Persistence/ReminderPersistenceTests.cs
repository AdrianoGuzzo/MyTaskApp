using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

public class ReminderPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 17, 30, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePolicy_RoundTripsExactly()
    {
        // Inclui uma combinação de [Flags]: coluna de texto quebraria o filtro
        // de bits silenciosamente.
        var policy = new ReminderPolicy(
            IsEnabled: true,
            ReminderAnchor.BeforeScheduledTime,
            TimeSpan.FromMinutes(37),
            RepeatUntilAcknowledged: true,
            TimeSpan.FromMinutes(7),
            AlertChannels.Notification | AlertChannels.BringToFront);

        var loaded = await RoundTripAsync(task => task.ChangeReminder(policy));

        loaded.Reminder.Should().Be(policy);
    }

    [Fact]
    public async Task APolicyThatIsOff_RoundTripsAsNone()
    {
        var loaded = await RoundTripAsync(task => task.ChangeReminder(ReminderPolicy.None));

        loaded.Reminder.Should().Be(ReminderPolicy.None);
    }

    [Fact]
    public async Task TheFactoryDefault_RoundTripsExactly()
    {
        var loaded = await RoundTripAsync(task => task.ChangeReminder(ReminderPolicy.Default));

        loaded.Reminder.Should().Be(ReminderPolicy.Default);
    }

    [Fact]
    public async Task TheStateInstants_RoundTripExactly()
    {
        // Mesma disciplina de ticks do ADR-011: é o tipo de coisa que quebra sem avisar.
        var firedAt = new DateTimeOffset(2026, 9, 17, 15, 0, 0, TimeSpan.FromHours(-3));
        var next = firedAt.AddMinutes(15);

        var loaded = await RoundTripAsync(task =>
        {
            task.ChangeReminder(ReminderPolicy.Default);
            var occurrence = task.Occurrences.Single();
            occurrence.ArmReminder(firedAt);
            occurrence.MarkReminderFired(firedAt, next);
        });

        var state = loaded.Occurrences.Single().Reminder;

        state.NextFireAtUtc.Should().Be(next);
        state.LastFiredAtUtc.Should().Be(firedAt);
        state.WaitingSinceUtc.Should().Be(firedAt);
        state.Attempt.Should().Be(1);
        state.IsAcknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task AnAcknowledgement_RoundTripsWithItsReason()
    {
        var loaded = await RoundTripAsync(task =>
        {
            task.ChangeReminder(ReminderPolicy.Default);
            var occurrence = task.Occurrences.Single();
            occurrence.ArmReminder(Now);
            occurrence.AcknowledgeReminder(Now.AddMinutes(3), ReminderAcknowledgement.MarkedSeen);
        });

        var state = loaded.Occurrences.Single().Reminder;

        state.AcknowledgedAtUtc.Should().Be(Now.AddMinutes(3));
        state.AcknowledgedBy.Should().Be(ReminderAcknowledgement.MarkedSeen);
        state.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public async Task AnOccurrenceThatWasNeverArmed_LoadsWithAnEmptyState()
    {
        // O EF precisa distinguir "owned ausente" de "colunas nulas"; sem as
        // colunas obrigatórias isto estoura na validação do modelo.
        var loaded = await RoundTripAsync(_ => { });

        var state = loaded.Occurrences.Single().Reminder;

        state.Should().NotBeNull();
        state.IsArmed.Should().BeFalse();
        state.Attempt.Should().Be(0);
    }

    [Fact]
    public async Task OnePolicyAppliedToSeveralTasks_IsStoredForEveryOneOfThem()
    {
        // Regressao: instancias de tipo owned nao podem ser compartilhadas entre
        // donos. Antes da copia em ChangeReminder, a primeira tarefa do lote
        // voltava do banco com o lembrete desligado.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        var batch = new[] { "comprar pao", "ligar dentista", "verificar estoque" }
            .Select(title => TaskItem.Create(
                title, Now, schedule: TaskSchedule.On(new DateOnly(2026, 9, 17)),
                reminder: ReminderPolicy.Default))
            .ToList();

        await using (var write = db.CreateContext())
        {
            write.Tasks.AddRange(batch);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var loaded = await read.Tasks.ToListAsync(Ct);

        loaded.Should().HaveCount(3);
        loaded.Should().AllSatisfy(task => task.Reminder.Should().Be(ReminderPolicy.Default));
    }

    private static async Task<TaskItem> RoundTripAsync(Action<TaskItem> arrange)
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        var task = TaskItem.Create(
            "Verificar estoque",
            Now,
            schedule: TaskSchedule.At(new DateOnly(2026, 9, 17), new TimeOnly(15, 30)));

        arrange(task);

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        return await read.Tasks.Include(item => item.Occurrences).SingleAsync(Ct);
    }
}
