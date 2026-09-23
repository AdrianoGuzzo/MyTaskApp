using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Abstractions;

internal static class DevelopmentCommandRepositoryExtensions
{
    /// <summary>Carrega o comando por id, ou falha com mensagem exibível.</summary>
    public static async Task<DevelopmentCommand> GetByIdAsync(
        this IDevelopmentCommandRepository commands,
        Guid commandId,
        CancellationToken cancellationToken) =>
        await commands.FindByIdAsync(commandId, cancellationToken)
        ?? throw new DomainException("Comando não encontrado.");

    /// <summary>Recusa um apelido que outro comando já usa.</summary>
    public static async Task EnsureAliasIsFreeAsync(
        this IDevelopmentCommandRepository commands,
        string alias,
        Guid? exceptId,
        CancellationToken cancellationToken)
    {
        if (await commands.AliasExistsAsync(alias, exceptId, cancellationToken))
        {
            throw new DomainException($"Já existe um comando chamado \"{alias}\".");
        }
    }
}
