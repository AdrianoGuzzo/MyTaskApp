using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Criar a tarefa a partir da issue (ADR-045): digitar parte do título,
/// escolher a sugestão e ver a linha virar <c>GAECO-1234 título</c>, vinculada.
/// </summary>
public class TodayIssueCaptureTests
{
    private static readonly TodayBoard EmptyBoard = new(new DateOnly(2026, 10, 3), [], [], [], [], []);

    private static readonly ExternalTask Sync = new()
    {
        Id = "GAECO-1234",
        Title = "Corrigir erro de sincronização",
        IssueType = "Bug",
        Status = "Em andamento",
        Url = "https://empresa.atlassian.net/browse/GAECO-1234",
        Provider = "Jira",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new() { Result = EmptyBoard };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    public TodayIssueCaptureTests()
    {
        var jira = new OneIssueJira(Sync);
        var search = new ExternalTaskSearch([jira], [jira], _time, NullLogger<ExternalTaskSearch>.Instance);
        _runner.Handlers[typeof(SearchExternalTasksHandler)] = new SearchExternalTasksHandler(search);
    }

    private TodayViewModel ViewModel() =>
        new(_runner, new FakeConfirmationDialog(), new FakeClipboardWriter(), _time, NullLogger<TodayViewModel>.Instance);

    [Fact]
    public void ChoosingAnIssue_RewritesTheLineWithTheKeyAndTheTitle()
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = "comprar pão\ncorrigir erro de sinc";

        var caret = viewModel.LinkCaptureLine(1, Sync);

        viewModel.CaptureText.Should().Be("comprar pão\nGAECO-1234 Corrigir erro de sincronização");
        caret.Should().Be(viewModel.CaptureText.Length);
        viewModel.CaptureLinks.Should().Equal(Sync);
    }

    [Fact]
    public void AKeyTypedByHand_KeepsWhatCameAfterIt()
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = "GAECO-1234 olhar o retry";

        viewModel.LinkCaptureLine(0, Sync);

        viewModel.CaptureText.Should().Be("GAECO-1234 olhar o retry");
    }

    [Fact]
    public void TheLabel_ConfirmsTheLinkUnderTheBox()
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = "corrigir";

        viewModel.LinkCaptureLine(0, Sync);

        viewModel.CaptureLinkLabel.Should().Be("GAECO-1234 · BUG — será vinculada ao Jira");
    }

    [Fact]
    public void ErasingTheKey_UndoesTheLink()
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = "corrigir";
        viewModel.LinkCaptureLine(0, Sync);

        viewModel.CaptureText = "Corrigir erro de sincronização";

        viewModel.CaptureLinks.Should().BeEmpty();
        viewModel.CaptureLinkLabel.Should().BeNull();
    }

    [Fact]
    public async Task AfterCapturing_TheLinksAreForgotten()
    {
        var viewModel = ViewModel();
        viewModel.CaptureText = "corrigir";
        viewModel.LinkCaptureLine(0, Sync);

        await viewModel.CaptureAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(QuickCaptureHandler));
        viewModel.CaptureText.Should().BeEmpty();

        viewModel.CaptureText = "GAECO-1234 de novo";
        viewModel.CaptureLinks.Should().BeEmpty("o vínculo era da linha que já virou tarefa");
    }

    [Fact]
    public async Task TheSearch_IsOnlyOffered_WithJiraConnected()
    {
        var viewModel = ViewModel();
        _runner.Enqueue<GetJiraConnectionHandler>(JiraConnection.Disconnected(isOAuthAvailable: true));

        await viewModel.RefreshIssueSearchAsync(Ct);

        viewModel.CaptureSuggestions.IsAvailable.Should().BeFalse();

        _runner.Enqueue<GetJiraConnectionHandler>(new JiraConnection(JiraConnectionState.Connected));
        await viewModel.RefreshIssueSearchAsync(Ct);

        viewModel.CaptureSuggestions.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task NotKnowingWhetherJiraIsConnected_MeansNoSuggestions()
    {
        var viewModel = ViewModel();
        _runner.FailuresByHandler[typeof(GetJiraConnectionHandler)] = new IOException("jira.json travado");

        await viewModel.RefreshIssueSearchAsync(Ct);

        viewModel.CaptureSuggestions.IsAvailable.Should().BeFalse();
    }

    /// <summary>
    /// O caminho inteiro na janela de verdade: digitar, esperar a pausa, ↓,
    /// Enter escolhe, Enter captura. Teclado, foco e binding só quebram em
    /// runtime — é o tipo de fio que este teste guarda.
    /// </summary>
    [AvaloniaFact]
    public async Task TypingPartOfTheTitle_ChoosingWithTheKeyboard_AndCapturing()
    {
        var viewModel = ViewModel();
        viewModel.CaptureSuggestions.IsAvailable = true;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("corrigir erro");
        _time.Advance(IssueSuggestionsViewModel.Debounce);
        await viewModel.CaptureSuggestions.Pending;
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "IssueSuggestions")
            .IsEffectivelyVisible.Should().BeTrue();

        // Enter sem escolha não vincula: seria capturar.
        viewModel.CaptureSuggestions.Selected.Should().BeNull();

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        viewModel.CaptureText.Should().Be("GAECO-1234 Corrigir erro de sincronização");
        viewModel.CaptureSuggestions.IsOpen.Should().BeFalse();
        window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "CaptureLinkLabel")
            .IsEffectivelyVisible.Should().BeTrue();
        _runner.Invoked.Should().NotContain(typeof(QuickCaptureHandler), "o primeiro Enter só escolheu a issue");

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        _runner.Invoked.Should().Contain(typeof(QuickCaptureHandler));
    }

    [AvaloniaFact]
    public async Task Escape_ClosesTheList_AndEnterCapturesThePlainText()
    {
        var viewModel = ViewModel();
        viewModel.CaptureSuggestions.IsAvailable = true;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("corrigir erro");
        _time.Advance(IssueSuggestionsViewModel.Debounce);
        await viewModel.CaptureSuggestions.Pending;
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        viewModel.CaptureSuggestions.IsOpen.Should().BeFalse();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        _runner.Invoked.Should().Contain(typeof(QuickCaptureHandler));
        viewModel.CaptureLinks.Should().BeEmpty();
    }

    private sealed class OneIssueJira(ExternalTask issue) : IExternalTaskSearchProvider, IExternalTaskProvider
    {
        public string ProviderName => "Jira";

        public Task<IReadOnlyList<ExternalTask>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExternalTask>>(
                query.Split(' ').All(word => issue.Title.Contains(word, StringComparison.OrdinalIgnoreCase)) ? [issue] : []);

        public Task<ExternalTask?> GetTaskAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalTask?>(string.Equals(id, issue.Id, StringComparison.OrdinalIgnoreCase) ? issue : null);
    }
}
