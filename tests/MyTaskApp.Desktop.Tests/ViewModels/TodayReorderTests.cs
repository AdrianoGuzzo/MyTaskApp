using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O lado do ViewModel do arrasto (ADR-022). O gesto em si — o item levantado,
/// o pouso — não é testado: nada disso falha de um jeito que um teste headless
/// capturasse. O que é testado é o que dá para quebrar sem perceber: pedir o
/// caso de uso certo, não recarregar depois, e desfazer quando a gravação falha.
/// </summary>
public class TodayReorderTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeConfirmationDialog _confirmation = new();

    private TodayViewModel ViewModel() =>
        new(
            _runner,
            _confirmation,
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

    private static TodayTask Row(string title) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false);

    private static TodayBoard Board(
        IReadOnlyList<TodayTask>? unscheduled = null,
        IReadOnlyList<TodayTask>? completed = null) =>
        new(Date, [], [], [], unscheduled ?? [], completed ?? []);

    private async Task<(TodayViewModel ViewModel, TodaySectionViewModel Section)> LoadedAsync(
        params string[] titles)
    {
        _runner.Result = Board(unscheduled: [.. titles.Select(Row)]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        _runner.Invoked.Clear();

        return (viewModel, viewModel.Sections.Single());
    }

    private static IReadOnlyList<string> Titles(TodaySectionViewModel section) =>
        [.. section.Items.Select(row => row.Title)];

    [Fact]
    public async Task Move_PutsTheRowWhereItWasDropped()
    {
        var (_, section) = await LoadedAsync("Primeira", "Segunda", "Terceira");

        section.Move(2, 0);

        Titles(section).Should().Equal("Terceira", "Primeira", "Segunda");
    }

    [Fact]
    public async Task Reorder_AsksForTheReorderUseCase()
    {
        var (viewModel, section) = await LoadedAsync("Primeira", "Segunda");
        section.Move(1, 0);

        await viewModel.ReorderAsync(new SectionReorder(section, 1, 0), Ct);

        _runner.LastInvoked.Should().Be<ReorderOccurrencesHandler>();
    }

    /// <summary>
    /// A asserção que guarda a animação: recarregar aqui recriaria todas as
    /// linhas no meio do pouso do item.
    /// </summary>
    [Fact]
    public async Task Reorder_DoesNotReloadTheBoard()
    {
        var (viewModel, section) = await LoadedAsync("Primeira", "Segunda");
        section.Move(1, 0);

        await viewModel.ReorderAsync(new SectionReorder(section, 1, 0), Ct);

        _runner.Invoked.Should().NotContain(typeof(GetTodayBoardHandler));
    }

    [Fact]
    public async Task Reorder_KeepsTheSameRowInstances()
    {
        var (viewModel, section) = await LoadedAsync("Primeira", "Segunda");
        var moved = section.Items[1];
        section.Move(1, 0);

        await viewModel.ReorderAsync(new SectionReorder(section, 1, 0), Ct);

        viewModel.Sections.Single().Items[0].Should().BeSameAs(moved);
    }

    [Fact]
    public async Task WhenSavingFails_TheRowGoesBackAndTheMessageAppears()
    {
        var (viewModel, section) = await LoadedAsync("Primeira", "Segunda", "Terceira");
        section.Move(2, 0);
        _runner.NextFailure = new DomainException("Não deu.");

        await viewModel.ReorderAsync(new SectionReorder(section, 2, 0), Ct);

        Titles(section).Should().Equal("Primeira", "Segunda", "Terceira");
        viewModel.ErrorMessage.Should().Be("Não deu.");
    }

    [Fact]
    public async Task WhenSavingFailsForAnUnexpectedReason_TheMessageIsTheReadableOne()
    {
        var (viewModel, section) = await LoadedAsync("Primeira", "Segunda");
        section.Move(1, 0);
        _runner.NextFailure = new InvalidOperationException("detalhe técnico");

        await viewModel.ReorderAsync(new SectionReorder(section, 1, 0), Ct);

        viewModel.ErrorMessage.Should().Be("Não foi possível salvar a nova ordem.");
        Titles(section).Should().Equal("Primeira", "Segunda");
    }

    /// <summary>
    /// O quadro guardado em memória tem de concordar com a tela: fixar o painel
    /// remonta a lista a partir dele, e sem isso a ordem antiga ressuscitaria.
    /// </summary>
    [Fact]
    public async Task AfterAReorder_HidingTheCompletedSectionKeepsTheNewOrder()
    {
        _runner.Result = Board(
            unscheduled: [Row("Primeira"), Row("Segunda"), Row("Terceira")],
            completed: [Row("Feita")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        _runner.Invoked.Clear();

        var section = viewModel.Sections.First(item => item.Header == "SEM HORÁRIO");
        section.Move(2, 0);
        await viewModel.ReorderAsync(new SectionReorder(section, 2, 0), Ct);

        viewModel.HideCompleted = true;

        Titles(viewModel.Sections.Single(item => item.Header == "SEM HORÁRIO"))
            .Should().Equal("Terceira", "Primeira", "Segunda");
    }

    /// <summary>
    /// Um arrasto atravessa a fronteira do tique de 60 s com facilidade. Se a
    /// recarga passasse por cima, os containers sumiriam debaixo do ponteiro.
    /// </summary>
    [Fact]
    public async Task WhileADragIsRunning_TheAutoRefreshLeavesTheListAlone()
    {
        var (viewModel, section) = await LoadedAsync("Primeira", "Segunda");
        section.Move(1, 0);

        viewModel.IsReordering = true;
        await viewModel.LoadAsync(Ct);

        _runner.Invoked.Should().BeEmpty();
        viewModel.Sections.Single().Should().BeSameAs(section);
        Titles(section).Should().Equal("Segunda", "Primeira");
    }

    [Fact]
    public async Task TheCompletedSection_DoesNotOfferReordering()
    {
        _runner.Result = Board(
            unscheduled: [Row("Pendente")],
            completed: [Row("Feita")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Single(item => item.Header == "CONCLUÍDAS")
            .CanReorder.Should().BeFalse();

        viewModel.Sections.Single(item => item.Header == "SEM HORÁRIO")
            .CanReorder.Should().BeTrue();
    }
}
