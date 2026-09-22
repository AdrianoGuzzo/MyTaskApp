using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Planning;

/// <summary>
/// A ordem que o usuário deu com a mão (ADR-022). Os critérios de sempre —
/// horário, prioridade, título, conclusão — continuam valendo; eles só deixaram
/// de ser a primeira palavra nas seções pendentes.
/// </summary>
public class TodayBoardManualOrderTests
{
    private static readonly DateOnly Today = new(2026, 9, 17);

    /// <summary>17/09/2026 14:00 em São Paulo.</summary>
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 17, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeQuery _query = new();

    private GetTodayBoardHandler Handler()
    {
        var options = Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" });
        var timeProvider = new FakeTimeProvider(NowUtc);
        var clock = new UserClock(timeProvider, options, NullLogger<UserClock>.Instance);

        return new GetTodayBoardHandler(_query, clock, timeProvider, options);
    }

    private static TodayOccurrenceRow Row(
        string title,
        DateOnly? date,
        TimeOnly? time = null,
        int? position = null,
        TaskItemStatus status = TaskItemStatus.Pending,
        DateTimeOffset? completedAt = null,
        TaskPriority priority = TaskPriority.Normal) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            title,
            priority,
            date,
            time,
            status,
            completedAt,
            Position: position);

    [Fact]
    public async Task InTodaysSection_ThePlacedOrderBeatsTheClock()
    {
        _query.Rows =
        [
            Row("Dentista", Today, new TimeOnly(9, 0), position: 1),
            Row("Reunião", Today, new TimeOnly(16, 0), position: 0),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Today.Select(task => task.Title).Should().Equal("Reunião", "Dentista");
    }

    [Fact]
    public async Task InTheOverdueSection_ThePlacedOrderBeatsTheDate()
    {
        _query.Rows =
        [
            Row("Anteontem", Today.AddDays(-2), new TimeOnly(9, 0), position: 1),
            Row("Ontem", Today.AddDays(-1), new TimeOnly(9, 0), position: 0),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Overdue.Select(task => task.Title).Should().Equal("Ontem", "Anteontem");
    }

    /// <summary>
    /// A "Limitação conhecida" do ADR-013 — o checklist recém-digitado saindo em
    /// ordem alfabética — deixa de valer para quem arrastar.
    /// </summary>
    [Fact]
    public async Task InTheUnscheduledSection_ThePlacedOrderBeatsPriorityAndTitle()
    {
        _query.Rows =
        [
            Row("Abrir chamado", Today, priority: TaskPriority.Urgent, position: 2),
            Row("Comprar pão", Today, position: 0),
            Row("Barbeiro", Today, position: 1),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Unscheduled.Select(task => task.Title)
            .Should().Equal("Comprar pão", "Barbeiro", "Abrir chamado");
    }

    /// <summary>
    /// A promessa que a migration sem backfill faz: quem nunca arrastou nada vê
    /// exatamente a lista de antes.
    /// </summary>
    [Fact]
    public async Task WithNothingPlaced_EverySectionKeepsItsOldOrder()
    {
        _query.Rows =
        [
            Row("Deploy", Today, new TimeOnly(16, 0)),
            Row("Dentista", Today, new TimeOnly(9, 0)),
            Row("Zelar", Today, priority: TaskPriority.Normal),
            Row("Assinar", Today, priority: TaskPriority.Urgent),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Today.Select(task => task.Title).Should().Equal("Dentista", "Deploy");
        board.Unscheduled.Select(task => task.Title).Should().Equal("Assinar", "Zelar");
    }

    /// <summary>
    /// Quem ainda não foi arrastado vai para o fim da seção, e entre si continua
    /// na ordem de sempre.
    /// </summary>
    [Fact]
    public async Task WhatWasNeverPlaced_SinksToTheBottomInItsUsualOrder()
    {
        _query.Rows =
        [
            Row("Nunca movida B", Today, new TimeOnly(8, 0)),
            Row("Nunca movida A", Today, new TimeOnly(7, 0)),
            Row("Arrastada", Today, new TimeOnly(23, 0), position: 0),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Today.Select(task => task.Title)
            .Should().Equal("Arrastada", "Nunca movida A", "Nunca movida B");
    }

    /// <summary>
    /// A regressão tentadora: ordenar CONCLUÍDAS por posição "por simetria" com
    /// as outras faria a lista mentir sobre a ordem em que as coisas foram
    /// feitas. Uma posição só chega aqui se a ocorrência foi arrastada antes de
    /// ser concluída — o domínio recusa arrastar o que já está concluído.
    /// </summary>
    [Fact]
    public async Task TheCompletedSection_IgnoresThePlacedOrder()
    {
        _query.Rows =
        [
            Row(
                "Concluída cedo",
                Today,
                new TimeOnly(9, 0),
                position: 0,
                status: TaskItemStatus.Completed,
                completedAt: NowUtc.AddHours(-3)),
            Row(
                "Concluída agora",
                Today,
                new TimeOnly(10, 0),
                position: 9,
                status: TaskItemStatus.Completed,
                completedAt: NowUtc),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Completed.Select(task => task.Title)
            .Should().Equal("Concluída agora", "Concluída cedo");
    }

    /// <summary>
    /// O relógio move linhas entre ATRASADAS, AGORA e HOJE levando a posição
    /// junto, então duas podem colidir na mesma casa. O desempate de sempre
    /// resolve — o que não pode é o resultado variar entre duas cargas.
    /// </summary>
    [Fact]
    public async Task TwoRowsInTheSameSlot_FallBackToTheUsualTiebreak()
    {
        _query.Rows =
        [
            Row("Mais tarde", Today, new TimeOnly(18, 0), position: 0),
            Row("Mais cedo", Today, new TimeOnly(8, 0), position: 0),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Today.Select(task => task.Title).Should().Equal("Mais cedo", "Mais tarde");
    }

    private sealed class FakeQuery : ITodayQuery
    {
        public IReadOnlyList<TodayOccurrenceRow> Rows { get; set; } = [];

        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
            DateOnly today,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows);
    }
}
