using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Domain.Tests.DatabaseOperations;

/// <summary>Conexões prontas para os testes da política: cada uma num banco diferente.</summary>
internal static class TestConnections
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 18, 41, 2, TimeSpan.Zero);

    public static ConnectionPermissions Everything { get; } = ConnectionPermissions.FromFlags(ConnectionPermission.All);

    public static DatabaseConnection Connection(
        DatabaseEnvironment environment,
        string? name = null,
        string host = "db.local",
        string? database = null,
        string username = "app",
        ConnectionPermissions? permissions = null) =>
        DatabaseConnection.Create(
            name ?? environment.ToString(),
            host,
            5432,
            database ?? environment.ToString().ToLowerInvariant(),
            username,
            environment,
            DatabaseSslMode.Prefer,
            description: null,
            permissions ?? ConnectionPermissions.FromFlags(EnvironmentPolicy.For(environment).Defaults),
            Now);

    public static DatabaseConnectionSnapshot Snapshot(
        DatabaseEnvironment environment,
        string? name = null,
        string host = "db.local",
        string? database = null,
        string username = "app",
        ConnectionPermissions? permissions = null) =>
        Connection(environment, name, host, database, username, permissions).Snapshot();

    /// <summary>Um perfil com regras, já conferido contra as colunas da origem (ADR-058).</summary>
    public static AnonymizationFacts VerifiedAnonymization(int uncoveredHigh = 0) =>
        new AnonymizationFacts(ProfileExists: true, ProfileEnabled: true, RuleCount: 3)
            .WithSource([], uncoveredHigh);

    public static DatabaseOperationRequest CopyAndAnonymize(
        DatabaseConnectionSnapshot source,
        DatabaseConnectionSnapshot destination,
        AnonymizationFacts? facts = null,
        DatabaseCopyOptions? options = null) =>
        new(
            DatabaseOperationType.CopyAndAnonymize,
            source,
            destination,
            facts ?? VerifiedAnonymization(),
            options ?? DatabaseCopyOptions.Default,
            source.IsProtected ? [source.EndpointKey] : []);
}
