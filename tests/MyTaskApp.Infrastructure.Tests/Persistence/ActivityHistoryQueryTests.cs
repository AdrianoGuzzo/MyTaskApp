using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O histórico dos últimos dias contra SQLite de verdade (ADR-053): só o intervalo
/// pedido, a lixeira fora, o arquivado dentro, e duas idas ao banco qualquer que
/// seja o tamanho dele.
/// </summary>
public class ActivityHistoryQueryTests
{
    private static readonly TimeSpan SaoPaulo = TimeSpan.FromHours(-3);

    /// <summary>A meia-noite de quarta, 30/09, em São Paulo: o começo da janela.</summary>
    private static readonly DateTimeOffset Since = new(2026, 9, 30, 0, 0, 0, SaoPaulo);

    /// <summary>A meia-noite de quarta, 07/10: o fim da janela, de fora.</summary>
    private static readonly DateTimeOffset Until = new(2026, 10, 7, 0, 0, 0, SaoPaulo);

    /// <summary>Bem depois de tudo: os lançamentos manuais não podem terminar no futuro.</summary>
    private static readonly DateTimeOffset Later = Until.AddDays(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, SaoPaulo);

    private static async Task<TaskItem> SeedAsync(
        TempSqliteDatabase db,
        string title,
        DateTimeOffset? completedAt = null,
        Action<TaskItem>? then = null)
    {
        var task = TaskItem.Create(title, Local(9, 1, 9));

        if (completedAt is { } at)
        {
            task.CompleteOccurrence(task.Occurrences.Single().Id, at);
        }

        then?.Invoke(task);

        await using var context = db.CreateContext();
        context.Tasks.Add(task);
        await context.SaveChangesAsync(Ct);

        return task;
    }

    private static async Task AddAsync(TempSqliteDatabase db, params TimeEntry[] entries)
    {
        await using var context = db.CreateContext();
        context.TimeEntries.AddRange(entries);
        await context.SaveChangesAsync(Ct);
    }

    private static TimeEntry Period(TaskItem task, DateTimeOffset from, DateTimeOffset until) =>
        TimeEntry.Manual(task.Occurrences.Single().Id, from, until, note: null, Later);

    private static async Task<Application.History.ActivityHistoryRows> QueryAsync(TempSqliteDatabase db)
    {
        await using var context = db.CreateContext();
        return await new ActivityHistoryQuery(context).GetAsync(Since, Until, Ct);
    }

    [Fact]
    public async Task Completions_AreTheOnesInsideTheWindow_WithTheEdgesRight()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await SeedAsync(db, "Na abertura", Since);
        var inside = await SeedAsync(db, "Dentro", Local(10, 5, 15, 42));
        await SeedAsync(db, "Um minuto antes", Since.AddMinutes(-1));
        await SeedAsync(db, "No fechamento", Until);
        await SeedAsync(db, "Pendente");

        var rows = await QueryAsync(db);

        rows.Completions.Select(row => row.Title).Should().BeEquivalentTo("Na abertura", "Dentro");
        var completion = rows.Completions.Single(row => row.Title == "Dentro");
        completion.TaskId.Should().Be(inside.Id);
        completion.OccurrenceId.Should().Be(inside.Occurrences.Single().Id);
        completion.CompletedAt.Should().Be(Local(10, 5, 15, 42));
    }

    [Fact]
    public async Task TheTrash_IsLeftOut_AndTheArchive_IsKept()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var archived = await SeedAsync(db, "Arquivada", Local(10, 2, 10), task => task.Archive(Local(10, 3, 9)));
        var trashed = await SeedAsync(db, "Na lixeira", Local(10, 2, 11), task => task.MoveToTrash(Local(10, 3, 9), "eu"));
        await AddAsync(
            db,
            Period(archived, Local(10, 2, 9), Local(10, 2, 10)),
            Period(trashed, Local(10, 2, 10), Local(10, 2, 11)));

        var rows = await QueryAsync(db);

        rows.Completions.Should().ContainSingle().Which.Title.Should().Be("Arquivada");
        rows.Periods.Should().ContainSingle().Which.Title.Should().Be("Arquivada");
    }

    [Fact]
    public async Task Periods_AreTheOnesThatTouchTheWindow_IncludingTheRunningOne()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedAsync(db, "Integração TGC");
        var occurrence = task.Occurrences.Single().Id;

        await AddAsync(
            db,
            Period(task, Local(9, 29, 22), Local(9, 30, 1)), // começou antes, terminou dentro
            Period(task, Local(10, 4, 9), Local(10, 4, 10, 20)), // dentro
            Period(task, Local(9, 29, 20), Since), // termina exatamente na abertura
            Period(task, Local(9, 28, 9), Local(9, 28, 10)), // antes de tudo
            Period(task, Until, Until.AddHours(1)), // começa exatamente no fechamento
            TimeEntry.StartTimer(occurrence, Local(10, 6, 14))); // correndo

        var rows = await QueryAsync(db);

        rows.Periods.Select(period => period.StartedAt).Should().BeEquivalentTo(
            [Local(9, 29, 22), Local(10, 4, 9), Local(10, 6, 14)]);
        rows.Periods.Should().OnlyContain(period => period.TaskId == task.Id && period.OccurrenceId == occurrence);
        rows.Periods.Single(period => period.StartedAt == Local(10, 6, 14)).EndedAt.Should().BeNull();
        rows.Periods.Single(period => period.StartedAt == Local(10, 4, 9)).EndedAt.Should().Be(Local(10, 4, 10, 20));
    }

    [Fact]
    public async Task TheWholeHistory_IsTwoCommands_HoweverManyTasksThereAre()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        for (var index = 0; index < 50; index++)
        {
            var task = await SeedAsync(db, $"Tarefa {index}", Local(10, 5, 9).AddMinutes(index * 5));
            await AddAsync(db, Period(task, Local(10, 4, 9).AddMinutes(index * 5), Local(10, 4, 9, 4).AddMinutes(index * 5)));
        }

        var counter = new CommandCounter();
        await using var context = new MyTaskAppDbContext(new DbContextOptionsBuilder<MyTaskAppDbContext>()
            .UseSqlite(db.ConnectionString)
            .AddInterceptors(counter)
            .Options);

        var rows = await new ActivityHistoryQuery(context).GetAsync(Since, Until, Ct);

        rows.Completions.Should().HaveCount(50);
        rows.Periods.Should().HaveCount(50);
        counter.Commands.Should().Be(2);
    }

    [Fact]
    public async Task AYearOfData_StaysFast_BecauseOnlyTheWindowIsRead()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        // 3 mil tarefas, uma por dia de trás para a frente, cada uma com um
        // período: só a última semana interessa ao histórico.
        await using (var context = db.CreateContext())
        {
            for (var index = 0; index < 3000; index++)
            {
                var day = Local(10, 6, 9).AddHours(-index * 3);
                var task = TaskItem.Create($"Tarefa {index}", day.AddDays(-1));
                task.CompleteOccurrence(task.Occurrences.Single().Id, day.AddHours(1));
                context.Tasks.Add(task);
                context.TimeEntries.Add(TimeEntry.Manual(task.Occurrences.Single().Id, day, day.AddMinutes(30), null, Later));
            }

            await context.SaveChangesAsync(Ct);
        }

        var watch = Stopwatch.StartNew();
        var rows = await QueryAsync(db);
        watch.Stop();

        // Uma tarefa a cada 3 horas: cerca de 50 numa semana, e não 3 mil.
        rows.Completions.Should().HaveCountLessThan(60);
        rows.Periods.Should().HaveCountLessThan(60);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Commands { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
