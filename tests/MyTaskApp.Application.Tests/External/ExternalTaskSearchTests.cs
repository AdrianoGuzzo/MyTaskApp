using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.External;
using MyTaskApp.Application.Tests.Fakes;

namespace MyTaskApp.Application.Tests.External;

public class ExternalTaskSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeExternalTasks _jira = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    private ExternalTaskSearch Search() =>
        new([_jira], [_jira], _time, NullLogger<ExternalTaskSearch>.Instance);

    public ExternalTaskSearchTests()
    {
        _jira.Issues.Add(FakeExternalTasks.Issue("GAECO-1234", "Corrigir erro de sincronização"));
        _jira.Issues.Add(FakeExternalTasks.Issue("GAECO-1267", "Corrigir erro no sincronismo"));
        _jira.Issues.Add(FakeExternalTasks.Issue("GAECO-1299", "Corrigir mensagens de erro", "Task"));
        _jira.Issues.Add(FakeExternalTasks.Issue("GAECO-1300", "Melhorar o cadastro", "Story"));
    }

    [Fact]
    public async Task ATitleFragment_FindsTheMatchingIssues()
    {
        var result = await Search().SearchAsync("corrigir erro", Ct);

        result.Failure.Should().BeNull();
        result.Items.Select(issue => issue.Id).Should().BeEquivalentTo(["GAECO-1234", "GAECO-1267", "GAECO-1299"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("co")]
    public async Task ATooShortQuery_DoesNotCallTheProvider(string query)
    {
        var result = await Search().SearchAsync(query, Ct);

        result.Items.Should().BeEmpty();
        _jira.Searches.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameSearch_IsServedFromTheCache()
    {
        var search = Search();

        await search.SearchAsync("corrigir erro", Ct);
        var second = await search.SearchAsync("  Corrigir   ERRO ", Ct);

        _jira.Searches.Should().ContainSingle();
        second.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task TheCacheExpires()
    {
        var search = Search();
        await search.SearchAsync("corrigir erro", Ct);

        _time.Advance(ExternalTaskSearch.SearchTimeToLive + TimeSpan.FromSeconds(1));
        await search.SearchAsync("corrigir erro", Ct);

        _jira.Searches.Should().HaveCount(2);
    }

    [Fact]
    public async Task TheCache_ForgetsEverythingWhenTheConnectionChanges()
    {
        var search = Search();
        await search.SearchAsync("corrigir erro", Ct);

        search.Invalidate();
        await search.SearchAsync("corrigir erro", Ct);

        _jira.Searches.Should().HaveCount(2);
    }

    [Fact]
    public async Task TheCache_IsBounded()
    {
        var search = Search();

        for (var i = 0; i <= ExternalTaskSearch.MaxCachedQueries; i++)
        {
            await search.SearchAsync($"consulta {i}", Ct);
        }

        // A primeira foi a mais antiga: saiu para dar lugar à última.
        await search.SearchAsync("consulta 0", Ct);

        _jira.Searches.Should().HaveCount(ExternalTaskSearch.MaxCachedQueries + 2);
    }

    [Fact]
    public async Task TheResults_AreLimited()
    {
        for (var i = 0; i < 20; i++)
        {
            _jira.Issues.Add(FakeExternalTasks.Issue($"ECO-{i + 1}", $"Revisar item {i}"));
        }

        var result = await Search().SearchAsync("revisar", Ct);

        result.Items.Should().HaveCount(ExternalTaskSearch.MaxResults);
    }

    [Fact]
    public async Task AnIssueKey_IsReadDirectly_WithoutTextSearch()
    {
        var result = await Search().SearchAsync(" gaeco-1234 ", Ct);

        result.Items.Should().ContainSingle().Which.Title.Should().Be("Corrigir erro de sincronização");
        _jira.Reads.Should().Equal("GAECO-1234");
        _jira.Searches.Should().BeEmpty();
    }

    [Fact]
    public async Task AKeyThatDoesNotExist_FallsBackToTextSearch()
    {
        var result = await Search().SearchAsync("GAECO-9999", Ct);

        _jira.Reads.Should().Equal("GAECO-9999");
        _jira.Searches.Should().Equal("GAECO-9999");
        result.Items.Should().BeEmpty();
        result.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Offline_ReportsUnavailable_WithoutThrowing()
    {
        _jira.Failure = ExternalTaskFailure.Unavailable;

        var result = await Search().SearchAsync("corrigir erro", Ct);

        result.Items.Should().BeEmpty();
        result.Failure.Should().Be(ExternalTaskFailure.Unavailable);
    }

    [Fact]
    public async Task AFailure_IsNotCached_SoTheNextKeystrokeTriesAgain()
    {
        var search = Search();
        _jira.Failure = ExternalTaskFailure.Unavailable;
        await search.SearchAsync("corrigir erro", Ct);

        _jira.Failure = null;
        var result = await search.SearchAsync("corrigir erro", Ct);

        result.Items.Should().HaveCount(3);
        _jira.Searches.Should().HaveCount(2);
    }

    [Fact]
    public async Task NotConnected_IsReportedAsSuch()
    {
        _jira.Failure = ExternalTaskFailure.NotConnected;

        var result = await Search().SearchAsync("corrigir erro", Ct);

        result.Failure.Should().Be(ExternalTaskFailure.NotConnected);
    }

    [Fact]
    public async Task WithoutAnyProvider_TheSearchIsEmptyAndNotConnected()
    {
        var search = new ExternalTaskSearch([], [], _time, NullLogger<ExternalTaskSearch>.Instance);

        var result = await search.SearchAsync("corrigir erro", Ct);

        result.Items.Should().BeEmpty();
        result.Failure.Should().Be(ExternalTaskFailure.NotConnected);
    }

    [Fact]
    public async Task Cancelling_StopsTheSearch()
    {
        _jira.Gate = new TaskCompletionSource();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var pending = Search().SearchAsync("corrigir erro", cancellation.Token);
        await cancellation.CancelAsync();

        await pending.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetAsync_AlwaysAsksTheProvider_BecauseRefreshMustBeFresh()
    {
        var search = Search();

        await search.GetAsync("Jira", "GAECO-1234", Ct);
        var issue = await search.GetAsync("Jira", "GAECO-1234", Ct);

        issue!.Id.Should().Be("GAECO-1234");
        _jira.Reads.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAsync_ForAnUnknownProvider_IsNotConnected()
    {
        var get = () => Search().GetAsync("Linear", "LIN-1", Ct);

        (await get.Should().ThrowAsync<ExternalTaskUnavailableException>())
            .Which.Failure.Should().Be(ExternalTaskFailure.NotConnected);
    }
}
