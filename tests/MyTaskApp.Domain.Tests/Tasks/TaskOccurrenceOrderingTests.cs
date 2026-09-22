using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

/// <summary>
/// A ordem manual da tela "Hoje" (ADR-022). O que estes testes guardam não é o
/// arrasto — é a regra que o torna honesto: a posição só existe enquanto a
/// ocorrência está pendente, e nada mais no agregado se mexe por causa dela.
/// </summary>
public class TaskOccurrenceOrderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private static TaskItem NewChecklist() => TaskItem.Create("Fechar o mês", Now);

    [Fact]
    public void ANewOccurrence_HasNoPositionOfItsOwn()
    {
        NewChecklist().Occurrences.Single().Position.Should().BeNull();
    }

    [Fact]
    public void PlaceOccurrence_RecordsTheChosenSlot()
    {
        var task = NewChecklist();

        task.PlaceOccurrence(task.Occurrences.Single().Id, 3);

        task.Occurrences.Single().Position.Should().Be(3);
    }

    [Fact]
    public void PlaceOccurrence_RejectsANegativeSlot()
    {
        var task = NewChecklist();

        var place = () => task.PlaceOccurrence(task.Occurrences.Single().Id, -1);

        place.Should().Throw<DomainException>();
    }

    [Fact]
    public void PlaceOccurrence_OnAnUnknownOccurrence_IsRejected()
    {
        var place = () => NewChecklist().PlaceOccurrence(Guid.CreateVersion7(), 0);

        place.Should().Throw<DomainException>();
    }

    /// <summary>
    /// CONCLUÍDAS ordena pela data de conclusão e ignora a posição. Aceitar a
    /// escrita aqui seria gravar algo que a leitura descarta em silêncio.
    /// </summary>
    [Fact]
    public void PlaceOccurrence_OnACompletedOccurrence_IsRejected()
    {
        var task = NewChecklist();
        var occurrenceId = task.Occurrences.Single().Id;
        task.CompleteOccurrence(occurrenceId, Now);

        var place = () => task.PlaceOccurrence(occurrenceId, 0);

        place.Should().Throw<DomainException>()
            .WithMessage("*pendente*");
    }

    [Fact]
    public void PlaceOccurrence_OnACancelledOccurrence_IsRejected()
    {
        var task = NewChecklist();
        var occurrenceId = task.Occurrences.Single().Id;
        task.CancelOccurrence(occurrenceId);

        var place = () => task.PlaceOccurrence(occurrenceId, 0);

        place.Should().Throw<DomainException>();
    }

    /// <summary>
    /// A contrapartida da recusa acima, e a razão de ela valer a pena: concluir
    /// não perde o lugar, então reabrir devolve a linha onde ela estava.
    /// </summary>
    [Fact]
    public void CompletingAndReopening_GivesTheRowItsPlaceBack()
    {
        var task = NewChecklist();
        var occurrenceId = task.Occurrences.Single().Id;

        task.PlaceOccurrence(occurrenceId, 2);
        task.CompleteOccurrence(occurrenceId, Now);
        task.ReopenOccurrence(occurrenceId);

        task.Occurrences.Single().Position.Should().Be(2);
    }

    /// <summary>
    /// Reagendar zera a posição pela mesma razão pela qual já desarma o
    /// lembrete: o lugar era numa fila de outro dia.
    /// </summary>
    [Fact]
    public void Rescheduling_ForgetsThePosition()
    {
        var task = NewChecklist();
        var occurrenceId = task.Occurrences.Single().Id;
        task.PlaceOccurrence(occurrenceId, 4);

        task.RescheduleOccurrence(
            occurrenceId,
            TaskSchedule.At(new DateOnly(2026, 9, 22), new TimeOnly(9, 0)));

        task.Occurrences.Single().Position.Should().BeNull();
    }

    [Fact]
    public void PlaceOccurrence_OnAnArchivedChecklist_IsRejected()
    {
        var task = NewChecklist();
        var occurrenceId = task.Occurrences.Single().Id;
        task.Archive(Now);

        var place = () => task.PlaceOccurrence(occurrenceId, 0);

        place.Should().Throw<DomainException>();
    }

    [Fact]
    public void PlaceOccurrence_OnAChecklistInTheTrash_IsRejected()
    {
        var task = NewChecklist();
        var occurrenceId = task.Occurrences.Single().Id;
        task.MoveToTrash(Now, "alguém");

        var place = () => task.PlaceOccurrence(occurrenceId, 0);

        place.Should().Throw<DomainException>();
    }

    /// <summary>
    /// Posição não é estado de execução. Chamar <c>RefreshConclusion</c> aqui
    /// "por simetria" com as irmãs faria um arrasto mexer na data de conclusão.
    /// </summary>
    [Fact]
    public void PlaceOccurrence_DoesNotTouchTheConclusionOfTheChecklist()
    {
        var task = NewChecklist();

        task.PlaceOccurrence(task.Occurrences.Single().Id, 1);

        task.ConcludedAt.Should().BeNull();
    }
}
