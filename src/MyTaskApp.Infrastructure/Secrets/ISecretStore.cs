using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.Secrets;

/// <summary>
/// Onde ficam os segredos do app: o token do Jira (<c>jira</c>, ADR-045), o
/// refresh token da sincronização (<c>sync</c>, ADR-061), o token do servidor
/// MCP (<c>mcp-access-token</c>, ADR-059) e a senha de cada conexão PostgreSQL
/// (<c>postgres-…</c>, ADR-056). Nunca no banco, nunca em JSON legível, nunca
/// no log: só aqui, protegido pelo sistema.
/// </summary>
/// <remarks>
/// Porta da Infrastructure, e não da Application: quem guarda e lê segredo são
/// os adaptadores (as autenticações, as credenciais do PostgreSQL, o token do
/// MCP), e nenhum caso de uso precisa saber que ele existe.
/// Pública só para o app do celular entregar o cofre da plataforma (ADR-067);
/// as implementações do desktop continuam internas.
/// </remarks>
public interface ISecretStore
{
    /// <summary><c>null</c> = nada guardado, ou guardado por outro usuário/máquina e ilegível aqui.</summary>
    Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default);

    Task WriteAsync(string name, string value, CancellationToken cancellationToken = default);

    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// O sistema sem cofre suportado: nem o DPAPI do Windows
/// (<see cref="DpapiSecretStore"/>) nem o chaveiro do Linux
/// (<see cref="SecretToolSecretStore"/>, que sem o <c>secret-tool</c> se
/// comporta como este). Recusar gravar é melhor do que gravar token em texto
/// puro: o usuário fica sem integração, e não com o segredo exposto no disco.
/// </summary>
internal sealed class UnsupportedSecretStore : ISecretStore
{
    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task WriteAsync(string name, string value, CancellationToken cancellationToken = default) =>
        throw new DomainException("Guardar credenciais com segurança ainda não é suportado neste sistema.");

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
