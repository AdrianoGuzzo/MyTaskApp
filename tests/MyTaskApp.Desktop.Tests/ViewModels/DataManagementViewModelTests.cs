using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A janela de Arquivados/Lixeira (§3, §5, §7, §11). O que se afirma aqui é o
/// que o usuário lê antes de decidir — sobretudo na única operação que não tem
/// volta.
/// </summary>
public class DataManagementViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();

    private DataManagementViewModel ViewModel() =>
        new(_runner, _confirmation, NullLogger<DataManagementViewModel>.Instance);

    private static ChecklistSummaryRow ArchivedRow(string title = "Fechar o mês") =>
        new(
            Guid.CreateVersion7(),
            title,
            "Conferir os lançamentos",
            TaskPriority.Normal,
            Now.AddDays(-60),
            Now.AddDays(-40),
            Now.AddDays(-5),
            null,
            null,
            TotalItems: 3,
            CompletedItems: 3);

    private static ChecklistSummaryRow TrashedRow(
        string title = "Coisa antiga",
        DateTimeOffset? archivedAt = null) =>
        new(
            Guid.CreateVersion7(),
            title,
            null,
            TaskPriority.Normal,
            Now.AddDays(-60),
            Now.AddDays(-40),
            archivedAt,
            Now.AddDays(-2),
            "adriano",
            TotalItems: 1,
            CompletedItems: 1);

    private void AreaReturns(DataRetentionPolicy retention, params ChecklistSummaryRow[] rows) =>
        _runner.ResultsByHandler[typeof(GetChecklistArchiveHandler)] =
            new ChecklistArchiveView(ChecklistScope.Archived, rows, retention, Now);

    private void RetentionIs(DataRetentionPolicy policy) =>
        _runner.ResultsByHandler[typeof(GetDataRetentionSettingsHandler)] = policy;

    private ChecklistCardViewModel Card(ChecklistSummaryRow row, int trashDays = 30) =>
        new(row, new DataRetentionPolicy(false, 30, trashDays), Now);

    // ------------------------------------------------------------------
    // Confirmações
    // ------------------------------------------------------------------

    /// <summary>
    /// O §7: a confirmação forte precisa se apresentar como irreversível, e não
    /// apenas dizê-lo em letra miúda.
    /// </summary>
    [Fact]
    public void ThePurgePrompt_IsMarkedIrreversibleAndSaysThereIsNoComingBack()
    {
        var prompt = DataManagementViewModel.PurgePrompt("Fechar o mês");

        prompt.IsIrreversible.Should().BeTrue();
        prompt.ConfirmLabel.Should().Be("Excluir definitivamente");
        prompt.Message.Should().Contain("Não será possível restaurá-lo");
        prompt.Message.Should().Contain("Fechar o mês");
    }

    [Fact]
    public void TheTrashPrompt_IsNotMarkedIrreversibleBecauseItIsNot()
    {
        DataManagementViewModel.TrashPrompt("Fechar o mês", 30)
            .IsIrreversible.Should().BeFalse();
    }

    [Fact]
    public async Task DecliningThePurge_DeletesNothing()
    {
        AreaReturns(DataRetentionPolicy.Factory);
        _confirmation.Answer = false;

        var viewModel = ViewModel();
        await viewModel.PurgeAsync(Card(TrashedRow()), Ct);

        _runner.Invoked.Should().NotContain(typeof(PurgeChecklistHandler));
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmingThePurge_SaysPlainlyThatItCannotBeUndone()
    {
        AreaReturns(DataRetentionPolicy.Factory);
        _confirmation.Answer = true;

        var viewModel = ViewModel();
        await viewModel.PurgeAsync(Card(TrashedRow()), Ct);

        _runner.Invoked.Should().Contain(typeof(PurgeChecklistHandler));
        viewModel.StatusMessage.Should()
            .Be("Checklist excluído definitivamente. Essa ação não pode ser desfeita.");
    }

    // ------------------------------------------------------------------
    // Concluídos
    // ------------------------------------------------------------------

    private static ChecklistSummaryRow ConcludedRow(string title = "Pagar o boleto") =>
        new(
            Guid.CreateVersion7(),
            title,
            "Vence dia 10",
            TaskPriority.Normal,
            Now.AddDays(-3),
            Now.AddDays(-2),
            null,
            null,
            null,
            TotalItems: 1,
            CompletedItems: 1);

    [Fact]
    public void AConcludedCard_SaysItIsConcludedAndWhen()
    {
        var card = Card(ConcludedRow());

        card.StateLabel.Should().Be("CONCLUÍDO");
        card.WhenLabel.Should().StartWith("Concluído em ");
        card.IsArchived.Should().BeFalse();
        card.IsInTrash.Should().BeFalse();
        card.HasDaysLeft.Should().BeFalse();
    }

    [Fact]
    public async Task Loading_FillsTheConcludedArea()
    {
        RetentionIs(DataRetentionPolicy.Factory);
        AreaReturns(DataRetentionPolicy.Factory, ConcludedRow());

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Concluded.Select(card => card.Title).Should().Equal("Pagar o boleto");
        viewModel.HasConcluded.Should().BeTrue();
    }

    [Fact]
    public async Task ArchivingAConcludedChecklist_DoesNotAskAndTellsTheMainList()
    {
        AreaReturns(DataRetentionPolicy.Factory);

        var viewModel = ViewModel();
        var notified = 0;
        viewModel.ChecklistsChanged += () => notified++;

        await viewModel.ArchiveAsync(Card(ConcludedRow()), Ct);

        _runner.Invoked.Should().Contain(typeof(ArchiveChecklistHandler));
        _confirmation.Asked.Should().BeEmpty();
        viewModel.StatusMessage.Should().Be("Checklist arquivado.");
        notified.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Restauração
    // ------------------------------------------------------------------

    [Fact]
    public async Task RestoringFromTheArchive_ConfirmsAndReloadsBothAreas()
    {
        AreaReturns(DataRetentionPolicy.Factory);

        var viewModel = ViewModel();
        var notified = 0;
        viewModel.ChecklistsChanged += () => notified++;

        await viewModel.RestoreAsync(Card(ArchivedRow()), Ct);

        _runner.Invoked.Should().Contain(typeof(RestoreChecklistHandler));
        viewModel.StatusMessage.Should().Be("Checklist restaurado com sucesso.");

        // A lista principal precisa saber: o checklist voltou para ela.
        notified.Should().Be(1);
    }

    /// <summary>
    /// Restaurar e não achar em "Hoje" porque o checklist continuou arquivado
    /// seria a surpresa mais fácil de evitar aqui.
    /// </summary>
    [Fact]
    public async Task RestoringSomethingThatWasArchivedBeforeBeingDeleted_SaysWhereItWent()
    {
        AreaReturns(DataRetentionPolicy.Factory);

        var viewModel = ViewModel();
        var card = Card(TrashedRow(archivedAt: Now.AddDays(-10)));

        card.WasArchivedBeforeTrash.Should().BeTrue();

        await viewModel.RestoreFromTrashAsync(card, Ct);

        viewModel.StatusMessage.Should().Be("Checklist restaurado para os arquivados.");
    }

    [Fact]
    public async Task RestoringSomethingThatWasNeverArchived_UsesThePlainMessage()
    {
        AreaReturns(DataRetentionPolicy.Factory);

        var viewModel = ViewModel();
        await viewModel.RestoreFromTrashAsync(Card(TrashedRow()), Ct);

        viewModel.StatusMessage.Should().Be("Checklist restaurado com sucesso.");
    }

    // ------------------------------------------------------------------
    // Detalhes e auditoria
    // ------------------------------------------------------------------

    [Fact]
    public async Task OpeningTheDetails_LoadsTheTrailOnceAndNotOncePerClick()
    {
        var taskId = Guid.CreateVersion7();

        _runner.ResultsByHandler[typeof(GetChecklistAuditHandler)] =
            (IReadOnlyList<TaskAuditEntry>)
            [
                TaskAuditEntry.ByUser(taskId, "Fechar o mês", TaskAuditOperation.Created, Now, "adriano"),
                TaskAuditEntry.BySystem(taskId, "Fechar o mês", TaskAuditOperation.Archived, Now, "30 dias."),
            ];

        var viewModel = ViewModel();
        var card = Card(ArchivedRow());

        await viewModel.ToggleDetailsAsync(card, Ct);
        await viewModel.ToggleDetailsAsync(card, Ct);
        await viewModel.ToggleDetailsAsync(card, Ct);

        card.Audit.Should().HaveCount(2);
        _runner.Invoked.Count(handler => handler == typeof(GetChecklistAuditHandler))
            .Should().Be(1);
    }

    [Fact]
    public async Task AChecklistWithNoTrail_SaysSoInsteadOfShowingAnEmptyBox()
    {
        _runner.ResultsByHandler[typeof(GetChecklistAuditHandler)] =
            (IReadOnlyList<TaskAuditEntry>)[];

        var viewModel = ViewModel();
        var card = Card(ArchivedRow());

        await viewModel.ShowAuditAsync(card, Ct);

        card.HasNoAudit.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Configurações
    // ------------------------------------------------------------------

    [Fact]
    public async Task Loading_ShowsTheStoredDeadlines()
    {
        RetentionIs(new DataRetentionPolicy(true, 60, 15));
        AreaReturns(DataRetentionPolicy.Factory);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.AutoArchiveEnabled.Should().BeTrue();
        viewModel.ArchiveAfter.Days.Should().Be(60);
        viewModel.TrashRetention.Days.Should().Be(15);
    }

    [Fact]
    public async Task Saving_SendsWhatTheScreenShows()
    {
        RetentionIs(DataRetentionPolicy.Factory);
        AreaReturns(DataRetentionPolicy.Factory);

        var viewModel = ViewModel();
        viewModel.AutoArchiveEnabled = true;
        viewModel.ArchiveAfter.Load(90);
        viewModel.TrashRetention.Load(7);

        await viewModel.SaveSettingsAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(UpdateDataRetentionSettingsHandler));
        viewModel.SettingsStatus.Should().Be("Configurações salvas.");
    }

    [Fact]
    public async Task RestoringTheFactoryDefault_ShowsItButDoesNotSaveOnItsOwn()
    {
        var viewModel = ViewModel();
        viewModel.AutoArchiveEnabled = true;

        await viewModel.RestoreDefaultSettingsAsync(Ct);

        viewModel.AutoArchiveEnabled.Should().BeFalse();
        viewModel.SettingsStatus.Should().Contain("Salve para aplicar");
        _runner.Invoked.Should().NotContain(typeof(UpdateDataRetentionSettingsHandler));
    }
}
