using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.Theming;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>O ↺ do cabeçalho e o painel "Histórico · 7 dias" desenhados de verdade (ADR-053).</summary>
public class ActivityHistoryRenderingTests
{
    private const string Tip = "Ver histórico dos últimos 7 dias";

    private static readonly DateOnly Today = new(2026, 10, 6);

    private static readonly Guid Task = Guid.CreateVersion7();

    private static readonly Guid Occurrence = Guid.CreateVersion7();

    private static readonly ActivityItemView Done =
        new(Task, Occurrence, "Corrigir problema Redis", true, "Concluída 14:31 · 48min");

    private static readonly ActivityItemView Worked =
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Integração TGC", false, "Trabalhado · 45min");

    private static ActivityHistoryView History(params ActivityItemView[] today)
    {
        string[] labels =
        [
            "HOJE · TER 06/10", "ONTEM · SEG 05/10", "DOM · 04/10", "SÁB · 03/10",
            "SEX · 02/10", "QUI · 01/10", "QUA · 30/09",
        ];

        var days = labels
            .Select((label, index) => new ActivityDayView(
                Today.AddDays(-index),
                label,
                index == 0 ? today : []))
            .ToList();

        return new ActivityHistoryView(
            days,
            today.Count(item => item.IsCompleted),
            TimeSpan.FromMinutes(93),
            today.Length == 0 ? "Nenhuma atividade registrada" : "1 concluída · 1h 33min");
    }

    private static async Task<(MainWindow Window, TodayViewModel ViewModel, FakeUseCaseRunner Runner)> ShowAsync(
        ActivityHistoryView history)
    {
        var runner = new FakeUseCaseRunner { Result = new TodayBoard(Today, [], [], [], [], []) };
        runner.ResultsByHandler[typeof(GetActivityHistoryHandler)] = history;

        var viewModel = new TodayViewModel(
            runner,
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        return (window, viewModel, runner);
    }

    private static Button HistoryButton(MainWindow window) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => Equals(ToolTip.GetTip(button), Tip) && button.IsEffectivelyVisible);

    private static async Task<(Flyout Flyout, ActivityHistoryPanel View)> OpenAsync(MainWindow window)
    {
        var button = HistoryButton(window);
        var flyout = (Flyout)button.Flyout!;

        flyout.ShowAt(button);
        await System.Threading.Tasks.Task.Yield();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return (flyout, (ActivityHistoryPanel)flyout.Content!);
    }

    private static IReadOnlyList<string> Texts(Control root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => block.Text!)
            .ToList();

    [AvaloniaFact]
    public async Task TheHistoryButton_IsInTheNormalHeader_AndInTheHud()
    {
        var (window, _, _) = await ShowAsync(History());

        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(button => Equals(ToolTip.GetTip(button), Tip))
            .ToList();

        buttons.Should().HaveCount(2).And.OnlyContain(button =>
            button.Classes.Contains("chrome") && button.Flyout is Flyout);
        HistoryButton(window).FindAncestorOfType<Border>()!.Classes.Should().Contain("titleBar");

        window.Chrome.EnterHud();
        window.Chrome.HudSizeOptions.Single(option => option.Label == "Normal").SelectCommand.Execute(null);
        window.UpdateLayout();

        HistoryButton(window).FindAncestorOfType<Border>()!.Classes.Should().Contain("hudBar");
    }

