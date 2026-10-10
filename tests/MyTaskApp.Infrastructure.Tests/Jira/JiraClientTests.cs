using System.Net;
using MyTaskApp.Application.External;
using MyTaskApp.Infrastructure.Jira;

namespace MyTaskApp.Infrastructure.Tests.Jira;

/// <summary>
/// A busca e a leitura de issues contra a Atlassian falsa (ADR-045), passando
/// pelo provedor que a Application enxerga.
/// </summary>
public sealed class JiraClientTests : IAsyncDisposable
{
    private const string SearchResponse = """
        {"issues":[
          {"id":"1","key":"GAECO-1234","fields":{"summary":"Corrigir erro de sincronização",
            "issuetype":{"name":"Bug"},"status":{"name":"Em andamento"},"project":{"key":"GAECO","name":"Gaeco"}}},
          {"id":"2","key":"GAECO-1267","fields":{"summary":"Corrigir erro no sincronismo",
            "issuetype":{"name":"História"},"status":{"name":"A fazer"},"project":{"key":"GAECO"}}}
        ],"isLast":true}
        """;

    private readonly JiraHarness _jira = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _jira.DisposeAsync();

    private JiraTaskSearchProvider Search() => new(_jira.Client);

    private JiraTaskProvider Reader() => new(_jira.Client);

    [Fact]
    public async Task Search_MapsTheIssues_WithTheBrowseLink()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);
        _jira.Server.On("/rest/api/3/search/jql", HttpStatusCode.OK, SearchResponse);

        var issues = await Search().SearchAsync("corrigir erro", Ct);

        issues.Should().HaveCount(2);
        var first = issues[0];
        first.Id.Should().Be("GAECO-1234");
        first.Title.Should().Be("Corrigir erro de sincronização");
        first.IssueType.Should().Be("Bug");
        first.Status.Should().Be("Em andamento");
        first.Project.Should().Be("GAECO");
        first.Provider.Should().Be("Jira");
        first.Url.Should().Be("https://empresa.atlassian.net/browse/GAECO-1234");
    }

    [Fact]
    public async Task Search_UsesTheNewJqlEndpoint_WithALimitAndOnlyTheNeededFields()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);
        _jira.Server.On("/rest/api/3/search/jql", HttpStatusCode.OK, SearchResponse);

        await Search().SearchAsync("corrigir erro", Ct);

        var request = _jira.Server.Requests.Last();
        request.Uri.AbsolutePath.Should().Be("/rest/api/3/search/jql");
        var query = Uri.UnescapeDataString(request.Uri.Query);
        query.Should().Contain("summary ~ \"corrigir erro*\"");
        query.Should().Contain($"maxResults={ExternalTaskSearch.MaxResults}");
        query.Should().Contain("fields=summary,issuetype,status,project");
    }

    [Fact]
    public async Task Search_ANumberWithTheDefaultProject_ReadsTheKey()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);
        await _jira.Auth.SetDefaultProjectAsync("GAECO", Ct);
        _jira.Server.On("/rest/api/3/issue/GAECO-1234", HttpStatusCode.OK,
            """{"key":"GAECO-1234","fields":{"summary":"Corrigir erro","issuetype":{"name":"Bug"}}}""");

        var issues = await Search().SearchAsync("1234", Ct);

        issues.Should().ContainSingle().Which.Id.Should().Be("GAECO-1234");
    }

    [Fact]
    public async Task Read_AnIssueByKey()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);
        _jira.Server.On("/rest/api/3/issue/GAECO-1234", HttpStatusCode.OK,
            """{"key":"GAECO-1234","fields":{"summary":"Corrigir erro","issuetype":{"name":"Bug"},"status":{"name":"Feito"}}}""");

        var issue = await Reader().GetTaskAsync("GAECO-1234", Ct);

        issue!.Title.Should().Be("Corrigir erro");
        issue.Status.Should().Be("Feito");
    }

    [Fact]
    public async Task Read_AnIssueThatDoesNotExist_IsNull()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);

        (await Reader().GetTaskAsync("GAECO-9999", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Offline_IsUnavailable()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);
        _jira.Server.Offline = true;

        var search = () => Search().SearchAsync("corrigir erro", Ct);

        (await search.Should().ThrowAsync<ExternalTaskUnavailableException>())
            .Which.Failure.Should().Be(ExternalTaskFailure.Unavailable);
    }

    [Fact]
    public async Task ASlowJira_TimesOutAsUnavailable()
    {
        await using var jira = new JiraHarness();
        jira.Options.RequestTimeoutSeconds = 2;
        await jira.ConnectWithApiTokenAsync(Ct);

        // O Jira só responde depois do teste: com um atraso fixo, um CI lento
        // atrasava o disparo do timeout e a resposta chegava antes dele.
        var gate = new TaskCompletionSource();
        jira.Server.On("/rest/api/3/search/jql", _ =>
        {
            gate.Task.Wait(TimeSpan.FromSeconds(30));
            return FakeJiraServer.Json(HttpStatusCode.OK, SearchResponse);
        });

        try
        {
            var search = () => new JiraTaskSearchProvider(jira.Client).SearchAsync("corrigir erro", Ct);

            (await search.Should().ThrowAsync<ExternalTaskUnavailableException>())
                .Which.Failure.Should().Be(ExternalTaskFailure.Unavailable);
        }
        finally
        {
            gate.SetResult();
        }
    }

    [Fact]
    public async Task CancellingTheSearch_IsNotAFailure()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);
        var gate = new TaskCompletionSource();
        _jira.Server.On("/rest/api/3/search/jql", _ =>
        {
            gate.Task.Wait(TimeSpan.FromSeconds(5));
            return FakeJiraServer.Json(HttpStatusCode.OK, SearchResponse);
        });

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = Search().SearchAsync("corrigir erro", cancellation.Token);
        await cancellation.CancelAsync();
        gate.SetResult();

        await pending.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ARejectedToken_IsReportedAsUnauthorized()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);
        _jira.Server.On("/rest/api/3/search/jql", HttpStatusCode.Unauthorized);

        var search = () => Search().SearchAsync("corrigir erro", Ct);

        (await search.Should().ThrowAsync<ExternalTaskUnavailableException>())
            .Which.Failure.Should().Be(ExternalTaskFailure.Unauthorized);
    }

    [Fact]
    public async Task NotConnected_IsReportedAsSuch()
    {
        var search = () => Search().SearchAsync("corrigir erro", Ct);

        (await search.Should().ThrowAsync<ExternalTaskUnavailableException>())
            .Which.Failure.Should().Be(ExternalTaskFailure.NotConnected);
        _jira.Server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TheLog_HasThePathButNotWhatTheUserTyped()
    {
        await _jira.ConnectWithApiTokenAsync(Ct);
        _jira.Server.On("/rest/api/3/search/jql", HttpStatusCode.OK, SearchResponse);

        await Search().SearchAsync("projeto secreto do cliente", Ct);

        _jira.Log.Mentions("/rest/api/3/search/jql").Should().BeTrue();
        _jira.Log.Mentions("secreto").Should().BeFalse();
    }
}
