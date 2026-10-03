using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.External;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O autocomplete do Jira na captura (ADR-045): uma busca por pausa, a
/// resposta velha descartada, e o Enter que continua sendo "capturar".
/// </summary>
public class IssueSuggestionsViewModelTests
{
    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    private readonly CountingJira _jira = new();

    public IssueSuggestionsViewModelTests()
    {
        var search = new ExternalTaskSearch([_jira], [_jira], _time, NullLogger<ExternalTaskSearch>.Instance);
        _runner.Handlers[typeof(SearchExternalTasksHandler)] = new SearchExternalTasksHandler(search);

        _jira.Issues.Add(Issue("GAECO-1234", "Corrigir erro de sincronização", "Bug"));
        _jira.Issues.Add(Issue("GAECO-1267", "Corrigir erro no sincronismo", "Bug"));
        _jira.Issues.Add(Issue("GAECO-1299", "Corrigir mensagens de erro", "Tarefa"));
    }

    private IssueSuggestionsViewModel ViewModel(bool available = true) =>
        new(_runner, _time, NullLogger.Instance) { IsAvailable = available };

    private static ExternalTask Issue(string id, string title, string type) =>
        new() { Id = id, Title = title, IssueType = type, Url = $"https://x.atlassian.net/browse/{id}", Provider = "Jira" };

    private async Task TypedAsync(IssueSuggestionsViewModel viewModel, params string[] keystrokes)
    {
        foreach (var text in keystrokes)
        {
            viewModel.UpdateQuery(text);
        }

        _time.Advance(IssueSuggestionsViewModel.Debounce);
        await viewModel.Pending;
    }