    [AvaloniaFact]
    public async Task TheCompactHud_LeavesTheHistoryOut_SoTheCounterStillFits()
    {
        var (window, _, _) = await ShowAsync(History());

        window.Chrome.EnterHud();
        window.Chrome.IsHudCompact.Should().BeTrue();
        window.UpdateLayout();

        window.GetVisualDescendants().OfType<Button>()
            .Where(button => Equals(ToolTip.GetTip(button), Tip))
            .Should().OnlyContain(button => !button.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Opening_LoadsTheHistory_AndShowsSevenDays()
    {
        var (window, _, runner) = await ShowAsync(History(Done, Worked));

        var (flyout, view) = await OpenAsync(window);

        flyout.IsOpen.Should().BeTrue();
        runner.Invoked.Should().Contain(typeof(GetActivityHistoryHandler));

        var texts = Texts(view);
        texts.Should().ContainInOrder(
            "Histórico · 7 dias",
            "HOJE · TER 06/10",
            "Corrigir problema Redis",
            "Concluída 14:31 · 48min",
            "Integração TGC",
            "Trabalhado · 45min",
            "ONTEM · SEG 05/10",
            "QUA · 30/09");
        texts.Should().Contain("1 concluída · 1h 33min");
        texts.Count(text => text == "Nenhuma atividade").Should().Be(6);

        flyout.Hide();
    }

    [AvaloniaFact]
    public async Task ACompletedLine_HasTheCheck_AndAWorkedLine_HasTheCircle()
    {
        var (window, _, _) = await ShowAsync(History(Done, Worked));

        var (flyout, view) = await OpenAsync(window);

        var lines = view.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("historyItem"))
            .ToList();

        lines.Should().HaveCount(2);
        lines[0].GetVisualDescendants().OfType<TextBlock>()
            .Should().Contain(block => block.Text == "✓" && block.IsEffectivelyVisible);
        lines[1].GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()
            .Should().ContainSingle(ellipse => ellipse.IsEffectivelyVisible);
        lines[1].GetVisualDescendants().OfType<TextBlock>()
            .Should().NotContain(block => block.Text == "✓" && block.IsEffectivelyVisible);

        flyout.Hide();
    }

    [AvaloniaFact]
    public async Task NothingDone_ShowsTheEmptyState_WithoutLosingTheDays()
    {
        var (window, _, _) = await ShowAsync(History());

        var (flyout, view) = await OpenAsync(window);

        var texts = Texts(view);
        texts.Should().Contain("Nenhuma atividade registrada");
        texts.Count(text => text == "Nenhuma atividade").Should().Be(7);
        texts.Should().Contain("DOM · 04/10");

        flyout.Hide();
    }

    [AvaloniaFact]
    public async Task TheCloseButton_ClosesThePanel()
    {
        var (window, _, _) = await ShowAsync(History(Done));
        var (flyout, view) = await OpenAsync(window);

        view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CloseButton")
            .Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        flyout.IsOpen.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Escape_ClosesThePanel_WithTheFocusStillInTheWindow()
    {
        var (window, _, _) = await ShowAsync(History(Done));
        var (flyout, _) = await OpenAsync(window);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        flyout.IsOpen.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Escape_ClosesThePanel_WithTheFocusInsideIt()
    {
        var (window, _, _) = await ShowAsync(History(Done));
        var (flyout, view) = await OpenAsync(window);

        view.Focus().Should().BeTrue();
        TopLevel.GetTopLevel(view)!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        flyout.IsOpen.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Escape_WithTheHistoryClosed_IsLeftToTheWindow()
    {
        var (window, _, _) = await ShowAsync(History(Done));
        var (flyout, _) = await OpenAsync(window);
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        window.RaiseEvent(args);

        args.Handled.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task ClickingALine_OpensThatOccurrence_AndClosesThePanel()
    {
        var (window, viewModel, runner) = await ShowAsync(History(Done, Worked));
        var task = new TodayTask(Occurrence, Task, Done.Title, TaskPriority.Normal, Today.AddDays(-3), null, false);
        runner.ResultsByHandler[typeof(GetTodayBoardHandler)] = new BoardTask(task, IsCompleted: true);

        TaskRowViewModel? opened = null;
        viewModel.History.OpenRequested += row => opened = row;

        var (flyout, view) = await OpenAsync(window);

        var line = view.GetVisualDescendants().OfType<Button>()
            .First(button => button.Classes.Contains("historyItem"));
        line.CommandParameter.Should().Be(Done);

        line.Command!.Execute(line.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        opened.Should().NotBeNull();
        opened!.OccurrenceId.Should().Be(Occurrence);
        opened.TaskId.Should().Be(Task);
        opened.IsCompleted.Should().BeTrue();
        flyout.IsOpen.Should().BeFalse();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task EveryTheme_PaintsThePanelWithItsOwnTokens(string themeId)
    {
        var theme = ThemeCatalog.Find(themeId)!;
        var (window, _, _) = await ShowAsync(History(Done, Worked));
        var (flyout, view) = await OpenAsync(window);

        var palette = ThemeResources.Build(theme);
        view.Resources.MergedDictionaries.Add(palette);
        view.UpdateLayout();

        var summary = view.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "SummaryText");
        var title = view.GetVisualDescendants().OfType<TextBlock>().First(block => block.Classes.Contains("historyTitle"));

        ((ISolidColorBrush)summary.Foreground!).Color.Should().Be(((ISolidColorBrush)palette["WidgetTextMidBrush"]!).Color);
        ((ISolidColorBrush)title.Foreground!).Color.Should().Be(((ISolidColorBrush)palette["WidgetTextHighBrush"]!).Color);

        flyout.Hide();
    }

    public static TheoryData<string> Themes()
    {
        var data = new TheoryData<string>();

        foreach (var theme in ThemeCatalog.All)
        {
            data.Add(theme.Id);
        }

        return data;
    }
}
