using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Planning;

/// <summary>
/// Gravar a nova ordem de uma seção (ADR-022). Cada linha da lista é um
/// checklist próprio, então uma seção reordenada é uma escrita em dezenas de
/// agregados — e ou entra inteira, ou não entra nada dela.
/// </summary>
public class ReorderOccurrencesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();

    private ReorderOccurrencesHandler Handler() =>
        new(_repository, _repository, NullLogger<ReorderOccurrencesHandler>.Instance);

    /// <summary>Cria N checklists de uma ocorrência cada — a forma da lista real.</summary>
    private List<TaskItem> Seed(int count)
    {
        var tasks = Enumerable
            .Range(0, count)
            .Select(index => TaskItem.Create($"Tarefa {index}", Now.AddSeconds(index)))
            .ToList();

        _repository.Seed([.. tasks]);

        return tasks;
    }

    private static Guid OccurrenceOf(TaskItem task) => task.Occurrences.Single().Id;

    private static int? PositionOf(TaskItem task) => task.Occurrences.Single().Position;

    [Fact]
    public async Task TheNewOrder_BecomesSlotsZeroToLast()
    {
        var tasks = Seed(3);
        var reversed = tasks.AsEnumerable().Reverse().Select(OccurrenceOf).ToList();

        await Handler().HandleAsync(new ReorderOccurrences(reversed), Ct);

        PositionOf(tasks[2]).Should().Be(0);
        PositionOf(tasks[1]).Should().Be(1);
        PositionOf(tasks[0]).Should().Be(2);
    }

    /// <summary>
    /// Uma seção inteira é uma transação só: arrastar não pode deixar metade da
    /// lista numerada se algo falhar no meio.
    /// </summary>
    [Fact]
    public async Task TheWholeSection_IsSavedOnce()
    {
        var tasks = Seed(5);

        await Handler().HandleAsync(
            new ReorderOccurrences([.. tasks.Select(OccurrenceOf)]), Ct);

        _repository.SaveCount.Should().Be(1);
    }

    /// <summary>
    /// A tela grava depois de já ter movido, e pode gravar a mesma ordem duas
    /// vezes. Reenviar não pode produzir deriva nenhuma.
    /// </summary>
    [Fact]
    public async Task SendingTheSameOrderTwice_ChangesNothing()
    {
        var tasks = Seed(3);
        var order = tasks.Select(OccurrenceOf).ToList();

        await Handler().HandleAsync(new ReorderOccurrences(order), Ct);
        await Handler().HandleAsync(new ReorderOccurrences(order), Ct);

        tasks.Select(PositionOf).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task ARepeatedOccurrence_IsRejected()
    {
        var tasks = Seed(2);
        var id = OccurrenceOf(tasks[0]);

        var reorder = async () => await Handler()
            .HandleAsync(new ReorderOccurrences([id, id]), Ct);

        await reorder.Should().ThrowAsync<DomainException>();
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task AnEmptyOrder_IsRejectedInsteadOfSavingNothingQuietly()
    {
        var reorder = async () => await Handler()
            .HandleAsync(new ReorderOccurrences([]), Ct);

        await reorder.Should().ThrowAsync<DomainException>();
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task MoreItemsThanTheCeiling_AreRefusedBeforeTouchingTheDatabase()
    {
        var tooMany = Enumerable
            .Range(0, ReorderOccurrences.MaxItems + 1)
            .Select(_ => Guid.CreateVersion7())
            .ToList();

        var reorder = async () => await Handler()
            .HandleAsync(new ReorderOccurrences(tooMany), Ct);

        await reorder.Should().ThrowAsync<DomainException>();
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownOccurrence_IsRejected()
    {
        var tasks = Seed(1);

        var reorder = async () => await Handler().HandleAsync(
            new ReorderOccurrences([OccurrenceOf(tasks[0]), Guid.CreateVersion7()]), Ct);

        await reorder.Should().ThrowAsync<DomainException>();
        _repository.SaveCount.Should().Be(0);
    }

    /// <summary>
    /// O que o ADR-008 promete: uma regra que recusa no meio do caminho não
    /// deixa nada persistido — nem sequer a parte que já havia passado.
    /// </summary>
    [Fact]
    public async Task AnArchivedChecklistInTheMiddle_AbortsTheWholeSection()
    {
        var tasks = Seed(3);
        tasks[1].Archive(Now);

        var reorder = async () => await Handler().HandleAsync(
            new ReorderOccurrences([.. tasks.Select(OccurrenceOf)]), Ct);

        await reorder.Should().ThrowAsync<DomainException>();

        _repository.SaveCount.Should().Be(0);
        tasks.Select(PositionOf).Should().AllSatisfy(position => position.Should().BeNull());
    }

    [Fact]
    public async Task ACompletedOccurrence_CannotBeDraggedIntoAnOrder()
    {
        var tasks = Seed(2);
        tasks[0].CompleteOccurrence(OccurrenceOf(tasks[0]), Now);

        var reorder = async () => await Handler().HandleAsync(
            new ReorderOccurrences([.. tasks.Select(OccurrenceOf)]), Ct);

        await reorder.Should().ThrowAsync<DomainException>();
        _repository.SaveCount.Should().Be(0);
    }
}
