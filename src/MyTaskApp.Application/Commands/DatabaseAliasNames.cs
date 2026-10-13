using MyTaskApp.Application.DatabaseOperations;

namespace MyTaskApp.Application.Commands;

/// <summary>
/// Os apelidos de banco cadastrados, como nomes de variável (ADR-060):
/// <c>{lock_eco_core_1010}</c> vira o banco copiado daquele apelido.
/// </summary>
public static class DatabaseAliasNames
{
    /// <summary>Lidos agora: um apelido criado na outra janela vale no próximo comando.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(
        ISavedDatabaseRepository savedDatabases,
        CancellationToken cancellationToken) =>
        [.. (await savedDatabases.ListAsync(cancellationToken)).Select(saved => saved.Alias)];
}
