namespace MyTaskApp.Application.External;

/// <summary>
/// Lê uma issue de um sistema de fora pela chave (ADR-045). Hoje há um, o Jira;
/// Azure DevOps, GitHub Issues ou Linear entram como mais uma implementação,
/// sem mudar o domínio nem os casos de uso.
/// </summary>
/// <remarks>
/// Sem conexão, sem rede ou com o acesso recusado, lança
/// <see cref="ExternalTaskUnavailableException"/> — nunca o erro de HTTP, que
/// carregaria detalhe técnico até a tela. Chave que não existe é <c>null</c>.
/// </remarks>
public interface IExternalTaskProvider
{
    string ProviderName { get; }

    Task<ExternalTask?> GetTaskAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Procura issues por texto, para o autocomplete do título (ADR-045).</summary>
/// <remarks>Mesmo contrato de falha de <see cref="IExternalTaskProvider"/>.</remarks>
public interface IExternalTaskSearchProvider
{
    string ProviderName { get; }

    Task<IReadOnlyList<ExternalTask>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>Por que o sistema de fora não respondeu.</summary>
public enum ExternalTaskFailure
{
    /// <summary>O usuário não conectou o sistema. Não é erro: é a integração desligada.</summary>
    NotConnected,

    /// <summary>Sem rede, tempo esgotado ou o serviço fora do ar.</summary>
    Unavailable,

    /// <summary>A autorização expirou ou foi revogada: é preciso conectar de novo.</summary>
    Unauthorized,
}

/// <summary>
/// O sistema de fora não pôde responder. A mensagem é apresentável e não leva
/// endereço com token, cabeçalho nem corpo de resposta: quem precisa do detalhe
/// técnico é o log, e ele já foi escrito por quem lançou.
/// </summary>
public sealed class ExternalTaskUnavailableException(ExternalTaskFailure failure, string? message = null)
    : Exception(message ?? DefaultMessage(failure))
{
    public ExternalTaskFailure Failure { get; } = failure;

    private static string DefaultMessage(ExternalTaskFailure failure) => failure switch
    {
        ExternalTaskFailure.NotConnected => "O Jira não está conectado.",
        ExternalTaskFailure.Unauthorized => "A conexão com o Jira expirou. Conecte de novo em Integrações.",
        _ => "O Jira não respondeu agora.",
    };
}
