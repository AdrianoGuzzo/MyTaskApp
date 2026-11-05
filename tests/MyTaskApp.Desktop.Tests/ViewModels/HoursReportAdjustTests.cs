using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Reajustar as horas pelo relatório (ADR-102): a tarefa clicada no painel
/// mostra os períodos dela no bloco, e cada um se corrige, exclui ou ganha um
/// irmão pelos casos de uso de verdade — as mesmas regras da aba Tempo.
/// </summary>
public class HoursReportAdjustTests
{
    /// <summary>Terça, 27/10/2026, 15:00 em São Paulo.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan SaoPaulo = TimeSpan.FromHours(-3);

    private readonly FakeTimeProvider _time = new(Now);

    private readonly UserClock _clock;

    private readonly FakeTimeEntryReportQuery _query = new();

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeTimeEntryEditor _editor = new();

    private readonly FakeConfirmationDialog _confirmation = new();

    private readonly ITimeEntryRepository _entries = Substitute.For<ITimeEntryRepository>();

    private readonly List<TimeEntry> _stored = [];

    private readonly TaskItem _task = TaskItem.Create("Relatório", Now.AddDays(-60));

    public HoursReportAdjustTests()
    {
        _clock = new UserClock(_time, Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }), NullLogger<UserClock>.Instance);

        var tasks = Substitute.For<ITaskItemRepository>();
        tasks.FindByOccurrenceIdAsync(Occurrence, Arg.Any<CancellationToken>()).Returns(_task);
        _entries.FindByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _stored.SingleOrDefault(entry => entry.Id == call.Arg<Guid>()));
        _entries.ListForOccurrenceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => _stored.ToList());

        _runner.Handlers[typeof(GetHoursHistoryHandler)] = new GetHoursHistoryHandler(_query, _clock, _time);
        _runner.Handlers[typeof(TimeReportHandlers)] = new TimeReportHandlers(_query, _clock, _time);
        _runner.Handlers[typeof(AddTimeEntryHandler)] = new AddTimeEntryHandler(
            tasks, _entries, Substitute.For<IUnitOfWork>(), Substitute.For<ITaskAuditLog>(), Substitute.For<ICurrentUser>(),
            _clock, _time, NullLogger<AddTimeEntryHandler>.Instance);
        _runner.Handlers[typeof(UpdateTimeEntryHandler)] = new UpdateTimeEntryHandler(
            tasks, _entries, Substitute.For<IUnitOfWork>(), Substitute.For<ITaskAuditLog>(), Substitute.For<ICurrentUser>(),
            _clock, _time, NullLogger<UpdateTimeEntryHandler>.Instance);
        _runner.Handlers[typeof(DeleteTimeEntryHandler)] = new DeleteTimeEntryHandler(
            tasks, _entries, Substitute.For<IUnitOfWork>(), Substitute.For<ITaskAuditLog>(), Substitute.For<ICurrentUser>(),
            _clock, _time, NullLogger<DeleteTimeEntryHandler>.Instance);
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = new List<TagRow>();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid Occurrence => _task.Occurrences.Single().Id;

    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, SaoPaulo);

    private HoursReportViewModel ViewModel() =>
        new(_runner, _editor, _confirmation, _clock, NullLogger<HoursReportViewModel>.Instance);

    /// <summary>Um período da tarefa, no "banco" dos casos de uso e no do relatório.</summary>
    private TimeEntry Add(DateTimeOffset start, DateTimeOffset end, string? note = null)
    {
        var entry = TimeEntry.Manual(Occurrence, start, end, note, Now);
        _stored.Add(entry);
        _query.Rows.Add(new TimeEntryReportRow(
            entry.Id, Occurrence, _task.Id, _task.Title, null, [],
            start, end, TimeEntrySource.Manual, note, start, null, false, false));

        return entry;
    }

    /// <summary>O relatório aberto, com o dia escolhido e a tarefa aberta no painel.</summary>
    private async Task<HoursReportViewModel> OpenAsync(int? day = 14)
    {
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);

        if (day is { } chosen)
        {
            viewModel.SelectedIndex = chosen - 1;
        }

        await viewModel.OpenTaskCommand.ExecuteAsync(viewModel.TaskRows.Single());

        return viewModel;
    }

    [Fact]
    public async Task ClickingATask_ShowsItsPeriodsInTheChosenDay()
    {
        Add(Local(14, 9), Local(14, 11), "rascunho");
        Add(Local(15, 9), Local(15, 10));

        var viewModel = await OpenAsync();

        viewModel.IsTaskOpen.Should().BeTrue();
        viewModel.IsTaskListShown.Should().BeFalse();
        viewModel.OpenTask!.Title.Should().Be("Relatório");
        viewModel.OpenTaskTotal.Should().Be("2h");
        viewModel.Entries.Should().ContainSingle();
        var row = viewModel.Entries[0];
        row.DayLabel.Should().Be("qua, 14/10");
        row.RangeLabel.Should().Be("09:00 → 11:00");
        row.Meta.Should().Be("2h · Manual");
        row.Note.Should().Be("rascunho");
        row.CanEdit.Should().BeTrue();
        _query.LastCriteria!.TaskId.Should().Be(_task.Id);
        viewModel.AddEntryCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task WithoutAChosenBlock_ThePeriodsAreTheWholeMonth()
    {
        Add(Local(14, 9), Local(14, 11));
        Add(Local(15, 9), Local(15, 10));

        var viewModel = await OpenAsync(day: null);

        viewModel.Entries.Select(entry => entry.DayLabel).Should().Equal("qui, 15/10", "qua, 14/10");
        viewModel.OpenTaskTotal.Should().Be("3h");
    }

    [Fact]
    public async Task ChoosingAnotherDay_KeepsTheTaskOpen_WithThePeriodsOfThatDay()
    {
        Add(Local(14, 9), Local(14, 11));
        Add(Local(15, 9), Local(15, 10));
        var viewModel = await OpenAsync();

        viewModel.SelectedIndex = 14;

        viewModel.IsTaskOpen.Should().BeTrue();
        viewModel.Entries.Single().DayLabel.Should().Be("qui, 15/10");
        viewModel.OpenTaskTotal.Should().Be("1h");

        viewModel.SelectedIndex = 19;

        viewModel.HasNoEntries.Should().BeTrue();
        viewModel.OpenTaskTotal.Should().Be("—");
        viewModel.AddEntryCommand.CanExecute(null).Should().BeTrue("é num dia sem horas que mais se lança o esquecido");
    }

    [Fact]
    public async Task APeriodThatCrossesMidnight_SaysWhatCountsInTheDay()
    {
        Add(Local(14, 23), Local(15, 2));

        var viewModel = await OpenAsync();

        viewModel.Entries.Single().RangeLabel.Should().Be("23:00 → 15/10 02:00");
        viewModel.Entries.Single().Meta.Should().Be("1h de 3h · Manual");
    }

    [Fact]
    public async Task Editing_SavesThroughTheUseCase_AndReloadsTheReportAndTheList()
    {
        var entry = Add(Local(14, 9), Local(14, 11), "rascunho");
        var viewModel = await OpenAsync();
        var changed = 0;
        viewModel.Changed += () => changed++;
        var loads = _runner.Invoked.Count(type => type == typeof(GetHoursHistoryHandler));
        _editor.Answer = draft => draft with { EndTime = new TimeOnly(12, 0) };

        await viewModel.EditEntryCommand.ExecuteAsync(viewModel.Entries.Single());

        _editor.Asked.Single().Heading.Should().Be("Editar período");
        _editor.Asked.Single().Initial.Should().Be(new TimeEntryDraft(Oct(14), new TimeOnly(9, 0), Oct(14), new TimeOnly(11, 0), "rascunho"));
        entry.EndedAt.Should().Be(Local(14, 12));
        changed.Should().Be(1);
        _runner.Invoked.Count(type => type == typeof(GetHoursHistoryHandler)).Should().Be(loads + 1);
        viewModel.IsTaskOpen.Should().BeTrue("reajustar não tira o usuário de onde ele estava");
    }

    [Fact]
    public async Task AnOverlap_IsRefusedInTheDialog_AndNothingChanges()
    {
        var first = Add(Local(14, 9), Local(14, 11));
        Add(Local(14, 11), Local(14, 12));
        var viewModel = await OpenAsync();
        var changed = 0;
        viewModel.Changed += () => changed++;
        _editor.Answer = draft => draft with { EndTime = new TimeOnly(11, 30) };

        await viewModel.EditEntryCommand.ExecuteAsync(viewModel.Entries.Single(row => row.Item.Id == first.Id));

        _editor.LastError.Should().NotBeNullOrWhiteSpace();
        first.EndedAt.Should().Be(Local(14, 11));
        changed.Should().Be(0);
    }

    [Fact]
    public async Task Cancelling_TheDialog_ChangesNothing()
    {
        Add(Local(14, 9), Local(14, 11));
        var viewModel = await OpenAsync();
        var changed = 0;
        viewModel.Changed += () => changed++;

        await viewModel.EditEntryCommand.ExecuteAsync(viewModel.Entries.Single());

        _runner.Invoked.Should().NotContain(typeof(UpdateTimeEntryHandler));
        changed.Should().Be(0);
    }

    [Fact]
    public async Task Deleting_AsksFirst_WithTheTaskAndThePeriod()
    {
        var entry = Add(Local(14, 9), Local(14, 11));
        var viewModel = await OpenAsync();
        var changed = 0;
        viewModel.Changed += () => changed++;

        await viewModel.DeleteEntryCommand.ExecuteAsync(viewModel.Entries.Single());

        _runner.Invoked.Should().NotContain(typeof(DeleteTimeEntryHandler), "a resposta padrão é Cancelar");
        _confirmation.LastAsked!.Headline.Should().Be("Excluir registro de tempo?");
        _confirmation.LastAsked.Message.Should().Contain("Relatório").And.Contain("qua, 14/10 · 09:00 → 11:00").And.Contain("2h");
        _confirmation.LastAsked.IsIrreversible.Should().BeTrue();

        _confirmation.Answer = true;
        await viewModel.DeleteEntryCommand.ExecuteAsync(viewModel.Entries.Single());

        _entries.Received(1).Remove(entry);
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Adding_OnAPastDay_StartsTheDialogOnThatDay_AndLogsInTheTask()
    {
        Add(Local(14, 9), Local(14, 11));
        var viewModel = await OpenAsync();
        var changed = 0;
        viewModel.Changed += () => changed++;
        _editor.Answer = draft => draft with { StartTime = new TimeOnly(13, 0), EndTime = new TimeOnly(14, 30) };

        await viewModel.AddEntryCommand.ExecuteAsync(null);

        _editor.Asked.Single().Initial.Should().Be(new TimeEntryDraft(Oct(14), new TimeOnly(9, 0), Oct(14), new TimeOnly(10, 0), null));
        await _entries.Received(1).AddAsync(
            Arg.Is<TimeEntry>(entry =>
                entry.TaskOccurrenceId == Occurrence
                && entry.StartedAt == Local(14, 13)
                && entry.EndedAt == Local(14, 14, 30)),
            Arg.Any<CancellationToken>());
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Adding_WithoutAPastDay_IsTheLastHour_AsInTheTimeTab()
    {
        Add(Local(14, 9), Local(14, 11));
        var viewModel = await OpenAsync(day: null);

        await viewModel.AddEntryCommand.ExecuteAsync(null);

        _editor.Asked.Single().Initial.Should().Be(new TimeEntryDraft(Oct(27), new TimeOnly(14, 0), Oct(27), new TimeOnly(15, 0), null));
        _runner.Invoked.Should().NotContain(typeof(AddTimeEntryHandler), "o usuário cancelou");
    }

    [Fact]
    public async Task ARunningTimer_IsNotEdited_InTheReport()
    {
        var start = Local(27, 14);
        _query.Rows.Add(new TimeEntryReportRow(
            Guid.CreateVersion7(), Occurrence, _task.Id, _task.Title, null, [],
            start, null, TimeEntrySource.Timer, null, start, null, false, false));

        var viewModel = await OpenAsync(day: 27);

        var row = viewModel.Entries.Single();
        row.IsRunning.Should().BeTrue();
        row.CanEdit.Should().BeFalse();
        row.Meta.Should().Be("em andamento · Timer");
        row.RangeLabel.Should().Be("14:00 → agora");

        await viewModel.EditEntryCommand.ExecuteAsync(row);

        _editor.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task GoingBack_ShowsTheTaskListAgain()
    {
        Add(Local(14, 9), Local(14, 11));
        var viewModel = await OpenAsync();

        viewModel.CloseTask();

        viewModel.IsTaskOpen.Should().BeFalse();
        viewModel.IsTaskListShown.Should().BeTrue();
        viewModel.Entries.Should().BeEmpty();
        viewModel.AddEntryCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task AFailedLoad_SaysSo_WithoutClosingTheTask()
    {
        Add(Local(14, 9), Local(14, 11));
        var viewModel = await OpenAsync();
        _runner.FailuresByHandler[typeof(TimeReportHandlers)] = new InvalidOperationException("banco fora");

        await viewModel.OpenTaskCommand.ExecuteAsync(viewModel.OpenTask!);

        viewModel.ErrorMessage.Should().Be("Não foi possível carregar os períodos desta tarefa.");
        viewModel.IsTaskOpen.Should().BeTrue();
    }
}
