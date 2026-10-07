using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Desktop.Widget;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O cronômetro desenhado (ADR-052). Binding de XAML falha em runtime: um ⏹ sem
/// comando, ou um relógio amarrado no lugar errado, passaria pela build e só
/// apareceria na mesa de quem usa.
/// </summary>
public class TimeTrackingRenderingTests
{
    private static readonly DateOnly Date = new(2026, 10, 6);
    private static readonly DateTimeOffset Started = new(2026, 10, 6, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Started + new TimeSpan(0, 37, 42);

    private static TodayTask Task(string title, TimeSpan logged = default, DateTimeOffset? timerStartedAt = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false,
            Estimate: TimeSpan.FromHours(6), Logged: logged, TimerStartedAt: timerStartedAt);

    private static ActiveTimerView Running(TodayTask task) =>
        new(Guid.CreateVersion7(), task.OccurrenceId, task.TaskId, task.Title, Started);

    private static async Task<MainWindow> ShowListAsync(TodayBoard board, bool hud = false)
    {
        var time = new FakeTimeProvider(Now);
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = board },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            time,
            NullLogger<TodayViewModel>.Instance,
            activeTimer: new ActiveTimerViewModel(time));

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Attach(new WidgetHudTests.RecordingStore(WidgetState.Default with { X = 500, Y = 300 }), new WidgetHudTests.RecordingBehavior());
        window.Show();

        if (hud)
        {
            window.Chrome.EnterHud();
        }

        for (var pass = 0; pass < 2; pass++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return window;
    }

    private static IEnumerable<T> All<T>(Visual root) => root.GetVisualDescendants().OfType<T>();

    private static Border Strip(Visual window) => All<Border>(window).Single(border => border.Name == "ActiveTimerStrip");

