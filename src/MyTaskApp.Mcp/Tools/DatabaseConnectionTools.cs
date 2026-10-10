using System.ComponentModel;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tools;

/// <summary>
/// Perfis de conexão PostgreSQL (ADR-056, ADR-057, ADR-059). Um perfil de
/// conexão diz <b>como chegar</b> a um servidor; um perfil de anonimização diz
/// <b>como os dados saem</b> dele. A senha nunca entra nem sai por aqui: a
/// resposta diz só se há uma guardada, e ela se define na tela de Bancos.
/// </summary>
[McpServerToolType]
public sealed class DatabaseConnectionTools(McpGateway gateway)
{
    [McpServerTool(Name = "database_profile_list", Title = "Listar conexões de banco", ReadOnly = true, Idempotent = true)]
    [Description("As conexões cadastradas, sem senha: servidor, porta, banco (vazio = só o servidor), usuário, ambiente, SSL, permissões e se há senha guardada.")]
    public Task<IReadOnlyList<ConnectionInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "database_profile_list",
            async (runner, token) => (IReadOnlyList<ConnectionInfo>)(await Connections(runner, token)).Select(ConnectionInfo.Of).ToList(),
            cancellationToken);

    [McpServerTool(Name = "database_profile_get", Title = "Consultar conexão de banco", ReadOnly = true, Idempotent = true)]
    [Description("Uma conexão pelo id, sem senha, com o que a política do ambiente dela permite e quem a usa.")]
    public Task<ConnectionDetail> GetAsync(
        [Description("O id da conexão.")] string connectionId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "database_profile_get",
            (runner, token) => Detail(runner, McpInput.Id(connectionId, "o id da conexão"), token),
            cancellationToken);

    [McpServerTool(Name = "database_profile_get_schema", Title = "Campos da conexão de banco", ReadOnly = true, Idempotent = true)]
    [Description("Os campos aceitos ao criar ou editar uma conexão, os ambientes com a política de cada um, os modos de SSL e as permissões.")]
    public Task<ConnectionSchema> GetSchemaAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync("database_profile_get_schema", (_, _) => Task.FromResult(ConnectionSchema.Current), cancellationToken);

    [McpServerTool(Name = "database_profile_get_providers", Title = "Provedores de banco", ReadOnly = true, Idempotent = true)]
    [Description("Os provedores de banco suportados. Hoje, só PostgreSQL.")]
    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "database_profile_get_providers",
            (_, _) => Task.FromResult<IReadOnlyList<ProviderInfo>>(
            [
                new ProviderInfo(
                    "postgresql",
                    "PostgreSQL",
                    "Leitura por Npgsql em sessão somente leitura; cópia por pg_dump/pg_restore com as máscaras no SELECT (ADR-058)."),
            ]),
            cancellationToken);

    [McpServerTool(Name = "database_profile_test_connection", Title = "Testar conexão de banco", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Conecta ao servidor com a senha guardada, numa sessão somente leitura, e diz se conectou, a versão, o usuário, " +
        "os schemas, quantas tabelas e os privilégios. Não altera nada no banco.")]
    public Task<ServerDiagnostics> TestAsync(
        [Description("O id da conexão.")] string connectionId,
        [Description("O banco a testar, quando a conexão é só o servidor.")] string? database = null,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "database_profile_test_connection",
            async (runner, token) =>
            {
                var row = await Find(runner, McpInput.Id(connectionId, "o id da conexão"), token);

                return await runner.RunAsync<TestDatabaseConnectionHandler, ServerDiagnostics>(
                    (handler, cancel) => handler.HandleAsync(
                        new TestDatabaseConnection(
                            row.Id, row.Name, row.Host, row.Port,
                            string.IsNullOrWhiteSpace(database) ? row.Database : database.Trim(),
                            row.Username, row.Environment, row.SslMode,
                            Password: null),
                        cancel),
                    token);
            },
            cancellationToken);

    [McpServerTool(Name = "database_profile_list_databases", Title = "Bancos do servidor", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Os bancos do servidor de uma conexão — para escolher o banco de uma conexão que é só o servidor. Só leitura.")]
    public Task<IReadOnlyList<string>> ListDatabasesAsync(
        [Description("O id da conexão.")] string connectionId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "database_profile_list_databases",
            (runner, token) => runner.RunAsync<ListServerDatabasesHandler, IReadOnlyList<string>>(
                (handler, cancel) => handler.HandleAsync(new ListServerDatabases(McpInput.Id(connectionId, "o id da conexão")), cancel),
                token),
            cancellationToken);

    [McpServerTool(Name = "database_profile_get_usage", Title = "Uso da conexão", ReadOnly = true, Idempotent = true)]
    [Description("Os perfis de anonimização, apelidos e perfis de cópia que usam a conexão. Usada, ela não pode ser excluída.")]
    public Task<ConnectionUsage> GetUsageAsync(
        [Description("O id da conexão.")] string connectionId,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "database_profile_get_usage",
            (runner, token) => Usage(runner, McpInput.Id(connectionId, "o id da conexão"), token),
            cancellationToken);

    [McpServerTool(Name = "database_profile_create", Title = "Criar conexão de banco", Idempotent = false, Destructive = false)]
    [Description(
        "Cadastra uma conexão PostgreSQL, sem senha (defina a senha na tela de Bancos). As permissões são encaixadas na " +
        "política do ambiente: Production e CriticalProduction ficam sempre só leitura, dump e origem com anonimização. " +
        "Não conecta nem altera nada no servidor.")]
    public Task<ConnectionDetail> CreateAsync(
        [Description("Nome único (até 80 caracteres).")] string name,
        [Description("Servidor (host ou IP).")] string host,
        [Description("Usuário do banco.")] string username,
        [Description("Development, Test, Staging, Production ou CriticalProduction.")] string environment,
        [Description("Porta (padrão 5432).")] int port = 5432,
        [Description("Banco fixo; vazio = só o servidor, e o banco é escolhido na cópia.")] string? database = null,
        [Description("Prefer (padrão), Require, VerifyFull ou Disable.")] string? sslMode = null,
        [Description("Descrição (até 500 caracteres).")] string? description = null,
        [Description("Permissões pedidas; vazio = o padrão do ambiente. Veja database_profile_get_schema.")] string[]? permissions = null,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "database_profile_create",
            DataArea.Databases,
            async (runner, token) =>
            {
                var env = McpInput.Enum<DatabaseEnvironment>(environment, "o ambiente");

                var id = await runner.RunAsync<SaveDatabaseConnectionHandler, Guid>(
                    (handler, cancel) => handler.HandleAsync(
                        new SaveDatabaseConnection(
                            null, name, host, port, NullIfBlank(database), username, env,
                            McpInput.OptionalEnum<DatabaseSslMode>(sslMode, "o modo SSL") ?? DatabaseSslMode.Prefer,
                            description,
                            Permissions(permissions) ?? ConnectionPermissions.FromFlags(EnvironmentPolicy.For(env).Defaults),
                            Password: null),
                        cancel),
                    token);

                return await Detail(runner, id, token);
            },
            cancellationToken);

    [McpServerTool(Name = "database_profile_update", Title = "Editar conexão de banco", Idempotent = true)]
    [Description(
        "Altera só os campos informados de uma conexão. A senha guardada continua a mesma — ela não se troca por aqui. " +
        "Com senha guardada, servidor, porta, usuário e SSL não mudam pelo MCP (a senha seguiria junto); o ambiente só " +
        "fica igual ou mais restrito. Não conecta, não migra, não apaga nem restaura nada no servidor.")]
    public Task<ConnectionDetail> UpdateAsync(
        [Description("O id da conexão.")] string connectionId,
        [Description("Novo nome.")] string? name = null,
        [Description("Novo servidor.")] string? host = null,
        [Description("Nova porta.")] int? port = null,
        [Description("Novo banco fixo.")] string? database = null,
        [Description("Deixa a conexão só com o servidor (sem banco fixo).")] bool clearDatabase = false,
        [Description("Novo usuário.")] string? username = null,
        [Description("Novo ambiente.")] string? environment = null,
        [Description("Novo modo SSL.")] string? sslMode = null,
        [Description("Nova descrição.")] string? description = null,
        [Description("Apaga a descrição.")] bool clearDescription = false,
        [Description("Novas permissões pedidas (o conjunto inteiro).")] string[]? permissions = null,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "database_profile_update",
            DataArea.Databases,
            async (runner, token) =>
            {
                var row = await Find(runner, McpInput.Id(connectionId, "o id da conexão"), token);
                var newEnvironment = McpInput.OptionalEnum<DatabaseEnvironment>(environment, "o ambiente") ?? row.Environment;
                var newSsl = McpInput.OptionalEnum<DatabaseSslMode>(sslMode, "o modo SSL") ?? row.SslMode;

                EnsureSafeChange(row, host, port, username, newSsl, newEnvironment);

                await runner.RunAsync<SaveDatabaseConnectionHandler, Guid>(
                    (handler, cancel) => handler.HandleAsync(
                        new SaveDatabaseConnection(
                            row.Id,
                            name ?? row.Name,
                            host ?? row.Host,
                            port ?? row.Port,
                            clearDatabase ? null : NullIfBlank(database) ?? row.Database,
                            username ?? row.Username,
                            newEnvironment,
                            newSsl,
                            clearDescription ? null : description ?? row.Description,
                            Permissions(permissions) ?? row.Permissions,
                            // Nulo é "manter a guardada": nunca apaga a senha por acidente.
                            Password: null),
                        cancel),
                    token);

                return await Detail(runner, row.Id, token);
            },
            cancellationToken);

    [McpServerTool(Name = "database_profile_duplicate", Title = "Duplicar conexão de banco", Idempotent = false, Destructive = false)]
    [Description("Copia uma conexão com outro nome — sem a senha, que não sai do cofre. Defina a senha da cópia na tela de Bancos.")]
    public Task<ConnectionDetail> DuplicateAsync(
        [Description("O id da conexão de origem.")] string connectionId,
        [Description("O nome da cópia.")] string name,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "database_profile_duplicate",
            DataArea.Databases,
            async (runner, token) =>
            {
                var id = await runner.RunAsync<DuplicateDatabaseConnectionHandler, Guid>(
                    (handler, cancel) => handler.HandleAsync(
                        new DuplicateDatabaseConnection(McpInput.Id(connectionId, "o id da conexão"), name), cancel),
                    token);

                return await Detail(runner, id, token);
            },
            cancellationToken);

    [McpServerTool(Name = "database_profile_delete", Title = "Excluir conexão de banco", Destructive = true, Idempotent = false)]
    [Description(
        "Exclui a conexão e a senha guardada dela. Recusa se algum perfil de anonimização, apelido ou perfil de cópia a usa " +
        "(veja database_profile_get_usage). Não toca no servidor.")]
    public Task<string> DeleteAsync(
        [Description("O id da conexão.")] string connectionId,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "database_profile_delete",
            DataArea.Databases,
            async (runner, token) =>
            {
                var row = await Find(runner, McpInput.Id(connectionId, "o id da conexão"), token);

                await runner.RunAsync<DeleteDatabaseConnectionHandler>(
                    (handler, cancel) => handler.HandleAsync(new DeleteDatabaseConnection(row.Id), cancel), token);

                return $"Conexão {row.Name} excluída.";
            },
            cancellationToken);

    [McpServerTool(Name = "database_profile_set_enabled", Title = "Habilitar conexão de banco", Idempotent = true, Destructive = false)]
    [Description("Habilita ou desabilita uma conexão. Desabilitada, ela não é inspecionada nem usada em cópia.")]
    public Task<ConnectionDetail> SetEnabledAsync(
        [Description("O id da conexão.")] string connectionId,
        [Description("true = habilitada.")] bool enabled,
        CancellationToken cancellationToken = default) =>
        gateway.WriteAsync(
            "database_profile_set_enabled",
            DataArea.Databases,
            async (runner, token) =>
            {
                var id = McpInput.Id(connectionId, "o id da conexão");

                await runner.RunAsync<SetDatabaseConnectionEnabledHandler>(
                    (handler, cancel) => handler.HandleAsync(new SetDatabaseConnectionEnabled(id, enabled), cancel), token);

                return await Detail(runner, id, token);
            },
            cancellationToken);

    [McpServerTool(Name = "audit_database_operations", Title = "Histórico de operações de banco", ReadOnly = true, Idempotent = true)]
    [Description("As últimas operações de banco (testes, cópias, verificações), com status, origem, destino e duração. Sem segredos.")]
    public Task<IReadOnlyList<DatabaseOperationRow>> GetOperationHistoryAsync(
        [Description("Quantas, de 1 a 500.")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        gateway.ReadAsync(
            "audit_database_operations",
            (runner, token) => limit is < 1 or > 500
                ? throw new DomainException("O limite fica entre 1 e 500.")
                : runner.RunAsync<GetDatabaseOperationHistoryHandler, IReadOnlyList<DatabaseOperationRow>>(
                    (handler, cancel) => handler.HandleAsync(new GetDatabaseOperationHistory(limit), cancel), token),
            cancellationToken);

    internal static Task<IReadOnlyList<DatabaseConnectionRow>> Connections(IUseCaseRunner runner, CancellationToken cancellationToken) =>
        runner.RunAsync<GetDatabaseConnectionsHandler, IReadOnlyList<DatabaseConnectionRow>>(
            (handler, token) => handler.HandleAsync(new GetDatabaseConnections(), token),
            cancellationToken);

    internal static async Task<DatabaseConnectionRow> Find(IUseCaseRunner runner, Guid id, CancellationToken cancellationToken) =>
        (await Connections(runner, cancellationToken)).FirstOrDefault(row => row.Id == id)
        ?? throw new DomainException("Conexão de banco não encontrada.");

    private static Task<ConnectionUsage> Usage(IUseCaseRunner runner, Guid id, CancellationToken cancellationToken) =>
        runner.RunAsync<DatabaseUsageHandlers, ConnectionUsage>(
            (handler, token) => handler.HandleAsync(new GetDatabaseConnectionUsage(id), token),
            cancellationToken);

    private static async Task<ConnectionDetail> Detail(IUseCaseRunner runner, Guid id, CancellationToken cancellationToken)
    {
        var row = await Find(runner, id, cancellationToken);
        var usage = await Usage(runner, id, cancellationToken);
        var rules = EnvironmentPolicy.For(row.Environment);

        return new ConnectionDetail(
            ConnectionInfo.Of(row),
            new EnvironmentInfo(
                row.Environment.ToString(),
                rules.IsProtected,
                rules.AllowedCopyDestinations.Select(environment => environment.ToString()).Order(StringComparer.Ordinal).ToList(),
                rules.RequiresVerification,
                rules.RequiresTypedConfirmation),
            new UsageSummary(
                usage.AnonymizationProfiles.Select(profile => new NamedRef(profile.Id, profile.Name)).ToList(),
                usage.Aliases.Select(alias => new NamedRef(alias.Id, alias.Alias)).ToList(),
                usage.CopyProfilesAsSource.Concat(usage.CopyProfilesAsDestination)
                    .DistinctBy(copy => copy.Id)
                    .Select(copy => new NamedRef(copy.Id, copy.Name))
                    .ToList()));
    }

    /// <summary>
    /// O que o MCP não troca numa conexão (ADR-059). A senha guardada continua
    /// presa à conexão quando ela muda: trocar o servidor, a porta, o usuário ou
    /// o SSL de quem tem senha, e depois testar, mandaria a senha para onde o
    /// cliente quisesse. E o ambiente só sobe — afrouxar Produção é decisão de
    /// tela, de quem vê o badge.
    /// </summary>
    private static void EnsureSafeChange(
        DatabaseConnectionRow row,
        string? host,
        int? port,
        string? username,
        DatabaseSslMode sslMode,
        DatabaseEnvironment environment)
    {
        var movesTheCredential =
            (host is not null && !string.Equals(host.Trim(), row.Host, StringComparison.OrdinalIgnoreCase))
            || (port is not null && port != row.Port)
            || (username is not null && !string.Equals(username.Trim(), row.Username, StringComparison.Ordinal))
            || sslMode != row.SslMode;

        if (row.HasPassword && movesTheCredential)
        {
            throw new DomainException(
                $"{row.Name} tem senha guardada: servidor, porta, usuário e SSL só mudam pela tela de Bancos, " +
                "para a senha não seguir para outro endereço. Ou duplique a conexão, que nasce sem senha.");
        }

        if (environment < row.Environment)
        {
            throw new DomainException(
                $"Pelo MCP, o ambiente de {row.Name} só pode ficar igual ou mais restrito que {row.Environment}. " +
                "Afrouxar a proteção é pela tela de Bancos.");
        }
    }

    private static ConnectionPermissions? Permissions(string[]? names)
    {
        if (names is null)
        {
            return null;
        }

        var flags = names
            .Select(name => McpInput.Enum<ConnectionPermission>(name, "a permissão"))
            .Aggregate(default(ConnectionPermission), (all, flag) => all | flag);

        return ConnectionPermissions.FromFlags(flags);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Uma conexão sem senha. <see cref="HasPassword"/> é tudo o que se diz dela.</summary>
public sealed record ConnectionInfo(
    Guid Id,
    string Name,
    string Provider,
    string Host,
    int Port,
    string? Database,
    string Username,
    string Environment,
    bool IsProtected,
    string SslMode,
    string? Description,
    bool IsEnabled,
    IReadOnlyList<string> Permissions,
    bool HasPassword,
    DateTimeOffset UpdatedAt)
{
    public static ConnectionInfo Of(DatabaseConnectionRow row) => new(
        row.Id,
        row.Name,
        "postgresql",
        row.Host,
        row.Port,
        row.Database,
        row.Username,
        row.Environment.ToString(),
        row.IsProtected,
        row.SslMode.ToString(),
        row.Description,
        row.IsEnabled,
        Enum.GetValues<ConnectionPermission>()
            .Where(flag => flag != ConnectionPermission.All && flag != default && row.Permissions.ToFlags().HasFlag(flag))
            .Select(flag => flag.ToString())
            .ToList(),
        row.HasPassword,
        row.UpdatedAt);
}

public sealed record EnvironmentInfo(
    string Environment,
    bool IsProtected,
    IReadOnlyList<string> AllowedCopyDestinations,
    bool RequiresVerification,
    bool RequiresTypedConfirmation);

public sealed record NamedRef(Guid Id, string Name);

public sealed record UsageSummary(
    IReadOnlyList<NamedRef> AnonymizationProfiles,
    IReadOnlyList<NamedRef> Aliases,
    IReadOnlyList<NamedRef> CopyProfiles);

public sealed record ConnectionDetail(ConnectionInfo Connection, EnvironmentInfo Policy, UsageSummary UsedBy);

public sealed record ProviderInfo(string Id, string Name, string Notes);

public sealed record FieldInfo(string Name, string Type, bool Required, string Description);

public sealed record EnvironmentPolicyInfo(
    string Environment,
    bool IsProtected,
    IReadOnlyList<string> AlwaysAllowed,
    IReadOnlyList<string> NeverAllowed,
    IReadOnlyList<string> Defaults,
    IReadOnlyList<string> AllowedCopyDestinations);

/// <summary>Os campos e as regras de uma conexão, para a IA perguntar antes de errar.</summary>
public sealed record ConnectionSchema(
    IReadOnlyList<FieldInfo> Fields,
    IReadOnlyList<EnvironmentPolicyInfo> Environments,
    IReadOnlyList<string> SslModes,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Notes)
{
    public static ConnectionSchema Current { get; } = Build();

    private static ConnectionSchema Build()
    {
        static IReadOnlyList<string> Names(ConnectionPermission flags) =>
            Enum.GetValues<ConnectionPermission>()
                .Where(flag => flag != ConnectionPermission.All && flag != default && flags.HasFlag(flag))
                .Select(flag => flag.ToString())
                .ToList();

        return new ConnectionSchema(
            [
                new("name", "string (até 80)", true, "Nome único da conexão."),
                new("host", "string (até 255)", true, "Servidor; sem espaços, vírgulas ou hífen no começo."),
                new("port", "int 1–65535", false, "Padrão 5432."),
                new("database", "string (até 63)", false, "Banco fixo. Vazio = só o servidor (ADR-057): o banco é escolhido na cópia."),
                new("username", "string (até 63)", true, "Usuário do PostgreSQL."),
                new("environment", "enum", true, "Development, Test, Staging, Production, CriticalProduction."),
                new("sslMode", "enum", false, "Prefer (padrão), Require, VerifyFull, Disable."),
                new("description", "string (até 500)", false, "Livre."),
                new("permissions", "enum[]", false, "Pedidas; a política do ambiente encaixa (piso e teto)."),
                new("password", "—", false, "Não aceito pelo MCP. Definida só na tela de Bancos, guardada no cofre do sistema."),
            ],
            Enum.GetValues<DatabaseEnvironment>()
                .Select(environment =>
                {
                    var rules = EnvironmentPolicy.For(environment);

                    return new EnvironmentPolicyInfo(
                        environment.ToString(),
                        rules.IsProtected,
                        Names(rules.Floor),
                        Names(ConnectionPermission.All & ~rules.Ceiling),
                        Names(rules.Defaults),
                        rules.AllowedCopyDestinations.Select(destination => destination.ToString()).Order(StringComparer.Ordinal).ToList());
                })
                .ToList(),
            Enum.GetNames<DatabaseSslMode>(),
            Names(ConnectionPermission.All),
            [
                "Nenhuma ferramenta executa SQL livre, migração, restore ou cópia: só leitura de catálogo, teste de conexão e cadastro.",
                "Editar uma conexão não toca no servidor.",
            ]);
    }
}
