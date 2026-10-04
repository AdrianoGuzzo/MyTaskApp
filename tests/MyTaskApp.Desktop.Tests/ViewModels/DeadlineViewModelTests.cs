using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O prazo na tela (ADR-050): a linha só copia o que a Application escreveu,
/// a seção PRAZOS tem lugar e ordem próprios, o HUD mostra só o que aperta, e
/// os comandos pedem o caso de uso certo.
/// </summary>
public class DeadlineViewModelTests
{
    private static readonly DateOnly Date = new(2026, 10, 6);
    private static readonly TaskDeadline Friday = new(new DateOnly(2026, 10, 9), new TimeOnly(18, 0));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private TodayViewModel Today() =>
        new(_runner, new FakeConfirmationDialog(), new FakeClipboardWriter(), TimeProvider.System, NullLogger<TodayViewModel>.Instance);

    private static TaskDeadlineView View(
        DeadlineSeverity severity,
        string label,
        DeadlineStatus status = DeadlineStatus.OnTrack) =>
        new(Friday, status, severity, TimeSpan.FromDays(3), label, "sex 09/10 18:00", "3 dias restantes");

    private static TodayTask Task(string title, TaskDeadlineView? deadline = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false,
            Deadline: deadline, NextAction: deadline is null ? null : "Criar endpoint", Estimate: TimeSpan.FromHours(6));

    private static TodayBoard Board(
        IReadOnlyList<TodayTask>? overdue = null,
        IReadOnlyList<TodayTask>? today = null,
        IReadOnlyList<TodayTask>? deadlines = null,
        IReadOnlyList<TodayTask>? unscheduled = null) =>
        new(Date, overdue ?? [], [], today ?? [], unscheduled ?? [], []) { Deadlines = deadlines ?? [] };

    [Fact]
    public async Task Deadlines_SitBetweenTodayAndUnscheduled_WithTheSummaryInTheHeader()
    {
        _runner.Result = Board(
            overdue: [Task("Publicar release", View(DeadlineSeverity.Overdue, "ATRASADA · há 3 horas", DeadlineStatus.Overdue))],
            today: [Task("Deploy")],
            deadlines: [Task("Integração Jira", View(DeadlineSeverity.Urgent, "URGENTE · vence hoje às 18:00", DeadlineStatus.DueToday))],
            unscheduled: [Task("Comprar pão")]);

        var viewModel = Today();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Select(section => section.Header).Should().Equal(
            "ATRASADAS", "HOJE", "PRAZOS · 1 atrasada · 1 hoje", "SEM HORÁRIO");

        var deadlines = viewModel.Sections.Single(section => section.Section == TodaySection.Deadlines);
        deadlines.CanReorder.Should().BeFalse("PRAZOS se ordena pelo prazo");
        deadlines.Items.Should().AllSatisfy(row => row.IsFixedOrder.Should().BeTrue());
    }

