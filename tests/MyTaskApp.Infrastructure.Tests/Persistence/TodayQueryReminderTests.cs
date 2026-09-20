using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// A tela "Hoje" precisa do estado do lembrete para acender o ⚠, e da política
/// para o flyout de ajuste abrir já preenchido — tudo em colunas da mesma
/// tabela, sem join novo.
/// </summary>
public class TodayQueryReminderTests
{
    private static readonly DateOnly Today = new(2026, 9, 17);
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 17, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CandidatesCarryWaitingSinceSoTheBoardCanShowTheWarning()
    {
        var firedAt = NowUtc.AddMinutes(-35);

        var row = await QueryAsync(occurrence =>
        {
            occurrence.ArmReminder(firedAt);
            occurrence.MarkReminderFired(firedAt, NowUtc.AddMinutes(-20));
        });

        row.ReminderWaitingSinceUtc.Should().Be(firedAt);
        row.ReminderAttempt.Should().Be(1);
    }

    [Fact]
    public async Task AnAnsweredReminder_StopsReportingAWait()
    {
        // Atendido é atendido: o ⚠ tem de apagar na próxima carga do quadro.
        var row = await QueryAsync(occurrence =>
        {
            occurrence.ArmReminder(NowUtc.AddMinutes(-35));
            occurrence.MarkReminderFired(NowUtc.AddMinutes(-35), NowUtc.AddMinutes(-20));
            occurrence.AcknowledgeReminder(NowUtc, ReminderAcknowledgement.MarkedSeen);
        });

        row.ReminderWaitingSinceUtc.Should().BeNull();
    }

    [Fact]
    public async Task AChecklistThatWasNeverRemindedOf_ReportsNoWait()
    {
        var row = await QueryAsync(_ => { });

        row.ReminderWaitingSinceUtc.Should().BeNull();
        row.ReminderAttempt.Should().Be(0);
    }

    [Fact]
    public async Task ThePolicyComesAlongForTheRowToOfferEditingIt()
    {
        var row = await QueryAsync(_ => { }, ReminderPolicy.Urgent);

        row.TaskReminder.Should().Be(ReminderPolicy.Urgent);
    }

    private static async Task<Application.Planning.TodayOccurrenceRow> QueryAsync(
        Action<TaskOccurrence> arrange,
        ReminderPolicy? policy = null)
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        var task = TaskItem.Create(
            "Verificar estoque",
            NowUtc,
            schedule: TaskSchedule.At(Today, new TimeOnly(9, 0)),
            reminder: policy ?? ReminderPolicy.Default);

        arrange(task.Occurrences.Single());

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        return (await new TodayQuery(read).GetCandidatesAsync(Today, Ct)).Single();
    }
}
