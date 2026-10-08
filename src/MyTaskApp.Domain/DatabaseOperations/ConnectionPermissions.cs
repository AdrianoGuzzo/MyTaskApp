namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// Cada permissão de uma conexão como um bit — é como ela vai para o banco, e
/// é o que deixa o teto e o piso do ambiente serem uma conta só (ADR-056).
/// Os valores vão para o banco: não reordene.
/// </summary>
[Flags]
public enum ConnectionPermission
{
    None = 0,

    Read = 1 << 0,

    Dump = 1 << 1,

    Restore = 1 << 2,

    Modify = 1 << 3,

    CreateDatabase = 1 << 4,

    DropDatabase = 1 << 5,

    ExecuteSql = 1 << 6,

    UseAsSource = 1 << 7,

    UseAsDestination = 1 << 8,

    /// <summary>Não é um poder, é uma obrigação: dados daqui só saem anonimizados.</summary>
    RequireAnonymization = 1 << 9,

    All = Read | Dump | Restore | Modify | CreateDatabase | DropDatabase | ExecuteSql
        | UseAsSource | UseAsDestination | RequireAnonymization,
}

/// <summary>As permissões de uma conexão, uma a uma, como a tela e a política as leem.</summary>
public readonly record struct ConnectionPermissions(
    bool CanRead,
    bool CanDump,
    bool CanRestore,
    bool CanModify,
    bool CanCreateDatabase,
    bool CanDropDatabase,
    bool CanExecuteSql,
    bool AllowAsSource,
    bool AllowAsDestination,
    bool RequireAnonymization)
{
    public static ConnectionPermissions FromFlags(ConnectionPermission flags) => new(
        flags.HasFlag(ConnectionPermission.Read),
        flags.HasFlag(ConnectionPermission.Dump),
        flags.HasFlag(ConnectionPermission.Restore),
        flags.HasFlag(ConnectionPermission.Modify),
        flags.HasFlag(ConnectionPermission.CreateDatabase),
        flags.HasFlag(ConnectionPermission.DropDatabase),
        flags.HasFlag(ConnectionPermission.ExecuteSql),
        flags.HasFlag(ConnectionPermission.UseAsSource),
        flags.HasFlag(ConnectionPermission.UseAsDestination),
        flags.HasFlag(ConnectionPermission.RequireAnonymization));

    public ConnectionPermission ToFlags() =>
        (CanRead ? ConnectionPermission.Read : 0)
        | (CanDump ? ConnectionPermission.Dump : 0)
        | (CanRestore ? ConnectionPermission.Restore : 0)
        | (CanModify ? ConnectionPermission.Modify : 0)
        | (CanCreateDatabase ? ConnectionPermission.CreateDatabase : 0)
        | (CanDropDatabase ? ConnectionPermission.DropDatabase : 0)
        | (CanExecuteSql ? ConnectionPermission.ExecuteSql : 0)
        | (AllowAsSource ? ConnectionPermission.UseAsSource : 0)
        | (AllowAsDestination ? ConnectionPermission.UseAsDestination : 0)
        | (RequireAnonymization ? ConnectionPermission.RequireAnonymization : 0);

    public bool Has(ConnectionPermission permission) => (ToFlags() & permission) == permission;

    /// <summary>Encaixa no ambiente: só fica o que o teto permite, e o piso entra sempre.</summary>
    public ConnectionPermissions ClampTo(EnvironmentRules rules) => FromFlags(rules.Clamp(ToFlags()));
}