    [Fact]
    public async Task InTheHud_OnlyPressingDeadlinesShow()
    {
        _runner.Result = Board(deadlines:
        [
            Task("Integração Jira", View(DeadlineSeverity.Attention, "ATENÇÃO · vence amanhã às 18:00", DeadlineStatus.DueSoon)),
            Task("Refatorar módulo", View(DeadlineSeverity.Normal, "7 dias restantes")),
        ]);

        var viewModel = Today();
        await viewModel.LoadAsync(Ct);

        viewModel.PressingDeadlinesOnly = true;

        viewModel.Sections.Single().Items.Select(row => row.Title).Should().Equal("Integração Jira");
        viewModel.PendingCount.Should().Be(2, "esconder não é desfazer");

        viewModel.PressingDeadlinesOnly = false;

        viewModel.Sections.Single().Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task InTheHud_ASectionOfRelaxedDeadlinesDisappears()
    {
        _runner.Result = Board(deadlines: [Task("Refatorar módulo", View(DeadlineSeverity.Normal, "7 dias restantes"))]);

        var viewModel = Today();
        await viewModel.LoadAsync(Ct);
        viewModel.PressingDeadlinesOnly = true;

        viewModel.Sections.Should().BeEmpty();
    }

    [Theory]
    [InlineData(DeadlineSeverity.Normal, false, false, false)]
    [InlineData(DeadlineSeverity.Attention, true, false, false)]
    [InlineData(DeadlineSeverity.Urgent, false, true, false)]
    [InlineData(DeadlineSeverity.Overdue, false, false, true)]
    public void TheRow_CopiesTheSeverity(DeadlineSeverity severity, bool attention, bool urgent, bool overdue)
    {
        var row = new TaskRowViewModel(Task("Integração Jira", View(severity, "rótulo")), isCompleted: false);

        row.HasDeadline.Should().BeTrue();
        row.DeadlineLabel.Should().Be("rótulo");
        row.IsDeadlineAttention.Should().Be(attention);
        row.IsDeadlineUrgent.Should().Be(urgent);
        row.IsDeadlineOverdue.Should().Be(overdue);
        row.DeadlineMenuHeader.Should().Be("Alterar prazo");
        row.DeadlineTip.Should().Contain("sex 09/10 18:00").And.Contain("Próxima ação: Criar endpoint");
    }

    [Fact]
    public void ARowWithoutDeadline_OffersToSetOne()
    {
        var row = new TaskRowViewModel(Task("Comprar pão"), isCompleted: false);

        row.HasDeadline.Should().BeFalse();
        row.DeadlineMenuHeader.Should().Be("Definir prazo");
        row.CanChangeDeadline.Should().BeTrue();
        row.CanClearDeadline.Should().BeFalse();
        row.DeadlineTip.Should().BeEmpty();
    }

    [Fact]
    public void ACompletedRow_KeepsTheDeadlineAsHistory()
    {
        var row = new TaskRowViewModel(
            Task("Revisar contrato", View(DeadlineSeverity.Normal, "concluída 3 horas após o prazo", DeadlineStatus.Missed)),
            isCompleted: true);

        row.CanChangeDeadline.Should().BeFalse();
        row.CanClearDeadline.Should().BeFalse();
        row.IsDeadlineMissed.Should().BeTrue();
        row.IsDeadlineUrgent.Should().BeFalse("concluída não pede atenção");
    }

    [Fact]
    public async Task AShortcutFromTheMenu_AsksTheUseCase_AndReloads()
    {
        _runner.Result = Board(unscheduled: [Task("Comprar pão")]);
        _runner.ResultsByHandler[typeof(SetDeadlineHandler)] = View(DeadlineSeverity.Attention, "ATENÇÃO · vence amanhã às 18:00");
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);
        var row = viewModel.Sections.Single().Items.Single();

        await viewModel.SetDeadlineShortcutAsync(new DeadlineShortcutRequest(row, DeadlineShortcut.Tomorrow), Ct);

        _runner.Invoked.Should().ContainInOrder(typeof(SetDeadlineHandler), typeof(GetTodayBoardHandler));
        viewModel.StatusMessage.Should().Be("Prazo: sex 09/10 18:00.");
    }

    [Fact]
    public async Task ARefusedShortcut_ShowsTheDomainMessage()
    {
        _runner.Result = Board(unscheduled: [Task("Comprar pão")]);
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);
        _runner.FailuresByHandler[typeof(SetDeadlineHandler)] = new DomainException("O prazo precisa ficar no futuro.");

        await viewModel.SetDeadlineShortcutAsync(
            new DeadlineShortcutRequest(viewModel.Sections.Single().Items.Single(), DeadlineShortcut.Today), Ct);

        viewModel.ErrorMessage.Should().Be("O prazo precisa ficar no futuro.");
    }

    [Fact]
    public async Task RemovingTheDeadline_AsksTheUseCase()
    {
        _runner.Result = Board(deadlines: [Task("Integração Jira", View(DeadlineSeverity.Normal, "3 dias restantes"))]);
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);

        await viewModel.ClearDeadlineAsync(viewModel.Sections.Single().Items.Single(), Ct);

        _runner.Invoked.Should().Contain(typeof(ClearDeadlineHandler));
        viewModel.StatusMessage.Should().Be("Prazo removido.");
    }

    [Fact]
    public async Task Custom_AsksForTheEditor()
    {
        _runner.Result = Board(unscheduled: [Task("Comprar pão")]);
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);
        TaskRowViewModel? requested = null;
        viewModel.DeadlineEditorRequested += row => requested = row;

        viewModel.EditDeadline(viewModel.Sections.Single().Items.Single());

        requested.Should().NotBeNull();
    }
}

public class TaskDeadlineViewModelTests
{
    private static readonly TaskDeadline Friday = new(new DateOnly(2026, 10, 9), new TimeOnly(18, 0));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private static TaskDeadlineView View(string countdown = "3 dias restantes") =>
        new(Friday, DeadlineStatus.OnTrack, DeadlineSeverity.Normal, TimeSpan.FromDays(3), countdown, "sex 09/10 18:00", countdown);

