using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.External;

/// <summary>
/// O retrato da issue a que a tarefa está ligada num sistema de fora — hoje o
/// Jira (ADR-045). O sistema de fora continua sendo a fonte da verdade; isto é
/// só o que basta para a tarefa se desenhar <b>sem rede</b>: chave, título,
/// tipo, status e o endereço para abrir.
/// </summary>
/// <remarks>
/// <para>
/// Não replica a issue. Descrição, comentários, responsável e sprint ficam no
/// Jira, a um clique. O MyTaskApp guarda o que ajuda a lembrar o que a tarefa
/// é e a voltar ao ambiente certo.
/// </para>
/// <para>
/// Imutável, e trocado inteiro: um retrato novo é outro objeto. Quem atualiza
/// é a raiz (<see cref="TaskItem.RefreshExternal"/>), que decide o que acontece
/// com o título local.
/// </para>
/// </remarks>
public sealed class ExternalLink
{
    public const int MaxProviderLength = 40;

    public const int MaxIdLength = 100;

    /// <summary>O resumo do Jira vai até 255.</summary>
    public const int MaxTitleLength = 255;

    public const int MaxTypeLength = 100;

    public const int MaxUrlLength = 2000;

    private ExternalLink(
        string provider,
        string id,
        string title,
        string url,
        string? issueType,
        string? status,
        DateTimeOffset syncedAt)
    {
        Provider = provider;
        Id = id;
        Title = title;
        Url = url;
        IssueType = issueType;
        Status = status;
        SyncedAt = syncedAt;
    }

    /// <summary>De onde veio: <c>"Jira"</c>. Texto, e não enum, para o domínio não conhecer cada sistema.</summary>
    public string Provider { get; private set; }

    /// <summary>A chave que o usuário reconhece: <c>GAECO-1234</c>.</summary>
    public string Id { get; private set; }

    /// <summary>O título da issue quando foi lida pela última vez.</summary>
    public string Title { get; private set; }

    /// <summary>Onde a issue abre no navegador.</summary>
    public string Url { get; private set; }

    /// <summary>"Bug", "Story", "Task"… como o sistema de fora chama.</summary>
    public string? IssueType { get; private set; }

    public string? Status { get; private set; }

    /// <summary>Quando o retrato foi tirado. É o "atualizado há…" da tela.</summary>
    public DateTimeOffset SyncedAt { get; private set; }

    public static ExternalLink Create(
        string? provider,
        string? id,
        string? title,
        string? url,
        string? issueType,
        string? status,
        DateTimeOffset syncedAt)
    {
        var normalizedProvider = Required(provider, "Informe a origem do vínculo.");

        if (normalizedProvider.Length > MaxProviderLength)
        {
            throw new DomainException("A origem do vínculo é longa demais.");
        }

        var normalizedId = Required(id, "Informe a chave da issue.");

        if (normalizedId.Length > MaxIdLength)
        {
            throw new DomainException("A chave da issue é longa demais.");
        }

        return new ExternalLink(
            normalizedProvider,
            normalizedId,
            Cut(Required(title, "A issue precisa de um título."), MaxTitleLength),
            SafeUrl(url),
            Optional(issueType),
            Optional(status),
            syncedAt);
    }

    /// <summary>
    /// O título que a tarefa ganha ao nascer da issue. O resumo do Jira pode
    /// passar do limite do título da tarefa; cortar é melhor do que recusar o
    /// vínculo por algo que o usuário não escreveu.
    /// </summary>
    public static string TaskTitleFor(string issueTitle) =>
        Cut(issueTitle.Trim(), TaskItem.MaxTitleLength);

    public bool IsSameAs(string provider, string id) =>
        string.Equals(Provider, provider?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Id, id?.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool IsSameAs(ExternalLink other) => IsSameAs(other.Provider, other.Id);

    private static string Required(string? value, string message)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed)
            ? throw new DomainException(message)
            : trimmed;
    }

    private static string? Optional(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : Cut(trimmed, MaxTypeLength);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();

    /// <summary>
    /// Só <c>http</c> e <c>https</c> absolutos: o endereço vira um clique que o
    /// sistema abre, e um <c>file:</c> ou <c>javascript:</c> vindo de fora não
    /// pode virar execução local.
    /// </summary>
    private static string SafeUrl(string? url)
    {
        var trimmed = url?.Trim();

        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Length > MaxUrlLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new DomainException("O endereço da issue precisa ser um link http(s) completo.");
        }

        return trimmed;
    }
}