    [Fact]
    public async Task TypingFast_SearchesOnce_AfterThePause()
    {
        var viewModel = ViewModel();

        await TypedAsync(viewModel, "c", "co", "cor", "corr", "corri", "corrig", "corrigir");

        _jira.Searches.Should().Equal("corrigir");
        viewModel.IsOpen.Should().BeTrue();
        viewModel.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task BeforeThePause_NothingIsAsked()
    {
        var viewModel = ViewModel();

        viewModel.UpdateQuery("corrigir erro");
        _time.Advance(IssueSuggestionsViewModel.Debounce - TimeSpan.FromMilliseconds(1));

        _jira.Searches.Should().BeEmpty();
        viewModel.IsOpen.Should().BeFalse();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await viewModel.Pending;

        _jira.Searches.Should().ContainSingle();
    }

    [Fact]
    public async Task ASearchOvertakenByAnother_IsDiscarded()
    {
        var viewModel = ViewModel();
        _jira.Gate = new TaskCompletionSource();

        // A: chega ao Jira e fica esperando a resposta.
        viewModel.UpdateQuery("corrigir mensagens");
        _time.Advance(IssueSuggestionsViewModel.Debounce);
        var first = viewModel.Pending;

        // B: o usuário continuou digitando.
        _jira.Gate = null;
        await TypedAsync(viewModel, "corrigir erro de sincronização");

        // A responde por último, e não pode tomar o lugar de B.
        _jira.Release();
        await first;

        viewModel.Items.Select(item => item.Key).Should().Equal("GAECO-1234");
    }

    [Fact]
    public async Task TheList_OpensWithNothingChosen_SoEnterStillCaptures()
    {
        var viewModel = ViewModel();

        await TypedAsync(viewModel, "corrigir erro");

        viewModel.Selected.Should().BeNull();
        viewModel.Accept(orFirst: false).Should().BeNull("Enter sem escolha é capturar, e não vincular");
    }

    [Fact]
    public async Task DownArrow_StartsAtTheFirst_AndEnterTakesIt()
    {
        var viewModel = ViewModel();
        await TypedAsync(viewModel, "corrigir erro");

        viewModel.Move(+1);
        viewModel.Move(+1);

        var second = viewModel.Items[1].Key;
        viewModel.Selected!.Key.Should().Be(second);

        var chosen = viewModel.Accept(orFirst: false);

        chosen!.Id.Should().Be(second);
        viewModel.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task UpArrow_WrapsToTheLast()
    {
        var viewModel = ViewModel();
        await TypedAsync(viewModel, "corrigir erro");

        viewModel.Move(-1);

        viewModel.Selected.Should().Be(viewModel.Items[^1]);
    }

    [Fact]
    public async Task Tab_TakesTheFirstWhenNothingWasChosen()
    {
        var viewModel = ViewModel();
        await TypedAsync(viewModel, "corrigir erro de sincronização");

        viewModel.Accept(orFirst: true)!.Id.Should().Be("GAECO-1234");
    }

    [Fact]
    public async Task TheExactKey_WithOneAnswer_ComesChosen()
    {
        var viewModel = ViewModel();

        await TypedAsync(viewModel, "gaeco-1234");

        viewModel.Selected!.Key.Should().Be("GAECO-1234");
        viewModel.Accept(orFirst: false)!.Title.Should().Be("Corrigir erro de sincronização");
    }

    [Fact]
    public async Task ALinkedLine_DoesNotSearchAgainWhileTheTitleIsEdited()
    {
        var viewModel = ViewModel();
        await TypedAsync(viewModel, "GAECO-1234");
        viewModel.Accept(orFirst: false);
        _jira.Searches.Clear();
        _jira.Reads.Clear();

        await TypedAsync(viewModel, "GAECO-1234 Corrigir erro de sincronização no login");

        _jira.Searches.Should().BeEmpty();
        _jira.Reads.Should().BeEmpty();
        viewModel.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Escape_Closes_AndTheSameTextDoesNotReopen()
    {
        var viewModel = ViewModel();
        await TypedAsync(viewModel, "corrigir erro");

        viewModel.Dismiss();
        await TypedAsync(viewModel, "corrigir erro");

        viewModel.IsOpen.Should().BeFalse();

        await TypedAsync(viewModel, "corrigir erro de");

        viewModel.IsOpen.Should().BeTrue("texto novo é pergunta nova");
    }

    [Fact]
    public async Task CtrlSpace_SearchesRightAway_EvenAfterEscape()
    {
        var viewModel = ViewModel();
        await TypedAsync(viewModel, "corrigir erro");
        viewModel.Dismiss();

        viewModel.SearchNow("corrigir erro");
        await viewModel.Pending;

        viewModel.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void CtrlSpace_WithoutJira_SaysWhereToConnect()
    {
        var viewModel = ViewModel(available: false);

        viewModel.SearchNow("corrigir");

        viewModel.Notice.Should().Contain("Integrações");
        _jira.Searches.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutJira_TypingAsksNothing()
    {
        var viewModel = ViewModel(available: false);

        await TypedAsync(viewModel, "corrigir erro");

        _jira.Searches.Should().BeEmpty();
        viewModel.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Offline_ShowsADiscreetLine_WithoutTechnicalDetail()
    {
        var viewModel = ViewModel();
        _jira.Failure = ExternalTaskFailure.Unavailable;

        await TypedAsync(viewModel, "corrigir erro");

        viewModel.Items.Should().BeEmpty();
        viewModel.Notice.Should().Be("Jira indisponível agora. A tarefa será criada sem vínculo.");
    }

    [Fact]
    public async Task ShortWords_AreNotWorthAskingFor()
    {
        var viewModel = ViewModel();

        await TypedAsync(viewModel, "co");

        _jira.Searches.Should().BeEmpty();
    }

    [Fact]
    public async Task NothingFound_ClosesQuietly()
    {
        var viewModel = ViewModel();

        await TypedAsync(viewModel, "comprar pão");

        viewModel.IsOpen.Should().BeFalse();
        viewModel.Notice.Should().BeNull();
    }

    [Fact]
    public async Task Reset_ForgetsTheLinkedKeys()
    {
        var viewModel = ViewModel();
        await TypedAsync(viewModel, "GAECO-1234");
        viewModel.Accept(orFirst: false);

        viewModel.Reset();
        await TypedAsync(viewModel, "GAECO-1234");

        viewModel.Items.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Bug", IssueKind.Bug)]
    [InlineData("História", IssueKind.Story)]
    [InlineData("Story", IssueKind.Story)]
    [InlineData("Tarefa", IssueKind.Task)]
    [InlineData("Spike", IssueKind.Other)]
    [InlineData(null, IssueKind.Other)]
    public void IssueTypes_AreRecognisedInBothLanguages(string? type, IssueKind kind)
    {
        IssueTypes.KindOf(type).Should().Be(kind);
    }

    /// <summary>Conta as idas ao "Jira" e segura a resposta quando o teste pede.</summary>
    private sealed class CountingJira : IExternalTaskSearchProvider, IExternalTaskProvider
    {
        private readonly List<TaskCompletionSource> _held = [];

        public string ProviderName => "Jira";

        public List<ExternalTask> Issues { get; } = [];

        public List<string> Searches { get; } = [];

        public List<string> Reads { get; } = [];

        public ExternalTaskFailure? Failure { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public void Release()
        {
            foreach (var held in _held)
            {
                held.TrySetResult();
            }
        }

        public async Task<IReadOnlyList<ExternalTask>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            Searches.Add(query);

            if (Gate is { } gate)
            {
                _held.Add(gate);
                await gate.Task;
            }

            if (Failure is { } failure)
            {
                throw new ExternalTaskUnavailableException(failure);
            }

            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return Issues.Where(issue => words.All(word => issue.Title.Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        public Task<ExternalTask?> GetTaskAsync(string id, CancellationToken cancellationToken = default)
        {
            Reads.Add(id);
            return Task.FromResult(Issues.Find(issue => string.Equals(issue.Id, id, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
