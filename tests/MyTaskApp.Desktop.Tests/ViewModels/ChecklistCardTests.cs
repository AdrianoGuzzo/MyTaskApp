using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O cartão de um checklist arquivado ou excluído (§3, §5) e o seletor de prazo
/// (§11). Rótulos são comportamento aqui: é por eles que o usuário sabe quanto
/// tempo ainda tem.
/// </summary>
public class ChecklistCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ChecklistSummaryRow Row(
        DateTimeOffset? archivedAt = null,
        DateTimeOffset? deletedAt = null,
        string? deletedBy = null,
        DateTimeOffset? concludedAt = null,
        int total = 3,
        int completed = 3) =>
        new(
            Guid.CreateVersion7(),
            "Fechar o mês",
            "Conferir os lançamentos",
            TaskPriority.Normal,
            Now.AddDays(-60),
            concludedAt,
            archivedAt,
            deletedAt,
            deletedBy,
            total,
            completed);

    private static ChecklistCardViewModel Card(ChecklistSummaryRow row, int trashDays = 30) =>
        new(row, new DataRetentionPolicy(false, 30, trashDays), Now);

    [Fact]
    public void AnArchivedChecklist_SaysSoAndHasNoDeadlineRunning()
    {
        var card = Card(Row(archivedAt: Now.AddDays(-5), concludedAt: Now.AddDays(-40)));

        card.StateLabel.Should().Be("ARQUIVADO");
        card.IsInTrash.Should().BeFalse();
        card.HasDaysLeft.Should().BeFalse();
        card.WhenLabel.Should().StartWith("Arquivado em");
    }

    [Fact]
    public void ADeletedChecklist_ShowsWhenWhoAndHowLongIsLeft()
    {
        var card = Card(Row(deletedAt: Now.AddDays(-10), deletedBy: "adriano"));

        card.StateLabel.Should().Be("NA LIXEIRA");
        card.WhenLabel.Should().StartWith("Excluído em");
        card.DeletedByLabel.Should().Be("por adriano");
        card.DaysLeftLabel.Should().Be("Restam 20 dias até a exclusão definitiva");
        card.IsExpiringSoon.Should().BeFalse();
    }

    [Fact]
    public void ADeletionWithoutAnIdentifiedUser_SimplyOmitsTheAuthor()
    {
        var card = Card(Row(deletedAt: Now.AddDays(-1)));

        card.HasDeletedBy.Should().BeFalse();
        card.DeletedByLabel.Should().BeNull();
    }

    [Fact]
    public void TheLastDays_AreHighlightedSoTheDeadlineIsNotASurprise()
    {
        var card = Card(Row(deletedAt: Now.AddDays(-28)));

        card.DaysLeftLabel.Should().Be("Restam 2 dias até a exclusão definitiva");
        card.IsExpiringSoon.Should().BeTrue();
    }

    [Fact]
    public void SomethingAlreadyPastItsDeadline_SaysTheNextSweepWillTakeIt()
    {
        var card = Card(Row(deletedAt: Now.AddDays(-45)));

        card.DaysLeftLabel.Should().Be("Será excluído definitivamente na próxima verificação");
    }

    [Fact]
    public void SomethingArchivedBeforeBeingDeleted_WarnsWhereRestoringWillPutIt()
    {
        var card = Card(Row(archivedAt: Now.AddDays(-20), deletedAt: Now.AddDays(-1)));

        card.WasArchivedBeforeTrash.Should().BeTrue();
    }

    [Fact]
    public void TheItemCount_ReadsNaturallyForASingleItem()
    {
        Card(Row(total: 1, completed: 1)).ItemsLabel.Should().Be("1 de 1 item concluído");
        Card(Row(total: 7, completed: 3)).ItemsLabel.Should().Be("3 de 7 itens concluídos");
    }

    [Fact]
    public void AChecklistThatWasNeverConcluded_SaysSoInsteadOfShowingADash()
    {
        Card(Row(archivedAt: Now)).ConcludedLabel.Should().Be("Não concluído");
    }

    [Fact]
    public void AnAutomaticOperation_ReadsAsDoneByTheSystem()
    {
        var line = new ChecklistAuditLineViewModel(TaskAuditEntry.BySystem(
            Guid.CreateVersion7(),
            "Fechar o mês",
            TaskAuditOperation.PermanentlyDeleted,
            Now,
            "Prazo vencido."));

        line.Operation.Should().Be("Excluído definitivamente");
        line.By.Should().Be("pelo sistema");
        line.IsSystem.Should().BeTrue();
        line.IsDestructive.Should().BeTrue();
        line.HasDetails.Should().BeTrue();
    }

    [Fact]
    public void AUserOperation_NamesTheUserWhenThereIsOne()
    {
        var withName = new ChecklistAuditLineViewModel(TaskAuditEntry.ByUser(
            Guid.CreateVersion7(), "Fechar o mês", TaskAuditOperation.Archived, Now, "adriano"));

        var without = new ChecklistAuditLineViewModel(TaskAuditEntry.ByUser(
            Guid.CreateVersion7(), "Fechar o mês", TaskAuditOperation.Archived, Now, null));

        withName.By.Should().Be("por adriano");
        without.By.Should().Be("pelo usuário");
    }

    // ------------------------------------------------------------------
    // Seletor de prazo
    // ------------------------------------------------------------------

    [Fact]
    public void APresetDeadline_IsSelectedInTheList()
    {
        var choice = new RetentionChoiceViewModel();

        choice.Load(30);

        choice.IsCustom.Should().BeFalse();
        choice.Days.Should().Be(30);
        choice.Options[choice.SelectedIndex].Should().Be("30 dias");
    }

    /// <summary>
    /// Um valor fora da lista não vira o preset mais próximo: abrir a tela não
    /// pode alterar em silêncio o que o usuário tinha escolhido.
    /// </summary>
    [Fact]
    public void ADeadlineOutsideTheList_BecomesCustomWithTheNumberIntact()
    {
        var choice = new RetentionChoiceViewModel();

        choice.Load(45);

        choice.IsCustom.Should().BeTrue();
        choice.Days.Should().Be(45);
        choice.CustomDays.Should().Be(45);
    }

    [Fact]
    public void ACustomDeadline_IsClampedToWhatTheDomainAccepts()
    {
        var choice = new RetentionChoiceViewModel();
        choice.Load(45);

        choice.CustomDays = 0;
        choice.Days.Should().Be(DataRetentionPolicy.MinDays);

        choice.CustomDays = DataRetentionPolicy.MaxDays + 1000;
        choice.Days.Should().Be(DataRetentionPolicy.MaxDays);
    }

    [Fact]
    public void TheListOffersEveryPresetPlusACustomOption()
    {
        var choice = new RetentionChoiceViewModel();

        choice.Options.Should().Equal(
            "1 dia", "7 dias", "15 dias", "30 dias", "60 dias", "90 dias",
            RetentionChoiceViewModel.CustomLabel);
    }
}