    private static IReadOnlyList<string> Texts(Visual root) =>
        All<TextBlock>(root)
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => block.Text!)
            .ToList();

    [AvaloniaFact]
    public async Task TheRunningRow_ShowsStopAndTheClock_AndTheButtonHasItsCommand()
    {
        var running = Task("Implementar autenticação", timerStartedAt: Started);
        var window = await ShowListAsync(new TodayBoard(Date, [], [], [], [running, Task("Comprar pão")], []) { ActiveTimer = Running(running) });

        var buttons = All<Button>(window).Where(button => button.Name == "TimerButton").ToList();
        var runningButton = buttons.Single(button => button.Classes.Contains("on"));

        var timeLines = All<StackPanel>(window).Where(panel => panel.Name == "TimeLine").ToList();

        buttons.Should().HaveCount(2).And.AllSatisfy(button => button.Command.Should().NotBeNull());
        runningButton.IsEffectivelyVisible.Should().BeTrue();
        runningButton.Opacity.Should().Be(1, "o cronômetro que corre nunca se esconde");
        var runningLine = timeLines.Single(line => line.IsVisible);
        runningLine.Classes.Should().Contain("running");
        Texts(runningLine).Should().Equal("00:37:42");
        All<Border>(window).Count(border => border.Classes.Contains("row") && border.Classes.Contains("timing")).Should().Be(1);
        Strip(window).IsVisible.Should().BeFalse("a linha está à vista, a faixa seria repetição");
    }

    [AvaloniaFact]
    public async Task ARowWithLoggedTime_ShowsTheTotalUnderTheTitle()
    {
        var window = await ShowListAsync(
            new TodayBoard(Date, [], [], [], [Task("Autenticação", TimeSpan.FromMinutes(272)), Task("Comprar pão")], []));

        var button = All<Button>(window).First(candidate => candidate.Name == "TimerButton");
        var lines = All<StackPanel>(window).Where(panel => panel.Name == "TimeLine").ToList();

        lines.Count(line => line.IsVisible).Should().Be(1, "sem tempo lançado, a linha de tempo não aparece");
        Texts(lines.Single(line => line.IsVisible)).Should().Equal("4h 32min");
        ToolTip.GetTip(button).Should().Be("Iniciar o cronômetro · 4h 32min registrados");
    }

    [AvaloniaFact]
    public async Task ATimerOfATaskOffTheBoard_ShowsTheStrip_WithTheTitleTheClockAndAStop()
    {
        var elsewhere = Task("Planejar sprint");
        var window = await ShowListAsync(new TodayBoard(Date, [], [], [], [Task("Comprar pão")], []) { ActiveTimer = Running(elsewhere) });

        var strip = Strip(window);

        strip.IsEffectivelyVisible.Should().BeTrue();
        Texts(strip).Should().Contain(["Planejar sprint", "00:37:42"]);
        All<Button>(strip).Single().Command.Should().NotBeNull();
    }

    [AvaloniaFact]
    public async Task InTheHud_TheStripIsAlwaysThere_WhileATimerRuns()
    {
        var running = Task("Implementar autenticação", timerStartedAt: Started);
        var window = await ShowListAsync(
            new TodayBoard(Date, [], [], [], [running], []) { ActiveTimer = Running(running) },
            hud: true);

        window.Chrome.IsHud.Should().BeTrue();
        Strip(window).IsEffectivelyVisible.Should().BeTrue();
        Texts(Strip(window)).Should().Contain("Implementar autenticação");
        All<Button>(window).Single(button => button.Name == "TimerButton").IsEffectivelyVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task InTheHud_WithNothingRunning_ThereIsNoStrip_AndNoTimerButtonUntilHover()
    {
        var window = await ShowListAsync(new TodayBoard(Date, [], [], [], [Task("Comprar pão")], []), hud: true);

        Strip(window).IsEffectivelyVisible.Should().BeFalse();
        All<Button>(window).Single(button => button.Name == "TimerButton").IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task TheTimeTab_DrawsTheHistory_WithEveryCommandResolved()
    {
        var time = new FakeTimeProvider(Now);
        var runner = new FakeUseCaseRunner();
        var row = Task("Implementar autenticação", TimeSpan.FromMinutes(90));
        var start = new DateTimeOffset(2026, 10, 6, 14, 0, 0, TimeSpan.FromHours(-3));
        runner.ResultsByHandler[typeof(GetTaskTimeLogHandler)] = new TaskTimeLogView(
            row.OccurrenceId, row.TaskId, TimeSpan.FromMinutes(90), null, TimeSpan.FromHours(6),
            [
                new TimeEntryDayView(Date, "Hoje", TimeSpan.FromMinutes(90),
                [
                    new TimeEntryView(Guid.CreateVersion7(), start, start.AddMinutes(90), TimeEntrySource.Manual,
                        "Corrigi problema na API", Date, new TimeOnly(14, 0), Date, new TimeOnly(15, 30),
                        "14:00 → 15:30", "1h 30min", "Manual"),
                ]),
            ]);
        var clock = new UserClock(time, Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }), NullLogger<UserClock>.Instance);
        var tab = new TaskTimeLogViewModel(
            runner, new FakeTimeEntryEditor(), new FakeConfirmationDialog(), new ActiveTimerViewModel(time), clock,
            NullLogger<TaskTimeLogViewModel>.Instance);
        var notes = new TaskNotesViewModel(
            runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(new FakeUseCaseRunner()),
            NullLogger<TaskNotesViewModel>.Instance,
            time: tab);

        notes.Load(new TaskRowViewModel(row, isCompleted: false));
        var window = new TaskNotesWindow(notes, new FakeConfirmationDialog());
        window.Show();
        notes.SelectedTabIndex = TaskNotesViewModel.TimeTab;
        await tab.ActivateAsync(CancellationToken.None);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var section = All<StackPanel>(window).Single(panel => panel.Name == "TimeSection");
        var buttons = All<Button>(section).Where(button => button.IsVisible).ToList();

        All<TabItem>(window).Single(tab => tab.Name == "TimeTabItem").IsVisible.Should().BeTrue();
        Texts(section).Should().Contain(
            ["Tempo trabalhado", "Registrado 1h 30min", "1h 30min / 6h · 25%", "Hoje", "14:00 → 15:30", "Manual", "Corrigi problema na API"]);
        buttons.Select(button => button.Name).Should().Contain(["TimeToggleButton", "AddTimeButton"]);
        buttons.Count(button => button.Classes.Contains("timeAction")).Should().Be(2);
        buttons.Should().AllSatisfy(button => button.Command.Should().NotBeNull());
    }

    [AvaloniaFact]
    public void TheDialog_OpensWithTheFields_AndTheAcceptLabel()
    {
        var viewModel = new TimeEntryEditorViewModel(new TimeEntryEditorRequest(
            "Adicionar tempo",
            "Adicionar",
            new TimeEntryDraft(Date, new TimeOnly(14, 0), Date, new TimeOnly(15, 30), null),
            (_, _) => System.Threading.Tasks.Task.FromResult<string?>(null)));

        var window = new TimeEntryWindow(viewModel);
        window.Show();
        window.UpdateLayout();

        window.Title.Should().Be("Adicionar tempo");
        All<CalendarDatePicker>(window).Single().SelectedDate.Should().Be(new DateTime(2026, 10, 6));
        All<TimePicker>(window).Select(picker => picker.SelectedTime)
            .Should().Equal(new TimeSpan(14, 0, 0), new TimeSpan(15, 30, 0));
        All<TextBox>(window).Should().Contain(box => box.Name == "NoteBox");
        Texts(window).Should().Contain(["Data", "Início", "Fim", "Observação", "1h 30min"]);
        All<Button>(window).Single(button => button.Name == "AcceptButton").Content.Should().Be("Adicionar");
        All<CheckBox>(window).Single(box => box.Name == "EndsLaterBox").IsVisible.Should().BeFalse();
    }
}