    private static TaskRowViewModel Row(
        TaskDeadlineView? deadline = null,
        DeadlineAlertStage? alerts = null,
        bool isCompleted = false) =>
        new(
            new TodayTask(Guid.CreateVersion7(), Guid.CreateVersion7(), "Implementar módulo", TaskPriority.Normal,
                new DateOnly(2026, 10, 5), null, false,
                Deadline: deadline, DeadlineAlerts: alerts, NextAction: "Criar endpoint", Estimate: TimeSpan.FromHours(6)),
            isCompleted);

    private TaskDeadlineViewModel ViewModel(TaskRowViewModel row)
    {
        var viewModel = new TaskDeadlineViewModel(_runner, NullLogger<TaskDeadlineViewModel>.Instance);
        viewModel.Load(row);

        return viewModel;
    }

    [Fact]
    public void Loading_ShowsTheDeadlineThePlanAndTheAlerts()
    {
        var viewModel = ViewModel(Row(View(), DeadlineAlertStages.OnlyTheDayBefore));

        viewModel.HasDeadline.Should().BeTrue();
        viewModel.DateLabel.Should().Be("sex 09/10 18:00");
        viewModel.Countdown.Should().Be("3 dias restantes");
        viewModel.NextAction.Should().Be("Criar endpoint");
        viewModel.EstimateHours.Should().Be(6m);
        viewModel.IsDayBeforeAlerts.Should().BeTrue();
        viewModel.HasPlanChanges.Should().BeFalse();
        viewModel.CustomDate.Should().Be(new DateTime(2026, 10, 9));
        viewModel.CustomTime.Should().Be(new TimeSpan(18, 0, 0));
        _runner.Invoked.Should().BeEmpty("carregar não grava nada");
    }

    [Fact]
    public void WithoutADeadline_ItIsOneLine()
    {
        var row = new TaskRowViewModel(
            new TodayTask(Guid.CreateVersion7(), Guid.CreateVersion7(), "Comprar pão", TaskPriority.Normal, null, null, false),
            isCompleted: false);

        var viewModel = ViewModel(row);

        viewModel.HasDeadline.Should().BeFalse();
        viewModel.EditLabel.Should().Be("Definir prazo");
        viewModel.ShowsPlan.Should().BeFalse("tarefa curta não ganha formulário");
    }

    [Fact]
    public async Task AShortcut_SetsTheDeadline_AndTellsTheList()
    {
        var viewModel = ViewModel(Row());
        _runner.ResultsByHandler[typeof(SetDeadlineHandler)] = View("1 dia restante");
        var changed = 0;
        viewModel.Changed += () => changed++;
        viewModel.BeginEdit();

        await viewModel.UseShortcutCommand.ExecuteAsync("Tomorrow");

        viewModel.Countdown.Should().Be("1 dia restante");
        viewModel.IsEditing.Should().BeFalse();
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Custom_NeedsADayAndATime()
    {
        var viewModel = ViewModel(Row());
        _runner.ResultsByHandler[typeof(SetDeadlineHandler)] = View();

        viewModel.ApplyCustomCommand.CanExecute(null).Should().BeFalse("sem prazo, o personalizado abre vazio");

        viewModel.CustomDate = new DateTime(2026, 10, 12);
        viewModel.ApplyCustomCommand.CanExecute(null).Should().BeFalse();

        viewModel.CustomTime = new TimeSpan(10, 30, 0);
        viewModel.ApplyCustomCommand.CanExecute(null).Should().BeTrue();

        await viewModel.ApplyCustomCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Equal(typeof(SetDeadlineHandler));
        viewModel.HasDeadline.Should().BeTrue();
    }

    [Fact]
    public async Task Clearing_ForgetsTheDeadline()
    {
        var viewModel = ViewModel(Row(View()));

        await viewModel.ClearCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Equal(typeof(ClearDeadlineHandler));
        viewModel.HasDeadline.Should().BeFalse();
    }

    [Fact]
    public void ChoosingAnAlertMode_SavesItForTheTask()
    {
        var viewModel = ViewModel(Row(View()));

        viewModel.IsSilentAlerts = true;

        _runner.Invoked.Should().Equal(typeof(SetTaskDeadlineAlertsHandler));
        viewModel.IsSilentAlerts.Should().BeTrue();
        viewModel.IsDefaultAlerts.Should().BeFalse();
    }

    [Fact]
    public void Customizing_SavesEachToggle()
    {
        var viewModel = ViewModel(Row(View()));

        viewModel.IsCustomAlerts = true;
        viewModel.AlertSevenDays = true;

        viewModel.AlertOneDay.Should().BeTrue("personalizar parte do padrão");
        _runner.Invoked.Should().HaveCount(2).And.AllBeEquivalentTo(typeof(SetTaskDeadlineAlertsHandler));
    }

    [Fact]
    public async Task ThePlan_IsSavedOnlyWhenItChanged()
    {
        var viewModel = ViewModel(Row(View()));
        viewModel.SavePlanCommand.CanExecute(null).Should().BeFalse();

        viewModel.NextAction = "Escrever os testes";
        viewModel.SavePlanCommand.CanExecute(null).Should().BeTrue();

        await viewModel.SavePlanCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Equal(typeof(UpdateTaskPlanHandler));
        viewModel.HasPlanChanges.Should().BeFalse();
    }

    [Fact]
    public async Task ARefusal_ShowsTheDomainMessage()
    {
        var viewModel = ViewModel(Row());
        _runner.FailuresByHandler[typeof(SetDeadlineHandler)] = new DomainException("O prazo precisa ficar no futuro.");

        await viewModel.UseShortcutCommand.ExecuteAsync("Today");

        viewModel.ErrorMessage.Should().Be("O prazo precisa ficar no futuro.");
    }

    [Fact]
    public void ACompletedTask_IsReadOnly()
    {
        var viewModel = ViewModel(Row(View(), isCompleted: true));

        viewModel.IsEditable.Should().BeFalse();
        viewModel.BeginEdit();
        viewModel.IsEditing.Should().BeFalse();

        viewModel.IsSilentAlerts = true;
        _runner.Invoked.Should().BeEmpty();
    }
}

public class DeadlineAlertViewModelTests
{
    private readonly FakeUseCaseRunner _runner = new();

