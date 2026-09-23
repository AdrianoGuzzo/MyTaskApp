namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// Se uma pasta existe agora. Interface para os testes não dependerem do disco,
/// e assíncrona porque um caminho de rede desconectado pode segurar a resposta
/// por segundos — tempo que a thread de UI não pode perder (ADR-026).
/// </summary>
/// <remarks>
/// Mora na Application desde o ADR-027: os casos de uso do worktree também
/// precisam perguntar ao disco antes de pedir algo ao Git.
/// </remarks>
public interface IDirectoryProbe
{
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Se há <b>qualquer coisa</b> no caminho — pasta ou arquivo.</summary>
    Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken = default);
}
