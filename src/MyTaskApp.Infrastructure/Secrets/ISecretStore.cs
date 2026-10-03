using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.Secrets;

/// <summary>
/// Onde ficam os segredos do app — hoje, o token do Jira (ADR-045). Nunca no
/// banco, nunca em JSON legível, nunca no log: só aqui, protegido pelo sistema.
/// </summary>
/// <remarks>
/// Porta da Infrastructure, e não da Application: quem guarda e lê segredo é
/// só a autenticação, e nenhum caso de uso precisa saber que ele existe.
/// </remarks>
internal interface ISecretStore
{
    /// <summary><c>null</c> = nada guardado, ou guardado por outro usuário/máquina e ilegível aqui.</summary>
    Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default);

    Task WriteAsync(string name, string value, CancellationToken cancellationToken = default);

    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fora do Windows ainda não há cofre. Recusar gravar é melhor do que gravar
/// token em texto puro: o usuário fica sem integração, e não com o segredo
/// exposto no disco.
/// </summary>
internal sealed class UnsupportedSecretStore : ISecretStore
{
    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task WriteAsync(string name, string value, CancellationToken cancellationToken = default) =>
        throw new DomainException("Guardar credenciais com segurança ainda não é suportado neste sistema.");

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
