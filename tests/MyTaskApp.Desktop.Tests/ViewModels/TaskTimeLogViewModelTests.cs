using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>A aba "Tempo" da tarefa (ADR-052): só pede e mostra; as regras são da Application.</summary>
public class TaskTimeLogViewModelTests
{
    // Terça, 06/10/2026, 18:00 em São Paulo.
    private static readonly DateTimeOffset Evening = new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();
    private readonly FakeTimeEntryEditor _editor = new();
    private readonly FakeTimeProvider _time = new(Evening);
    private readonly ActiveTimerViewModel _timer;

    private readonly TodayTask _task = new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "Implementar autenticação", TaskPriority.Normal,
        Today, null, false, Estimate: TimeSpan.FromHours(6));

    public TaskTimeLogViewModelTests() => _timer = new ActiveTimerViewModel(_time);

    private TaskTimeLogViewModel Tab(bool isCompleted = false)
    {
        var clock = new UserClock(_time, Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }), NullLogger<UserClock>.Instance);
        var tab = new TaskTimeLogViewModel(_runner, _editor, _confirmation, _timer, clock, NullLogger<TaskTimeLogViewModel>.Instance);
        tab.Load(new TaskRowViewModel(_task, isCompleted));
        return tab;
    }

    private static TimeEntryView Entry(
        int fromHour, int fromMinute, int toHour, int toMinute,
        TimeEntrySource source = TimeEntrySource.Manual,
        string? note = null,
        bool active = false)
    {
        var start = new DateTimeOffset(2026, 10, 6, fromHour, fromMinute, 0, TimeSpan.FromHours(-3));
        var end = new DateTimeOffset(2026, 10, 6, toHour, toMinute, 0, TimeSpan.FromHours(-3));

        return new TimeEntryView(
            Guid.CreateVersion7(), start, active ? null : end, source, note,
            Today, new TimeOnly(fromHour, fromMinute),
            active ? null : Today, active ? null : new TimeOnly(toHour, toMinute),
            active ? $"{fromHour:00}:{fromMinute:00} → agora" : $"{fromHour:00}:{fromMinute:00} → {toHour:00}:{toMinute:00}",
            WorkTimeFormatter.Duration(end - start),
            WorkTimeFormatter.Source(source));
    }

    private TaskTimeLogView Log(TimeSpan logged, DateTimeOffset? runningSince = null, params TimeEntryView[] entries) =>
        new(_task.OccurrenceId, _task.TaskId, logged, runningSince, _task.Estimate,
            entries.Length == 0
                ? []
                : [new TimeEntryDayView(Today, "Hoje", logged, entries)]);

    [Fact]
    public async Task Activating_LoadsTheHistory_TheTotal_AndTheEstimate()
    {
        _runner.ResultsByHandler[typeof(GetTaskTimeLogHandler)] = Log(
            TimeSpan.FromMinutes(270),
            entries: [Entry(14, 0, 15, 30, note: "Corrigi problema na API"), Entry(16, 10, 16, 52, TimeEntrySource.Timer)]);
        var tab = Tab();

        await tab.ActivateAsync(Ct);

        tab.SummaryText.Should().Be("Registrado 4h 30min");
        tab.EstimateText.Should().Be("4h 30min / 6h · 75%");
        tab.IsOverEstimate.Should().BeFalse();
        tab.Days.Single().Label.Should().Be("Hoje");
        var first = tab.Days.Single().Entries[0];
        first.RangeLabel.Should().Be("14:00 → 15:30");
        first.DurationLabel.Should().Be("1h 30min");
        first.SourceLabel.Should().Be("Manual");
        first.HasNote.Should().BeTrue();
        first.CanEdit.Should().BeTrue();
    }

    [Fact]
    public async Task OverTheEstimate_SaysByHowMuch()
    {
        _runner.ResultsByHandler[typeof(GetTaskTimeLogHandler)] = Log(TimeSpan.FromMinutes(440), entries: [Entry(9, 0, 16, 20)]);
        var tab = Tab();

        await tab.ActivateAsync(Ct);

        tab.EstimateText.Should().Be("7h 20min / 6h · +1h 20min acima da estimativa");
        tab.IsOverEstimate.Should().BeTrue();
    }

    [Fact]
    public async Task WhileItRunsHere_TheRunningPartIsShownApart_AndCountsTowardTheEstimate()
    {
        _timer.Show(new ActiveTimerView(Guid.CreateVersion7(), _task.OccurrenceId, _task.TaskId, _task.Title, Evening.AddMinutes(-27)));
        _runner.ResultsByHandler[typeof(GetTaskTimeLogHandler)] = Log(
            TimeSpan.FromMinutes(272), Evening.AddMinutes(-27), Entry(17, 33, 0, 0, TimeEntrySource.Timer, active: true));
        var tab = Tab();

        await tab.ActivateAsync(Ct);

        tab.IsRunningHere.Should().BeTrue();
        tab.TimerLabel.Should().Be("Parar");
        tab.SummaryText.Should().Be("Registrado 4h 32min · em andamento 27min");
        tab.EstimateText.Should().Be("4h 59min / 6h · 83%");
        tab.Days.Single().Entries.Single().CanEdit.Should().BeFalse("o que corre se para antes de corrigir");
        tab.Days.Single().Entries.Single().DurationLabel.Should().Be("em andamento");
        _timer.Dispose();
    }

    [Fact]
    public async Task AddingTime_OpensTheDialogForToday_AndSavesThroughTheUseCase()
    {
        _runner.ResultsByHandler[typeof(GetTaskTimeLogHandler)] = Log(TimeSpan.Zero);
        _runner.ResultsByHandler[typeof(AddTimeEntryHandler)] = Guid.CreateVersion7();
        _editor.Answer = draft => draft with { StartTime = new TimeOnly(14, 0), EndTime = new TimeOnly(15, 30) };
        var tab = Tab();
        var changed = 0;
        tab.Changed += () => changed++;

        await tab.AddEntryCommand.ExecuteAsync(null);

        var asked = _editor.Asked.Single();
        asked.Heading.Should().Be("Adicionar tempo");
        asked.AcceptLabel.Should().Be("Adicionar");
        asked.Initial.StartDate.Should().Be(Today);
        asked.Initial.EndTime.Should().Be(new TimeOnly(18, 0));
        _runner.Invoked.Should().Contain(typeof(AddTimeEntryHandler));
        changed.Should().Be(1, "a lista precisa recarregar o total");
    }

    [Fact]
    public async Task ARefusedPeriod_StaysInTheDialog_WithTheDomainMessage()
    {
        _runner.FailuresByHandler[typeof(AddTimeEntryHandler)] = new DomainException(
            "Este período se sobrepõe a outro já registrado nesta tarefa (06/10 14:00 → 15:30).");
        _editor.Answer = draft => draft;
        var tab = Tab();
        var changed = 0;
        tab.Changed += () => changed++;

        await tab.AddEntryCommand.ExecuteAsync(null);

        _editor.LastError.Should().Contain("sobrepõe");
        changed.Should().Be(0);
    }

    [Fact]
    public async Task CancellingTheDialog_SavesNothing()
    {
        var tab = Tab();

        await tab.AddEntryCommand.ExecuteAsync(null);

        _runner.Invoked.Should().NotContain(typeof(AddTimeEntryHandler));
    }

    [Fact]
    public async Task Editing_StartsFromThePeriod_AndUpdatesIt()
    {
        var entry = Entry(14, 0, 15, 30, TimeEntrySource.Timer, "antes");
        _runner.ResultsByHandler[typeof(GetTaskTimeLogHandler)] = Log(TimeSpan.FromMinutes(90), entries: [entry]);
        _editor.Answer = draft => draft with { StartTime = new TimeOnly(14, 10) };
        var tab = Tab();
        await tab.ActivateAsync(Ct);

        await tab.EditEntryCommand.ExecuteAsync(tab.Days.Single().Entries.Single());

        var asked = _editor.Asked.Single();
        asked.Heading.Should().Be("Editar período");
        asked.Initial.Should().Be(new TimeEntryDraft(Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 30), "antes"));
        _runner.Invoked.Should().Contain(typeof(UpdateTimeEntryHandler));
    }

    [Fact]
    public async Task Deleting_AsksFirst_WithThePeriodAndItsDuration()
    {
        _runner.ResultsByHandler[typeof(GetTaskTimeLogHandler)] = Log(TimeSpan.FromMinutes(90), entries: [Entry(14, 0, 15, 30)]);
        var tab = Tab();
        await tab.ActivateAsync(Ct);

        await tab.DeleteEntryCommand.ExecuteAsync(tab.Days.Single().Entries.Single());

        var asked = _confirmation.LastAsked!;
        asked.Headline.Should().Be("Excluir registro de tempo?");
        asked.Message.Should().Contain("14:00 → 15:30").And.Contain("1h 30min");
        asked.ConfirmLabel.Should().Be("Excluir");
        asked.IsIrreversible.Should().BeTrue();
        _runner.Invoked.Should().NotContain(typeof(DeleteTimeEntryHandler), "a resposta padrão é Cancelar");

        _confirmation.Answer = true;
        await tab.DeleteEntryCommand.ExecuteAsync(tab.Days.Single().Entries.Single());

        _runner.Invoked.Should().Contain(typeof(DeleteTimeEntryHandler));
    }

    [Fact]
    public async Task Play_InTheTab_StartsAndShowsTheClockRightAway()
    {
        var view = new ActiveTimerView(Guid.CreateVersion7(), _task.OccurrenceId, _task.TaskId, _task.Title, Evening);
        _runner.Enqueue<GetActiveTimerHandler>((object?)null);
        _runner.ResultsByHandler[typeof(StartTimerHandler)] = view;
        _runner.ResultsByHandler[typeof(GetTaskTimeLogHandler)] = Log(TimeSpan.Zero, Evening);
        var tab = Tab();

        await tab.ToggleTimerCommand.ExecuteAsync(null);

        tab.IsRunningHere.Should().BeTrue();
        _timer.TaskTitle.Should().Be("Implementar autenticação");
        _timer.Dispose();
    }

    [Fact]
    public async Task Play_InTheTab_WhileAnotherRuns_AsksTheSameQuestionAsTheList()
    {
        _runner.Enqueue<GetActiveTimerHandler>(
            new ActiveTimerView(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Corrigir dashboard", Evening));
        var tab = Tab();

        await tab.ToggleTimerCommand.ExecuteAsync(null);

        _confirmation.LastAsked!.ConfirmLabel.Should().Be("Parar e iniciar");
        _confirmation.LastAsked.Message.Should().Contain("\"Corrigir dashboard\"").And.Contain("\"Implementar autenticação\"?");
        _runner.Invoked.Should().NotContain(typeof(StartTimerHandler));
    }

    [Fact]
    public void ACompletedTask_CannotBeStarted_FromTheTab()
    {
        var tab = Tab(isCompleted: true);

        tab.CanToggleTimer.Should().BeFalse();
    }

    [Fact]
    public void Detaching_LetsGoOfTheAppClock()
    {
        var tab = Tab();
        tab.Detach();
        var notified = false;
        tab.PropertyChanged += (_, _) => notified = true;

        _timer.Show(new ActiveTimerView(Guid.CreateVersion7(), _task.OccurrenceId, _task.TaskId, _task.Title, Evening));

        notified.Should().BeFalse();
        _timer.Dispose();
    }
}

/// <summary>Os campos do diálogo "Adicionar tempo" (ADR-052).</summary>
public class TimeEntryEditorViewModelTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static TimeEntryEditorViewModel Editor(TimeEntryDraft initial, Func<TimeEntryDraft, Task<string?>>? save = null) =>
        new(new TimeEntryEditorRequest(
            "Adicionar tempo",
            "Adicionar",
            initial,
            Today,
            (draft, _) => save?.Invoke(draft) ?? Task.FromResult<string?>(null)));

    [Fact]
    public void TheFields_StartFromTheInitialPeriod_AndShowItsDuration()
    {
        var editor = Editor(new TimeEntryDraft(Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 30), "nota"));

        editor.Draft().Should().Be(new TimeEntryDraft(Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 30), "nota"));
        editor.DurationText.Should().Be("1h 30min");
        editor.ShowsEndsLater.Should().BeFalse();
        editor.CanAccept.Should().BeTrue();
    }

    [Fact]
    public void AnEndBeforeTheStart_OffersTheNextDay_WithoutGuessing()
    {
        var editor = Editor(new TimeEntryDraft(Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 0), null));
        editor.StartText = "2300";
        editor.EndText = "0130";

        editor.ShowsEndsLater.Should().BeTrue();
        editor.Draft()!.EndDate.Should().Be(Today, "virar o dia é escolha do usuário");
        editor.DurationText.Should().BeEmpty();

        editor.EndsLater = true;

        editor.Draft()!.EndDate.Should().Be(Today.AddDays(1));
        editor.DurationText.Should().Be("2h 30min");
        editor.EndsLaterLabel.Should().Be("Termina no dia seguinte");
    }

    [Fact]
    public void APeriodThatCrossedMidnight_OpensWithTheBoxChecked()
    {
        var editor = Editor(new TimeEntryDraft(Today, new TimeOnly(23, 0), Today.AddDays(1), new TimeOnly(1, 0), null));

        editor.EndsLater.Should().BeTrue();
        editor.ShowsEndsLater.Should().BeTrue();
        editor.Draft()!.EndDate.Should().Be(Today.AddDays(1));
    }

    [Fact]
    public async Task ARefusal_StaysOnScreen_AndAcceptingAgainClearsIt()
    {
        var answers = new Queue<string?>(["O horário final deve ser posterior ao horário inicial.", null]);
        var editor = Editor(
            new TimeEntryDraft(Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 0), null),
            _ => Task.FromResult(answers.Dequeue()));

        (await editor.AcceptAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        editor.ErrorMessage.Should().Contain("posterior");

        (await editor.AcceptAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        editor.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void AnEmptyField_CannotBeAccepted()
    {
        var editor = Editor(new TimeEntryDraft(Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 0), null));
        editor.EndText = string.Empty;

        editor.CanAccept.Should().BeFalse();
        editor.Draft().Should().BeNull();
    }

    [Fact]
    public void TheFields_OpenWrittenOut()
    {
        var editor = Editor(new TimeEntryDraft(Today, new TimeOnly(8, 5), Today, new TimeOnly(9, 0), null));

        editor.DateText.Should().Be("06/10/2026");
        editor.StartText.Should().Be("08:05");
        editor.EndText.Should().Be("09:00");
    }

    [Fact]
    public void TypingDigitsOnly_IsReadAsTheTime_AndWrittenOutOnLeaving()
    {
        var editor = Editor(new TimeEntryDraft(Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 0), null));

        editor.DateText = "0510";
        editor.StartText = "0831";
        editor.EndText = "1015";

        editor.Draft().Should().Be(new TimeEntryDraft(Today.AddDays(-1), new TimeOnly(8, 31), Today.AddDays(-1), new TimeOnly(10, 15), null));
        editor.DurationText.Should().Be("1h 44min");

        editor.Tidy();

        editor.DateText.Should().Be("05/10/2026");
        editor.StartText.Should().Be("08:31");
        editor.EndText.Should().Be("10:15");
        editor.InputError.Should().BeNull();
    }

    [Fact]
    public void AnUnreadableField_ComplainsOnlyOnLeaving_AndTheComplaintGoesWhenFixed()
    {
        var editor = Editor(new TimeEntryDraft(Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 0), null));

        editor.StartText = "2560";

        editor.CanAccept.Should().BeFalse();
        editor.InputError.Should().BeNull("no meio da digitação ainda não é erro");

        editor.Tidy();

        editor.InputError.Should().StartWith("Início inválido");
        editor.StartText.Should().Be("2560", "o que não foi lido fica como foi digitado");

        editor.StartText = "1430";

        editor.InputError.Should().BeNull();
        editor.CanAccept.Should().BeTrue();
    }

    [Fact]
    public void PickingInTheCalendar_WritesTheDay()
    {
        var editor = Editor(new TimeEntryDraft(Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 0), null));

        editor.PickDate(new DateOnly(2026, 9, 30));

        editor.DateText.Should().Be("30/09/2026");
        editor.Draft()!.StartDate.Should().Be(new DateOnly(2026, 9, 30));
    }
}
