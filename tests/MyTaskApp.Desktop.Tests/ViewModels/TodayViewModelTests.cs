using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

public class TodayViewModelTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeConfirmationDialog _confirmation = new();

    private TodayViewModel ViewModel() =>
        new(_runner, _confirmation, TimeProvider.System, NullLogger<TodayViewModel>.Instance);

    private static TodayTask Row(string title, TimeOnly? time = null, bool isLate = false) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, time, isLate);

    private static TodayBoard Board(
        IReadOnlyList<TodayTask>? overdue = null,
        IReadOnlyList<TodayTask>? now = null,
        IReadOnlyList<TodayTask>? today = null,
        IReadOnlyList<TodayTask>? unscheduled = null,
        IReadOnlyList<TodayTask>? completed = null) =>
        new(Date, overdue ?? [], now ?? [], today ?? [], unscheduled ?? [], completed ?? []);

    [Fact]
    public async Task Load_ShowsTheDayInTheHeader()
    {
        _runner.Result = Board();

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Title.Should().Be("HOJE — 17/09/2026");
    }

    [Fact]
    public async Task Load_KeepsTheSectionOrderFromTheSpec()
    {
        _runner.Result = Board(
            overdue: [Row("Revisar documentação")],
            now: [Row("Verificar chamados", new TimeOnly(14, 0))],
            today: [Row("Deploy", new TimeOnly(15, 30))],
            unscheduled: [Row("Organizar documentação")],
            completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Select(section => section.Header)
            .Should().Equal("ATRASADAS", "AGORA", "HOJE", "SEM HORÁRIO", "CONCLUÍDAS");
    }

    [Fact]
    public async Task Load_HidesEmptySectionsInsteadOfShowingEmptyHeaders()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Select(section => section.Header).Should().Equal("HOJE");
    }

    [Fact]
    public async Task Load_ShowsTheScheduledTimeBesideTheTask()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Single().Items.Single().TimeLabel.Should().Be("15:30");
    }

    [Fact]
    public async Task Load_LeavesTheTimeBlankForWorkWithoutAnHour()
    {
        _runner.Result = Board(unscheduled: [Row("Organizar documentação")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Single().Items.Single().TimeLabel.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_CarriesTheLateFlagToTheRow()
    {
        _runner.Result = Board(today: [Row("Daily", new TimeOnly(9, 0), isLate: true)]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Single().Items.Single().IsLate.Should().BeTrue();
    }

    [Fact]
    public async Task Load_MarksCompletedRowsAsDone()
    {
        _runner.Result = Board(completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Single().Items.Single().IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Load_ReplacesThePreviousContentInsteadOfAppending()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Should().ContainSingle();
        viewModel.Sections.Single().Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Load_ClearsTheBusyFlagWhenItFinishes()
    {
        _runner.Result = Board();

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task TogglingAPendingRow_AsksToCompleteIt()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleAsync(viewModel.Sections.Single().Items.Single(), Ct);

        _runner.Invoked.Should().Contain(typeof(CompleteOccurrenceHandler));
    }

    [Fact]
    public async Task TogglingACompletedRow_AsksToReopenIt()
    {
        _runner.Result = Board(completed: [Row("Revisar PR")]);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleAsync(viewModel.Sections.Single().Items.Single(), Ct);

        _runner.Invoked.Should().Contain(typeof(ReopenOccurrenceHandler));
    }

    [Fact]
    public async Task Toggling_RefreshesTheBoardSoSectionsStayAccurate()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleAsync(viewModel.Sections.Single().Items.Single(), Ct);

        _runner.LastInvoked.Should().Be(typeof(GetTodayBoardHandler));
    }

    [Fact]
    public async Task BusinessFailure_IsShownToTheUserAsWritten()
    {
        _runner.Result = Board();
        _runner.NextFailure = new DomainException("Esta ocorrência já foi concluída.");

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Esta ocorrência já foi concluída.");
    }

    [Fact]
    public async Task InfrastructureFailure_IsReplacedByAMessageTheUserUnderstands()
    {
        _runner.Result = Board();
        _runner.NextFailure = new InvalidOperationException("SQLite Error 14: unable to open database file");

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Não foi possível carregar suas tarefas.");
        viewModel.ErrorMessage.Should().NotContain("SQLite");
    }

    [Fact]
    public async Task Failure_DoesNotLeaveTheScreenStuckLoading()
    {
        _runner.Result = Board();
        _runner.NextFailure = new InvalidOperationException("falha qualquer");

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task ASuccessfulReload_ClearsAPreviousError()
    {
        _runner.Result = Board();
        var viewModel = ViewModel();
        _runner.NextFailure = new DomainException("erro anterior");
        await viewModel.LoadAsync(Ct);

        await viewModel.LoadAsync(Ct);

        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task EmptyDay_IsReportedInsteadOfShowingNothing()
    {
        _runner.Result = Board();

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Should().BeEmpty();
        viewModel.IsEmpty.Should().BeTrue();
        viewModel.EmptyMessage.Should().Be("Nada para hoje. Aproveite.");
    }

    [Fact]
    public async Task HidingTheCompleted_LeavesOnlyWhatIsStillPending()
    {
        _runner.Result = Board(
            today: [Row("Deploy", new TimeOnly(15, 30))],
            completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.HideCompleted = true;

        viewModel.Sections.Select(section => section.Header).Should().Equal("HOJE");
    }

    [Fact]
    public async Task ShowingThemAgain_BringsTheSectionBack()
    {
        _runner.Result = Board(today: [Row("Deploy")], completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.HideCompleted = true;
        viewModel.HideCompleted = false;

        viewModel.Sections.Select(section => section.Header).Should().Equal("HOJE", "CONCLUÍDAS");
    }

    [Fact]
    public async Task HidingTheCompleted_DoesNotGoBackToTheDatabase()
    {
        // O quadro na tela já tem tudo o que a lista precisa. Recarregar aqui
        // custaria uma consulta e apagaria a mensagem de erro que estivesse à
        // vista — pelo simples gesto de fixar o painel.
        _runner.Result = Board(today: [Row("Deploy")], completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        var queries = _runner.Invoked.Count;

        viewModel.HideCompleted = true;

        _runner.Invoked.Should().HaveCount(queries);
    }

    [Fact]
    public async Task HidingTheCompleted_KeepsTheNumbersCountingEverything()
    {
        // Esconder não é desfazer: o progresso do dia continua contando o que
        // foi feito, e é dele que vivem o cabeçalho e o balão da bandeja.
        _runner.Result = Board(today: [Row("Deploy")], completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.HideCompleted = true;

        viewModel.TotalCount.Should().Be(2);
        viewModel.CompletedCount.Should().Be(1);
        viewModel.PendingCount.Should().Be(1);
        viewModel.ProgressLabel.Should().Be("1 de 2 concluídas");
    }

    [Fact]
    public async Task HidingTheCompleted_SurvivesTheNextLoad()
    {
        // O quadro se refresca sozinho a cada minuto: se a preferência não
        // sobrevivesse à recarga, as concluídas voltariam sem ninguém pedir.
        _runner.Result = Board(today: [Row("Deploy")], completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.HideCompleted = true;

        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Select(section => section.Header).Should().Equal("HOJE");
    }

    [Fact]
    public async Task DayFinishedWithTheCompletedHidden_SaysSoInsteadOfGoingBlank()
    {
        // Terminar o dia esvazia a lista, e painel em branco parece defeito.
        // A mensagem também não pode ser a do dia vazio: houve trabalho.
        _runner.Result = Board(completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.HideCompleted = true;

        viewModel.IsEmpty.Should().BeTrue();
        viewModel.EmptyMessage.Should().Be("Tudo concluído. Aproveite.");
    }

    [Fact]
    public void HidingTheCompleted_BeforeTheFirstLoad_HasNothingToRemount()
    {
        // O composition root liga o DataContext antes da primeira carga, e é
        // ali que a preferência chega: sem quadro nenhum, não há o que montar.
        var viewModel = ViewModel();

        viewModel.HideCompleted = true;

        viewModel.Sections.Should().BeEmpty();
    }
}
