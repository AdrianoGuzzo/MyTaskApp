using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Application.Sync;

/// <summary>O que deu errado numa chamada ao servidor, do jeito que o motor precisa decidir.</summary>
public enum SyncFailureKind
{
    /// <summary>Rede, timeout, 5xx, 429: tenta de novo depois, com espera crescente.</summary>
    Transient,

    /// <summary>401 mesmo depois de renovar o token.</summary>
    Unauthorized,

    /// <summary>O refresh token não vale mais: precisa entrar de novo pelo navegador.</summary>
    NeedsReconnect,

    /// <summary>Este dispositivo foi revogado por outro.</summary>
    DeviceRevoked,

    /// <summary>O servidor não aceita mais esta versão do app.</summary>
    UpgradeRequired,

    /// <summary>O cursor está à frente do servidor (restauração): recomeça do zero.</summary>
    CursorAhead,

    NotFound,

    Forbidden,

    /// <summary>O pedido foi recusado como está (400): repetir igual não muda nada.</summary>
    Rejected,
}

/// <summary>Uma falha da sincronização com o tipo que decide o próximo passo.</summary>
public sealed class SyncApiException(SyncFailureKind kind, string message, string? code = null, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public SyncFailureKind Kind { get; } = kind;

    /// <summary>O código do contrato, quando o servidor mandou um.</summary>
    public string? Code { get; } = code;

    /// <summary>O <c>Retry-After</c> do 429 ou do 503.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>O cliente da API v1 (ADR-061). Cada chamada já leva o token; 401 renova e tenta uma vez.</summary>
public interface ISyncApi
{
    Task<MeResponse> MeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(CancellationToken cancellationToken);

    Task<DeviceInfo> HelloAsync(DeviceHello hello, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceInfo>> DevicesAsync(CancellationToken cancellationToken);

    Task<DeviceInfo> RenameDeviceAsync(Guid deviceId, string nickname, CancellationToken cancellationToken);

    Task<DeviceInfo> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<PushResponse> PushAsync(Guid workspaceId, PushRequest request, CancellationToken cancellationToken);

    Task<PullResponse> PullAsync(Guid workspaceId, string cursor, int limit, CancellationToken cancellationToken);

    Task<FetchResponse> FetchAsync(Guid workspaceId, FetchRequest request, CancellationToken cancellationToken);
}

/// <summary>Um login pelo navegador em andamento: abrir o endereço e esperar a volta.</summary>
public sealed record SyncAuthorization(Uri AuthorizeUrl, Task Completion);

/// <summary>
/// A conta da sincronização no desktop (ADR-061): o login pelo navegador com
/// PKCE, o access token só na memória e o refresh token no cofre do sistema.
/// </summary>
public interface ISyncAuthenticator
{
    /// <summary>A build tem um servidor configurado. Sem ele, a sincronização nem aparece como opção.</summary>
    bool IsAvailable { get; }

    Uri? ServerBaseUrl { get; }

    /// <summary>
    /// Abre a porta da volta e devolve o endereço para o navegador. A
    /// conclusão grava o refresh token; um clique novo desiste do anterior.
    /// </summary>
    Task<SyncAuthorization> BeginAuthorizationAsync(CancellationToken cancellationToken);

    /// <summary>Um access token válido, renovado se preciso. Sem refresh válido, <see cref="SyncFailureKind.NeedsReconnect"/>.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>O servidor recusou o access token: o próximo pedido renova.</summary>
    void ForgetAccessToken();

    Task<bool> HasCredentialsAsync(CancellationToken cancellationToken);

    /// <summary>Revoga o refresh token no servidor (se der) e apaga do cofre. Os dados locais ficam.</summary>
    Task SignOutAsync(CancellationToken cancellationToken);
}
