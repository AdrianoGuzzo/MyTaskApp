namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// O que um ambiente admite (ADR-056). <see cref="Ceiling"/> é o máximo que uma
/// conexão dele pode ter; <see cref="Floor"/>, o que ela tem sempre. Quando os
/// dois coincidem num bit, aquele bit está travado — e a tela o mostra assim.
/// </summary>
public sealed record EnvironmentRules(
    DatabaseEnvironment Environment,
    ConnectionPermission Ceiling,
    ConnectionPermission Floor,
    ConnectionPermission Defaults,
    IReadOnlySet<DatabaseEnvironment> AllowedCopyDestinations,
    bool IsProtected,
    bool RequiresVerification,
    bool AllowsKeepingArtifact,
    bool RequiresTypedConfirmation,
    bool BlocksOnUncoveredHighCandidates)
{
    public ConnectionPermission Clamp(ConnectionPermission requested) => (requested & Ceiling) | Floor;

    /// <summary>O bit não muda, peça o que pedir: o teto e o piso dizem a mesma coisa sobre ele.</summary>
    public bool IsLocked(ConnectionPermission permission) =>
        (Ceiling & permission) == (Floor & permission);
}

/// <summary>
/// A política fixa de cada ambiente. Produção é exatamente o conjunto
/// obrigatório do ADR-056: lê, faz dump, serve de origem e exige anonimização
/// — e nada além disso, peça a tela o que pedir.
/// </summary>
public static class EnvironmentPolicy
{
    private const ConnectionPermission ProductionPermissions =
        ConnectionPermission.Read
        | ConnectionPermission.Dump
        | ConnectionPermission.UseAsSource
        | ConnectionPermission.RequireAnonymization;

    private static readonly IReadOnlySet<DatabaseEnvironment> NonProduction = new HashSet<DatabaseEnvironment>
    {
        DatabaseEnvironment.Development,
        DatabaseEnvironment.Test,
        DatabaseEnvironment.Staging,
    };

    private static readonly IReadOnlySet<DatabaseEnvironment> TestAndStaging = new HashSet<DatabaseEnvironment>
    {
        DatabaseEnvironment.Test,
        DatabaseEnvironment.Staging,
    };

    private static readonly EnvironmentRules Development = Writable(
        DatabaseEnvironment.Development,
        ConnectionPermission.All & ~ConnectionPermission.ExecuteSql & ~ConnectionPermission.RequireAnonymization);

    private static readonly EnvironmentRules Test = Writable(
        DatabaseEnvironment.Test,
        ConnectionPermission.All & ~ConnectionPermission.ExecuteSql & ~ConnectionPermission.RequireAnonymization);

    /// <summary>Homologação aceita tudo, mas não nasce podendo apagar o banco.</summary>
    private static readonly EnvironmentRules Staging = Writable(
        DatabaseEnvironment.Staging,
        ConnectionPermission.All & ~ConnectionPermission.ExecuteSql & ~ConnectionPermission.RequireAnonymization
            & ~ConnectionPermission.DropDatabase);

    private static readonly EnvironmentRules Production = new(
        DatabaseEnvironment.Production,
        Ceiling: ProductionPermissions,
        Floor: ProductionPermissions,
        Defaults: ProductionPermissions,
        AllowedCopyDestinations: NonProduction,
        IsProtected: true,
        RequiresVerification: false,
        AllowsKeepingArtifact: true,
        RequiresTypedConfirmation: false,
        BlocksOnUncoveredHighCandidates: false);

    private static readonly EnvironmentRules CriticalProduction = Production with
    {
        Environment = DatabaseEnvironment.CriticalProduction,
        AllowedCopyDestinations = TestAndStaging,
        RequiresVerification = true,
        AllowsKeepingArtifact = false,
        RequiresTypedConfirmation = true,
        BlocksOnUncoveredHighCandidates = true,
    };

    public static EnvironmentRules For(DatabaseEnvironment environment) => environment switch
    {
        DatabaseEnvironment.Development => Development,
        DatabaseEnvironment.Test => Test,
        DatabaseEnvironment.Staging => Staging,
        DatabaseEnvironment.Production => Production,
        DatabaseEnvironment.CriticalProduction => CriticalProduction,
        _ => throw new DomainException("Ambiente de banco desconhecido."),
    };

    /// <summary>Produção ou produção crítica: nunca destino, nunca alterado.</summary>
    public static bool IsProtected(DatabaseEnvironment environment) => For(environment).IsProtected;

    private static EnvironmentRules Writable(DatabaseEnvironment environment, ConnectionPermission defaults) => new(
        environment,
        Ceiling: ConnectionPermission.All,
        Floor: ConnectionPermission.None,
        Defaults: defaults,
        AllowedCopyDestinations: NonProduction,
        IsProtected: false,
        RequiresVerification: false,
        AllowsKeepingArtifact: true,
        RequiresTypedConfirmation: false,
        BlocksOnUncoveredHighCandidates: false);
}
