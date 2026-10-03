using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Domain.External;

namespace MyTaskApp.Application.External;

/// <summary>
/// O que a busca devolveu, ou por que não devolveu. Falha não é exceção aqui:
/// para o autocomplete, "Jira fora do ar" é só uma linha discreta embaixo da
/// caixa, e a captura segue sem vínculo.
/// </summary>
public sealed record ExternalTaskSearchResult(IReadOnlyList<ExternalTask> Items, ExternalTaskFailure? Failure = null)
{
    public static readonly ExternalTaskSearchResult Empty = new([]);
}

/// <summary>
/// A busca do autocomplete (ADR-045): limita, guarda em cache e transforma a
/// falha do sistema de fora em resposta. Singleton: o cache vale para o app
/// inteiro, e não para um escopo de caso de uso.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache por consulta normalizada.</b> "Corrigir  Erro" e "corrigir erro"
/// são a mesma pergunta. Dois minutos bastam para o usuário que apaga e
/// redigita não refazer a ida ao Jira, e são pouco para uma issue nova não
/// aparecer. A resposta de falha não entra: a próxima tecla tenta de novo.
/// </para>
/// <para>
/// <b>Chave primeiro.</b> <c>GAECO-1234</c> é lida direto — é a
/// correspondência clara que o usuário quis. Só se ela não existir a busca
/// cai para texto, porque <c>COVID-19</c> também tem forma de chave.
/// </para>
/// <para>
/// Quem debounça e cancela é a tela: aqui o cancelamento só é respeitado.
/// </para>
/// </remarks>
public sealed class ExternalTaskSearch(
    IEnumerable<IExternalTaskSearchProvider> searchers,
    IEnumerable<IExternalTaskProvider> readers,
    TimeProvider timeProvider,
    ILogger<ExternalTaskSearch> logger)
{
    public const int MinQueryLength = 3;

    public const int MaxResults = 8;

    public const int MaxCachedQueries = 50;

    public static readonly TimeSpan SearchTimeToLive = TimeSpan.FromMinutes(2);

    private readonly IReadOnlyList<IExternalTaskSearchProvider> _searchers = [.. searchers];

    private readonly IReadOnlyList<IExternalTaskProvider> _readers = [.. readers];

    private readonly Lock _lock = new();

    /// <summary>Consulta normalizada → resposta e quando expira. A ordem de inserção decide quem sai.</summary>
    private readonly Dictionary<string, (ExternalTaskSearchResult Result, DateTimeOffset Expires)> _cache = [];

    private readonly LinkedList<string> _order = [];

    public async Task<ExternalTaskSearchResult> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        var trimmed = Collapse(query);

        if (trimmed.Length < MinQueryLength)
        {
            return ExternalTaskSearchResult.Empty;
        }

        var cacheKey = trimmed.ToLower(CultureInfo.InvariantCulture);

        if (TryCached(cacheKey) is { } cached)
        {
            return cached;
        }

        if (_searchers.Count == 0)
        {
            return new ExternalTaskSearchResult([], ExternalTaskFailure.NotConnected);
        }

        var result = await LookUpAsync(trimmed, cancellationToken);

        if (result.Failure is null)
        {
            Remember(cacheKey, result);
        }

        return result;
    }

    /// <summary>
    /// A issue como está agora no sistema de fora, sem cache: é o "Atualizar do
    /// Jira", e devolver um retrato de dois minutos atrás seria mentir.
    /// </summary>
    public Task<ExternalTask?> GetAsync(string provider, string id, CancellationToken cancellationToken = default)
    {
        var reader = _readers.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderName, provider, StringComparison.OrdinalIgnoreCase));

        return reader is null
            ? throw new ExternalTaskUnavailableException(ExternalTaskFailure.NotConnected)
            : reader.GetTaskAsync(id, cancellationToken);
    }

    /// <summary>A conexão mudou: o que veio de outra conta não vale mais.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cache.Clear();
            _order.Clear();
        }
    }

    private async Task<ExternalTaskSearchResult> LookUpAsync(string query, CancellationToken cancellationToken)
    {
        var items = new List<ExternalTask>();
        ExternalTaskFailure? failure = null;

        if (IssueKey.TryParse(query, out var key))
        {
            foreach (var reader in _readers)
            {
                try
                {
                    if (await reader.GetTaskAsync(key.ToString(), cancellationToken) is { } issue)
                    {
                        return new ExternalTaskSearchResult([issue]);
                    }
                }
                catch (ExternalTaskUnavailableException exception)
                {
                    failure = Worse(failure, exception.Failure);
                }
            }
        }

        foreach (var searcher in _searchers)
        {
            try
            {
                items.AddRange(await searcher.SearchAsync(query, cancellationToken));
            }
            catch (ExternalTaskUnavailableException exception)
            {
                failure = Worse(failure, exception.Failure);
                logger.LogInformation("ExternalSearchUnavailable {Provider} {Failure}", searcher.ProviderName, exception.Failure);
            }
        }

        // Um sistema que respondeu vale mais que outro que falhou: mostra o que veio.
        return items.Count == 0 && failure is { } reason
            ? new ExternalTaskSearchResult([], reason)
            : new ExternalTaskSearchResult([.. items.Take(MaxResults)]);
    }

    /// <summary>Reconectar pesa mais que esperar a rede, que pesa mais que "desligado".</summary>
    private static ExternalTaskFailure Worse(ExternalTaskFailure? current, ExternalTaskFailure next) =>
        current is null || Severity(next) > Severity(current.Value) ? next : current.Value;

    private static int Severity(ExternalTaskFailure failure) => failure switch
    {
        ExternalTaskFailure.Unauthorized => 2,
        ExternalTaskFailure.Unavailable => 1,
        _ => 0,
    };

    private ExternalTaskSearchResult? TryCached(string key)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (entry.Expires > timeProvider.GetUtcNow())
            {
                return entry.Result;
            }

            _cache.Remove(key);
            _order.Remove(key);
            return null;
        }
    }

    private void Remember(string key, ExternalTaskSearchResult result)
    {
        lock (_lock)
        {
            if (_cache.Remove(key))
            {
                _order.Remove(key);
            }

            while (_cache.Count >= MaxCachedQueries && _order.First is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _order.RemoveFirst();
            }

            _cache[key] = (result, timeProvider.GetUtcNow() + SearchTimeToLive);
            _order.AddLast(key);
        }
    }

    /// <summary>Espaços das pontas fora, e os do meio colapsados num só.</summary>
    private static string Collapse(string? query) =>
        string.Join(' ', (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