    private DeadlineAlertViewModel Shown(Guid? occurrenceId = null, DeadlineSeverity severity = DeadlineSeverity.Urgent)
    {
        var viewModel = new DeadlineAlertViewModel(_runner, NullLogger<DeadlineAlertViewModel>.Instance);
        viewModel.Show(new DeadlineAlert(
            occurrenceId ?? Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Implementar integração Jira",
            "Prazo urgente",
            "Vence em 1h 42min.",
            DeadlineAlertStage.TwoHours,
            severity,
            IsUrgent: true));

        return viewModel;
    }

    [Fact]
    public void Showing_TellsWhatIsDueAndWhen()
    {
        var viewModel = Shown();

        viewModel.Heading.Should().Be("Prazo urgente");
        viewModel.Title.Should().Be("Implementar integração Jira");
        viewModel.Message.Should().Be("Vence em 1h 42min.");
        viewModel.IsUrgent.Should().BeTrue();
        viewModel.IsOverdue.Should().BeFalse();
        viewModel.IsDigest.Should().BeFalse();
    }

    [Fact]
    public async Task OneMoreDay_ChangesTheDeadline_AndCloses()
    {
        var viewModel = Shown();
        _runner.ResultsByHandler[typeof(SetDeadlineHandler)] = new TaskDeadlineView(
            new TaskDeadline(new DateOnly(2026, 10, 10), new TimeOnly(18, 0)),
            DeadlineStatus.DueSoon, DeadlineSeverity.Attention, TimeSpan.FromHours(30), "x", "y", "z");
        var acted = false;
        viewModel.Acted += () => acted = true;

        await viewModel.ExtendCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Equal(typeof(SetDeadlineHandler));
        acted.Should().BeTrue();
    }

    [Fact]
    public async Task Snoozing_OnlySnoozesTheAlert()
    {
        var viewModel = Shown();

        await viewModel.SnoozeCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Equal(typeof(SnoozeDeadlineAlertHandler));
    }

    [Fact]
    public async Task AFailure_StaysOnTheCardWithAMessage()
    {
        var viewModel = Shown();
        _runner.NextFailure = new InvalidOperationException("SQLITE_BUSY");
        var acted = false;
        viewModel.Acted += () => acted = true;

        await viewModel.SnoozeCommand.ExecuteAsync(null);

        acted.Should().BeFalse();
        viewModel.ErrorMessage.Should().Be("Não foi possível adiar o aviso.");
    }

    [Fact]
    public void Open_AsksForTheTask()
    {
        var occurrenceId = Guid.CreateVersion7();
        var viewModel = Shown(occurrenceId);
        Guid? requested = null;
        viewModel.OpenRequested += id => requested = id;

        viewModel.OpenCommand.Execute(null);

        requested.Should().Be(occurrenceId);
    }

    [Fact]
    public void TheDigest_HasNoTaskToOpen()
    {
        var viewModel = Shown(Guid.Empty, DeadlineSeverity.Overdue);
        var opened = false;
        viewModel.OpenRequested += _ => opened = true;

        viewModel.OpenCommand.Execute(null);

        viewModel.IsDigest.Should().BeTrue();
        viewModel.IsOverdue.Should().BeTrue();
        opened.Should().BeFalse();
    }
}
