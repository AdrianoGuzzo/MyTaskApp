using MyTaskApp.Application.Development;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>O GitHub em memória (ADR-047): a PR aberta por branch, e o estado do <c>gh</c>.</summary>
internal sealed class FakePullRequestClient : IPullRequestClient
{
    public PullRequestSupport Support { get; set; } = PullRequestSupport.Ready;

    /// <summary>A PR aberta de cada branch, por <c>"{dono}/{nome}|{branch}"</c>.</summary>
    public Dictionary<string, PullRequestInfo> Open { get; } = [];

    /// <summary>Faz a pergunta lançar, como um <c>gh</c> com defeito.</summary>
    public Exception? Failure { get; set; }

    public List<string> Calls { get; } = [];

    public int Resets { get; private set; }

    public Task<PullRequestSupport> CheckAsync(GitHubRepository repository, CancellationToken cancellationToken = default)
    {
        Calls.Add($"check {repository.Owner}/{repository.Name}");
        return Failure is null ? Task.FromResult(Support) : Task.FromException<PullRequestSupport>(Failure);
    }

    public Task<PullRequestLookup> FindOpenAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"pr {repository.Owner}/{repository.Name} {branch}");

        if (Failure is not null)
        {
            return Task.FromException<PullRequestLookup>(Failure);
        }

        return Task.FromResult(Support is PullRequestSupport.Ready
            ? new PullRequestLookup(Support, Open.GetValueOrDefault($"{repository.Owner}/{repository.Name}|{branch}"))
            : new PullRequestLookup(Support));
    }

    public void Reset() => Resets++;
}
