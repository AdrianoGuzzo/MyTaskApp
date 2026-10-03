using MyTaskApp.Domain.External;

namespace MyTaskApp.Application.External;

/// <summary>
/// Uma issue de um sistema de fora, como a busca e a leitura devolvem (ADR-045).
/// Só o que o MyTaskApp precisa para executar a tarefa — não é um espelho do Jira.
/// </summary>
public sealed class ExternalTask
{
    /// <summary>A chave que o usuário reconhece: <c>GAECO-1234</c>.</summary>
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Não é guardada: a descrição mora no Jira, a um clique.</summary>
    public string? Description { get; init; }

    public string? IssueType { get; init; }

    public string? Status { get; init; }

    /// <summary>O projeto, para a lista mostrar de onde a issue vem.</summary>
    public string? Project { get; init; }

    public string Url { get; init; } = string.Empty;

    /// <summary><c>"Jira"</c>.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>O retrato que a tarefa guarda, tirado agora.</summary>
    public ExternalLink ToLink(DateTimeOffset syncedAt) =>
        ExternalLink.Create(Provider, Id, Title, Url, IssueType, Status, syncedAt);

    /// <summary>O caminho de volta: o retrato guardado, visto como issue.</summary>
    public static ExternalTask From(ExternalLink link) =>
        new()
        {
            Id = link.Id,
            Title = link.Title,
            IssueType = link.IssueType,
            Status = link.Status,
            Url = link.Url,
            Provider = link.Provider,
        };
}
