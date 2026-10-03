using MyTaskApp.Application.External;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>Um "Jira" em memória: conta as chamadas e falha quando pedido.</summary>
internal sealed class FakeExternalTasks : IExternalTaskProvider, IExternalTaskSearchProvider
{
    public string ProviderName => "Jira";

    public List<ExternalTask> Issues { get; } = [];

    public List<string> Searches { get; } = [];

    public List<string> Reads { get; } = [];

    /// <summary>Falha de toda chamada, até ser removida.</summary>
    public ExternalTaskFailure? Failure { get; set; }

    /// <summary>Segura a busca até o teste soltar — para cancelamento e corrida.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public static ExternalTask Issue(string id, string title, string type = "Bug", string? status = "A fazer") =>
        new()
        {
            Id = id,
            Title = title,
            IssueType = type,
            Status = status,
            Url = $"https://empresa.atlassian.net/browse/{id}",
            Provider = "Jira",
        };

    public async Task<IReadOnlyList<ExternalTask>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        Searches.Add(query);

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Failure is { } failure)
        {
            throw new ExternalTaskUnavailableException(failure);
        }

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return Issues
            .Where(issue => words.All(word => issue.Title.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public Task<ExternalTask?> GetTaskAsync(string id, CancellationToken cancellationToken = default)
    {
        Reads.Add(id);
        cancellationToken.ThrowIfCancellationRequested();

        if (Failure is { } failure)
        {
            throw new ExternalTaskUnavailableException(failure);
        }

        return Task.FromResult(Issues.Find(issue => string.Equals(issue.Id, id, StringComparison.OrdinalIgnoreCase)));
    }
}
