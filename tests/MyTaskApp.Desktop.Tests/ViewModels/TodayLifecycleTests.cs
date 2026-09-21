using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Arquivar e excluir a partir da lista principal (§1, §4, §12). O que estes
/// testes guardam é o pedaço da regra que mora na tela: perguntar antes, com o
/// prazo certo, e não fazer nada quando a resposta é não.
/// </summary>
public class TodayLifecycleTests
{
    private static readonly DateOnly Date = new(2026, 9, 21);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new()
    {
        Result = new TodayBoard(Date, [], [], [], [], []),
    };

    private readonly FakeConfirmationDialog _confirmation = new();

    private readonly FakeClipboardWriter _clipboard = new();

    private TodayViewModel ViewModel() =>
        new(_runner, _confirmation, _clipboard, TimeProvider.System, NullLogger<TodayViewModel>.Instance);

    private static TaskRowViewModel Row(string title = "Fechar o mês") =>
        new(
            new TodayTask(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                title,
                TaskPriority.Normal,
                Date,
                null,
                false),
            isCompleted: false);

    private void RetentionIs(int trashDays) =>
        _runner.ResultsByHandler[typeof(GetDataRetentionSettingsHandler)] =
            new DataRetentionPolicy(false, 30, trashDays);

    // ------------------------------------------------------------------
    // Arquivar
    // ------------------------------------------------------------------

    /// <summary>
    /// Arquivar não perde nada e se desfaz em dois cliques. Perguntar aqui só
    /// treinaria o usuário a confirmar sem ler — encarecendo a pergunta que
    /// realmente importa, a da exclusão.
    /// </summary>
    [Fact]
    public async Task Archiving_DoesNotAskForConfirmation()
    {
        await ViewModel().ArchiveAsync(Row(), Ct);

        _confirmation.Asked.Should().BeEmpty();
        _runner.Invoked.Should().Contain(typeof(ArchiveChecklistHandler));
    }

    [Fact]
    public async Task Archiving_RefreshesTheBoardAndConfirmsInPlainWords()
    {
        var viewModel = ViewModel();

        await viewModel.ArchiveAsync(Row(), Ct);

        _runner.Invoked.Should().Contain(typeof(GetTodayBoardHandler));
        viewModel.StatusMessage.Should().Be("Checklist arquivado com sucesso.");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task AnArchiveThatFails_SaysSoAndDoesNotClaimSuccess()
    {
        var viewModel = ViewModel();
        _runner.NextFailure = new InvalidOperationException("banco fora do ar");

        await viewModel.ArchiveAsync(Row(), Ct);

        viewModel.StatusMessage.Should().BeNull();
        viewModel.ErrorMessage.Should().Be("Não foi possível arquivar este checklist.");
    }

    // ------------------------------------------------------------------
    // Lixeira
    // ------------------------------------------------------------------

    /// <summary>
    /// A confirmação do §4 precisa dizer o prazo <b>real</b>: uma mensagem com
    /// "30 dias" fixo mentiria para quem mudou a configuração.
    /// </summary>
    [Fact]
    public async Task MovingToTrash_AsksWithTheDeadlineActuallyConfigured()
    {
        RetentionIs(7);
        _confirmation.Answer = true;

        await ViewModel().MoveToTrashAsync(Row(), Ct);

        _confirmation.LastAsked!.Message.Should().Contain("7 dias");
        _confirmation.LastAsked.Message.Should().Contain("Fechar o mês");
        _confirmation.LastAsked.IsIrreversible.Should().BeFalse();
    }

    [Fact]
    public async Task TheTrashPrompt_SaysTheChecklistCanBeRestored()
    {
        RetentionIs(30);
        _confirmation.Answer = true;

        await ViewModel().MoveToTrashAsync(Row(), Ct);

        _confirmation.LastAsked!.Message.Should().Contain("restaurado");
        _confirmation.LastAsked.Message.Should().Contain("excluído definitivamente");
    }

    [Fact]
    public async Task SayingNo_LeavesTheChecklistExactlyWhereItWas()
    {
        RetentionIs(30);
        _confirmation.Answer = false;

        var viewModel = ViewModel();
        await viewModel.MoveToTrashAsync(Row(), Ct);

        _runner.Invoked.Should().NotContain(typeof(MoveChecklistToTrashHandler));
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task SayingYes_MovesItAndSaysWhereItWent()
    {
        RetentionIs(30);
        _confirmation.Answer = true;

        var viewModel = ViewModel();
        await viewModel.MoveToTrashAsync(Row(), Ct);

        _runner.Invoked.Should().Contain(typeof(MoveChecklistToTrashHandler));
        viewModel.StatusMessage.Should().Be("Checklist movido para a lixeira.");
    }

    /// <summary>
    /// Um prazo de um dia não vira "1 dias".
    /// </summary>
    [Fact]
    public async Task TheDeadlineIsWrittenInProperPortuguese()
    {
        RetentionIs(1);
        _confirmation.Answer = true;

        await ViewModel().MoveToTrashAsync(Row(), Ct);

        _confirmation.LastAsked!.Message.Should().Contain("1 dia.");
    }

    [Fact]
    public async Task OpeningDataManagement_AsksTheShellForTheWindow()
    {
        var viewModel = ViewModel();
        var asked = 0;

        viewModel.DataManagementRequested += () => asked++;
        viewModel.OpenDataManagement();

        asked.Should().Be(1);
    }
}
