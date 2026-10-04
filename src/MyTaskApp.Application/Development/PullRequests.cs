using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Planning;

namespace MyTaskApp.Application.Development;

/// <summary>Uma PR aberta da branch, como o GitHub respondeu (ADR-047).</summary>
public sealed record PullRequestInfo(int Number, string Title, Uri Url, bool IsDraft)
{
    /// <summary>"PR #123 aberta" — ou "em rascunho", que ainda não pede revisão.</summary>
    public string Label => IsDraft ? $"PR #{Number} em rascunho" : $"PR #{Number} aberta";
}

/// <summary>Se dá para perguntar ao GitHub, e por que não.</summary>
public enum PullRequestSupport
{
    /// <summary>O <c>gh</c> está instalado e logado no github.com.</summary>
    Ready,

    /// <summary>Sem remoto <c>origin</c>, ou ele não é do github.com: não há o que perguntar.</summary>
    NotGitHub,

    /// <summary>O <c>gh</c> não foi encontrado.</summary>
    CliMissing,

    /// <summary>O <c>gh</c> existe, mas não está logado no github.com.</summary>
    NotAuthenticated,

    /// <summary>O <c>gh</c> respondeu com erro ou não respondeu a tempo.</summary>
    Failed,
}

/// <summary>A resposta: o suporte e, quando houver, a PR aberta.</summary>
public sealed record PullRequestLookup(PullRequestSupport Support, PullRequestInfo? PullRequest = null)
{
    /// <summary>Repositório do GitHub em que o <c>gh</c> falta ou não está logado: hora do tutorial.</summary>
    public bool NeedsCli => Support is PullRequestSupport.CliMissing or PullRequestSupport.NotAuthenticated;
}

/// <summary>
/// Quem pergunta ao GitHub pelas PRs (ADR-047). Só leitura; nunca lança por
/// causa do GitHub — a falha vem como <see cref="PullRequestSupport"/>.
/// </summary>
public interface IPullRequestClient
{
    /// <summary>O <c>gh</c> está pronto para perguntar a este repositório?</summary>
    Task<PullRequestSupport> CheckAsync(GitHubRepository repository, CancellationToken cancellationToken = default);

    /// <summary>A PR aberta cuja branch de origem é <paramref name="branch"/>, se houver.</summary>
    Task<PullRequestLookup> FindOpenAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken = default);

    /// <summary>Esquece as respostas guardadas: "Verificar novamente" quer a de agora.</summary>
    void Reset();
}

/// <summary>
/// A PR aberta de uma branch do repositório. Sem <see cref="Branch"/>, só
/// confere se dá para perguntar — o formulário mostra o tutorial do <c>gh</c>
/// antes de o usuário escolher a branch.
/// </summary>
public sealed record FindPullRequest(string RepositoryPath, string? Branch, bool Refresh = false);

/// <summary>
/// Lê o remoto <c>origin</c>, e só pergunta ao GitHub quando ele é do
/// github.com. Nunca lança: é um indicador, e um repositório com problema
/// não pode atrapalhar a criação do worktree.
/// </summary>
public sealed class FindPullRequestHandler(
    IGitClient git,
    IPullRequestClient pullRequests,
    ILogger<FindPullRequestHandler> logger)
{
    private const string DefaultRemote = "origin";

    public async Task<PullRequestLookup> HandleAsync(
        FindPullRequest query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (query.Refresh)
            {
                pullRequests.Reset();
            }

            var url = await git.GetRemoteUrlAsync(query.RepositoryPath, DefaultRemote, cancellationToken);

            if (GitHubRepository.TryParse(url) is not { } repository)
            {
                return new PullRequestLookup(PullRequestSupport.NotGitHub);
            }

            var branch = query.Branch?.Trim();

            if (string.IsNullOrEmpty(branch))
            {
                return new PullRequestLookup(await pullRequests.CheckAsync(repository, cancellationToken));
            }

            return await pullRequests.FindOpenAsync(repository, branch, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "PullRequestLookupFailed {Repository} {Branch}", query.RepositoryPath, query.Branch);
            return new PullRequestLookup(PullRequestSupport.Failed);
        }
    }
}

/// <summary>As PRs abertas dos worktrees da lista Hoje.</summary>
public sealed record FindWorktreePullRequests(IReadOnlyList<TaskWorktree> Worktrees);

/// <summary>
/// Uma pergunta por worktree, poucas por vez. Só volta quem tem PR aberta; o
/// resto — sem GitHub, sem <c>gh</c>, sem PR — não tem o que mostrar no balão.
/// </summary>
public sealed class FindWorktreePullRequestsHandler(FindPullRequestHandler single)
{
    /// <summary>Cada pergunta é uma ida à rede; mais que isso em paralelo só enfileira no GitHub.</summary>
    private const int MaxParallel = 4;

    public async Task<IReadOnlyDictionary<Guid, PullRequestInfo>> HandleAsync(
        FindWorktreePullRequests query,
        CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(MaxParallel, MaxParallel);

        var lookups = query.Worktrees.Select(async worktree =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                // O worktree conhece o remoto tanto quanto o repositório principal.
                var lookup = await single.HandleAsync(
                    new FindPullRequest(worktree.WorktreePath, worktree.Branch),
                    cancellationToken);

                return (worktree.DevelopmentId, lookup.PullRequest);
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(lookups);

        return results
            .Where(result => result.PullRequest is not null)
            .ToDictionary(result => result.DevelopmentId, result => result.PullRequest!);
    }
}
