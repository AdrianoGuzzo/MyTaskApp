namespace MyTaskApp.Application.External.Jira;

/// <summary>Em que pé está a conexão com o Jira.</summary>
public enum JiraConnectionState
{
    Disconnected,

    /// <summary>Autorizou, mas a conta enxerga mais de um site: falta escolher qual.</summary>
    ChoosingSite,

    Connected,
}

/// <summary>Como o app se autentica no Jira.</summary>
public enum JiraAuthMethod
{
    /// <summary>OAuth 2.0 (3LO) da Atlassian: o fluxo de um clique.</summary>
    OAuth,

    /// <summary>E-mail + API token: o caminho avançado, para quando o OAuth não serve.</summary>
    ApiToken,
}

/// <summary>Um site do Jira Cloud que a conta autorizada enxerga.</summary>
public sealed record JiraSite(string Id, string Name, string Url);

/// <summary>Um projeto, para o "Projeto padrão" da busca.</summary>
public sealed record JiraProject(string Key, string Name);

/// <summary>
/// O que a tela de Integrações mostra. Não carrega token nenhum: o segredo não
/// sai da Infrastructure (ADR-045).
/// </summary>
public sealed record JiraConnection(
    JiraConnectionState State,
    JiraAuthMethod? Method = null,
    string? SiteName = null,
    string? SiteUrl = null,
    string? AccountName = null,
    string? AccountEmail = null,
    string? DefaultProject = null,
    IReadOnlyList<JiraSite>? Sites = null)
{
    /// <summary>Esta build tem o app OAuth registrado? Sem ele, o caminho é o API token.</summary>
    public bool IsOAuthAvailable { get; init; }

    public bool IsConnected => State == JiraConnectionState.Connected;

    public static JiraConnection Disconnected(bool isOAuthAvailable) =>
        new(JiraConnectionState.Disconnected) { IsOAuthAvailable = isOAuthAvailable };
}

/// <summary>
/// Uma autorização em andamento: o endereço que o navegador abre e a espera
/// pela volta. A tela abre o navegador (é ela quem sabe abrir link) e aguarda
/// <see cref="Completion"/>; cancelar a espera desiste da autorização.
/// </summary>
public sealed record JiraAuthorization(Uri AuthorizeUrl, Task<JiraConnection> Completion);

/// <summary>
/// A autenticação com o Jira, isolada (ADR-045). Só esta porta conhece token,
/// e ela não o devolve para ninguém: o resto do app pergunta "estou conectado?"
/// e pede issues.
/// </summary>
/// <remarks>
/// Falha apresentável é <c>DomainException</c> (ADR-008), sem detalhe de HTTP:
/// "o e-mail ou o token não foram aceitos", "a autorização foi cancelada".
/// </remarks>
public interface IJiraAuthenticationService
{
    Task<JiraConnection> GetConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Começa o OAuth: prepara a volta no <c>localhost</c> e devolve o endereço
    /// de autorização. A conexão só é gravada quando o usuário autoriza.
    /// </summary>
    Task<JiraAuthorization> BeginAuthorizationAsync(CancellationToken cancellationToken = default);

    /// <summary>Com mais de um site autorizado, o escolhido pelo usuário.</summary>
    Task<JiraConnection> ChooseSiteAsync(string siteId, CancellationToken cancellationToken = default);

    /// <summary>O caminho avançado: site, e-mail e API token, conferidos antes de gravar.</summary>
    Task<JiraConnection> ConnectWithApiTokenAsync(
        string siteUrl,
        string email,
        string apiToken,
        CancellationToken cancellationToken = default);

    /// <summary>Pergunta ao Jira quem é o usuário. Recusa com a mensagem do motivo.</summary>
    Task<JiraConnection> TestAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JiraProject>> ListProjectsAsync(CancellationToken cancellationToken = default);

    Task<JiraConnection> SetDefaultProjectAsync(string? projectKey, CancellationToken cancellationToken = default);

    /// <summary>Apaga o segredo e a conexão. As tarefas vinculadas continuam como estão.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
