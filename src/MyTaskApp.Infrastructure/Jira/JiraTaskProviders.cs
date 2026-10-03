using MyTaskApp.Application.External;

namespace MyTaskApp.Infrastructure.Jira;

/// <summary>O Jira como <see cref="IExternalTaskProvider"/>: uma issue pela chave (ADR-045).</summary>
internal sealed class JiraTaskProvider(IJiraClient client) : IExternalTaskProvider
{
    public const string Name = "Jira";

    public string ProviderName => Name;

    public async Task<ExternalTask?> GetTaskAsync(string id, CancellationToken cancellationToken = default) =>
        await client.GetIssueAsync(id.Trim(), cancellationToken) is { } issue ? ToTask(issue) : null;

    internal static ExternalTask ToTask(JiraIssue issue) =>
        new()
        {
            Id = issue.Key,
            Title = issue.Summary,
            IssueType = issue.Type,
            Status = issue.Status,
            Project = issue.Project,
            Url = issue.Url.ToString(),
            Provider = Name,
        };
}

/// <summary>O Jira como <see cref="IExternalTaskSearchProvider"/>: o autocomplete do título.</summary>
internal sealed class JiraTaskSearchProvider(IJiraClient client) : IExternalTaskSearchProvider
{
    public string ProviderName => JiraTaskProvider.Name;

    public async Task<IReadOnlyList<ExternalTask>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        [.. (await client.SearchAsync(query, ExternalTaskSearch.MaxResults, cancellationToken)).Select(JiraTaskProvider.ToTask)];
}
