using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Development;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.GitHub;

/// <summary>
/// As PRs pelo GitHub CLI (ADR-047): o app usa o login que o <c>gh</c> já tem,
/// e não guarda token nenhum. Só leitura, com os argumentos separados como no
/// Git (ADR-027).
/// </summary>
/// <remarks>
/// <para>
/// Todo comando roda com <c>GH_PROMPT_DISABLED=1</c> — nada de perguntar num
/// terminal que não existe — e sem aviso de versão nova, cor ou spinner.
/// </para>
/// <para>
/// As respostas ficam guardadas por <see cref="CacheDuration"/>: a lista Hoje
/// recarrega a cada mudança, e cada recarga não pode virar uma rajada de idas
/// ao GitHub. "Verificar novamente" chama <see cref="Reset"/>. A falha fica
/// guardada por bem menos (<see cref="FailureCacheDuration"/>): a rede volta, e
/// a PR não pode continuar sumida até o cache vencer.
/// </para>
/// </remarks>
internal sealed class GhCliPullRequestClient(
    IProcessRunner runner,
    GhLocator locator,
    TimeProvider timeProvider,
    ILogger<GhCliPullRequestClient> logger) : IPullRequestClient
{
    /// <summary>A resposta normal vem em menos de um segundo; é só um aviso, e quem espera está digitando.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    internal static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    /// <summary>
    /// O bastante para a recarga seguinte da lista Hoje não abrir outra leva de
    /// <c>gh</c> presos, e pouco para a PR voltar logo depois da rede.
    /// </summary>
    internal static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(20);

    /// <summary>O código de saída do <c>gh</c> quando falta autenticação.</summary>
    internal const int AuthenticationRequiredExitCode = 4;

    internal static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
    {
        ["GH_PROMPT_DISABLED"] = "1",
        ["GH_NO_UPDATE_NOTIFIER"] = "1",
        ["GH_SPINNER_DISABLED"] = "1",
        ["NO_COLOR"] = "1",
    };

    private readonly ConcurrentDictionary<string, (PullRequestLookup Lookup, DateTimeOffset Expires)> _cache = new();

    public async Task<PullRequestSupport> CheckAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken = default)
    {
        var lookup = await CachedAsync($"check|{GitHubRepository.Host}", async () =>
        {
            var result = await RunAsync(["auth", "status", "--hostname", GitHubRepository.Host], cancellationToken);

            return result switch
            {
                null => new PullRequestLookup(PullRequestSupport.CliMissing),
                { TimedOut: true } => new PullRequestLookup(PullRequestSupport.Failed),
                { ExitCode: 0 } => new PullRequestLookup(PullRequestSupport.Ready),
                _ => new PullRequestLookup(PullRequestSupport.NotAuthenticated),
            };
        });

        return lookup.Support;
    }

    public Task<PullRequestLookup> FindOpenAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken = default) =>
        CachedAsync($"pr|{repository.Slug}|{branch}", async () =>
        {
            var result = await RunAsync(
                [
                    "pr", "list",
                    "--repo", repository.Slug,
                    "--head", branch,
                    "--state", "open",
                    "--json", "number,title,url,isDraft",
                    "--limit", "1",
                ],
                cancellationToken);

            return result switch
            {
                null => new PullRequestLookup(PullRequestSupport.CliMissing),
                { TimedOut: true } => new PullRequestLookup(PullRequestSupport.Failed),
                { ExitCode: 0 } => new PullRequestLookup(
                    PullRequestSupport.Ready,
                    GhOutputParser.ParseFirstPullRequest(result.StandardOutput)),
                { ExitCode: AuthenticationRequiredExitCode } => new PullRequestLookup(PullRequestSupport.NotAuthenticated),
                _ => new PullRequestLookup(PullRequestSupport.Failed),
            };
        });

    public void Reset() => _cache.Clear();

    /// <summary>
    /// "Não achei o <c>gh</c>" não fica guardado: procurar é barato, e quem
    /// acabou de instalar não pode esperar o cache vencer. A falha fica pouco.
    /// </summary>
    private async Task<PullRequestLookup> CachedAsync(string key, Func<Task<PullRequestLookup>> load)
    {
        var now = timeProvider.GetUtcNow();

        if (_cache.TryGetValue(key, out var cached) && cached.Expires > now)
        {
            return cached.Lookup;
        }

        var lookup = await load();

        TimeSpan? duration = lookup.Support switch
        {
            PullRequestSupport.CliMissing => null,
            PullRequestSupport.Failed => FailureCacheDuration,
            _ => CacheDuration,
        };

        if (duration is { } keep)
        {
            _cache[key] = (lookup, now + keep);
        }

        return lookup;
    }

    /// <summary>O que o <c>gh</c> respondeu, ou <c>null</c> quando ele não existe.</summary>
    private async Task<ProcessResult?> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (locator.Locate() is not { } executable)
        {
            return null;
        }

        var request = new ProcessRequest(executable, arguments, Timeout, Environment: Environment);

        try
        {
            var result = await runner.RunAsync(request, cancellationToken);

            if (result.ExitCode != 0 || result.TimedOut)
            {
                logger.LogInformation(
                    "GhCommandFailed {Command} {ExitCode} {TimedOut} {StandardError}",
                    request.Display,
                    result.ExitCode,
                    result.TimedOut,
                    result.StandardError.Trim());
            }

            return result;
        }
        catch (ProcessStartException exception)
        {
            logger.LogWarning(exception, "GhStartFailed {Executable}", executable);
            return null;
        }
    }
}
