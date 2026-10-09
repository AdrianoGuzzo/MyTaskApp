using System.Globalization;

namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// Uma conexão PostgreSQL cadastrada (ADR-056): onde fica o banco, com que
/// usuário, em que ambiente e o que o app pode fazer com ela.
/// </summary>
/// <remarks>
/// <para>
/// <b>Não há senha aqui.</b> Só <see cref="SecretReference"/>, o nome do segredo
/// no cofre do sistema (DPAPI no Windows, chaveiro no Linux). O banco do app
/// pode ir para backup e suporte; a senha de produção não.
/// </para>
/// <para>
/// <b>As permissões são encaixadas no ambiente, não recusadas.</b> O que se pede
/// é cortado pelo teto e completado pelo piso de <see cref="EnvironmentPolicy"/>
/// ao gravar — e de novo a cada leitura de <see cref="Permissions"/>. Pedir
/// "restaurar" numa conexão de produção grava "não restaura"; uma linha
/// editada à mão no SQLite, ou gravada por uma versão com teto diferente,
/// continua sem conseguir mais do que o ambiente admite.
/// </para>
/// </remarks>
public sealed class DatabaseConnection
{
    public const int MaxNameLength = 80;

    public const int MaxHostLength = 255;

    /// <summary>NAMEDATALEN - 1: o limite de identificador do próprio PostgreSQL.</summary>
    public const int MaxIdentifierLength = 63;

    public const int MaxDescriptionLength = 500;

    public const int MaxSecretReferenceLength = 100;

    public const int DefaultPort = 5432;

    public const string SecretReferencePrefix = "postgres-";

    private DatabaseConnection(Guid id, DateTimeOffset createdAt)
    {
        Id = id;
        Name = string.Empty;
        Host = string.Empty;
        Database = string.Empty;
        Username = string.Empty;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    public string Host { get; private set; }

    public int Port { get; private set; }

    public string Database { get; private set; }

    public string Username { get; private set; }

    public DatabaseEnvironment Environment { get; private set; }

    public DatabaseSslMode SslMode { get; private set; }

    public string? Description { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>O que foi gravado. Leia <see cref="Permissions"/>: é o que vale.</summary>
    public ConnectionPermission StoredPermissions { get; private set; }

    /// <summary>O nome do segredo no cofre; <c>null</c> enquanto nenhuma senha foi guardada.</summary>
    public string? SecretReference { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public EnvironmentRules Rules => EnvironmentPolicy.For(Environment);

    /// <summary>As permissões que valem: as gravadas, encaixadas de novo no ambiente.</summary>
    public ConnectionPermissions Permissions => ConnectionPermissions.FromFlags(Rules.Clamp(StoredPermissions));

    public bool IsProtected => Rules.IsProtected;

    public bool CanRead => Permissions.CanRead;

    public bool CanDump => Permissions.CanDump;

    public bool CanRestore => Permissions.CanRestore;

    public bool CanModify => Permissions.CanModify;

    public bool CanCreateDatabase => Permissions.CanCreateDatabase;

    public bool CanDropDatabase => Permissions.CanDropDatabase;

    public bool CanExecuteSql => Permissions.CanExecuteSql;

    public bool AllowAsSource => Permissions.AllowAsSource;

    public bool AllowAsDestination => Permissions.AllowAsDestination;

    public bool RequireAnonymization => Permissions.RequireAnonymization;

    public static DatabaseConnection Create(
        string name,
        string host,
        int port,
        string database,
        string username,
        DatabaseEnvironment environment,
        DatabaseSslMode sslMode,
        string? description,
        ConnectionPermissions requested,
        DateTimeOffset createdAt)
    {
        var connection = new DatabaseConnection(Guid.CreateVersion7(createdAt), createdAt) { IsEnabled = true };
        connection.Apply(name, host, port, database, username, environment, sslMode, description, requested);
        return connection;
    }

    /// <summary>Atômico: tudo é validado antes de qualquer atribuição.</summary>
    public void Update(
        string name,
        string host,
        int port,
        string database,
        string username,
        DatabaseEnvironment environment,
        DatabaseSslMode sslMode,
        string? description,
        ConnectionPermissions requested,
        DateTimeOffset at)
    {
        Apply(name, host, port, database, username, environment, sslMode, description, requested);
        UpdatedAt = at;
    }

    public void SetEnabled(bool enabled, DateTimeOffset at)
    {
        if (IsEnabled == enabled)
        {
            return;
        }

        IsEnabled = enabled;
        UpdatedAt = at;
    }

    /// <summary>O nome do segredo desta conexão no cofre: estável, derivado do Id.</summary>
    public string SecretNameForThis() => SecretReferencePrefix + Id.ToString("N", CultureInfo.InvariantCulture);

    public void AttachSecret(string reference, DateTimeOffset at)
    {
        if (reference != SecretNameForThis())
        {
            throw new DomainException("Referência de senha inválida para esta conexão.");
        }

        SecretReference = reference;
        UpdatedAt = at;
    }

    public void DetachSecret(DateTimeOffset at)
    {
        if (SecretReference is null)
        {
            return;
        }

        SecretReference = null;
        UpdatedAt = at;
    }

    /// <summary>A forma que vai às portas: imutável, sem senha e com as permissões que valem.</summary>
    public DatabaseConnectionSnapshot Snapshot() => new(
        Id,
        Name,
        Host,
        Port,
        Database,
        Username,
        Environment,
        SslMode,
        IsEnabled,
        Permissions,
        SecretReference);

    private void Apply(
        string name,
        string host,
        int port,
        string database,
        string username,
        DatabaseEnvironment environment,
        DatabaseSslMode sslMode,
        string? description,
        ConnectionPermissions requested)
    {
        var normalizedName = Required(name, MaxNameLength, "o nome da conexão");
        var normalizedHost = Identifier(host, MaxHostLength, "o servidor");

        // Vírgula é lista de hosts no libpq: o mesmo cadastro apontaria para
        // vários servidores, e a comparação com os bancos de produção perderia o sentido.
        if (normalizedHost.Any(char.IsWhiteSpace) || normalizedHost.Contains(',', StringComparison.Ordinal))
        {
            throw new DomainException("O servidor não pode ter espaços nem vírgulas.");
        }

        if (port is < 1 or > 65535)
        {
            throw new DomainException("A porta fica entre 1 e 65535.");
        }

        var normalizedDatabase = Identifier(database, MaxIdentifierLength, "o banco");

        // Um "nome de banco" com '=' ou "postgresql://" é lido pelas
        // ferramentas como connection string inteira — e poderia levar um
        // restore para outro servidor, longe da política.
        if (normalizedDatabase.Contains('=', StringComparison.Ordinal)
            || normalizedDatabase.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || normalizedDatabase.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("O nome do banco não pode ter '=' nem ser uma URL de conexão.");
        }
        var normalizedUsername = Identifier(username, MaxIdentifierLength, "o usuário");

        if (!Enum.IsDefined(environment))
        {
            throw new DomainException("Ambiente de banco desconhecido.");
        }

        if (!Enum.IsDefined(sslMode))
        {
            throw new DomainException("Modo SSL desconhecido.");
        }

        var normalizedDescription = Optional(description, MaxDescriptionLength, "A descrição");

        Name = normalizedName;
        Host = normalizedHost;
        Port = port;
        Database = normalizedDatabase;
        Username = normalizedUsername;
        Environment = environment;
        SslMode = sslMode;
        Description = normalizedDescription;
        StoredPermissions = EnvironmentPolicy.For(environment).Clamp(requested.ToFlags());
    }

    private static string Required(string? value, int maxLength, string what)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException($"Informe {what}.");
        }

        if (normalized.Length > maxLength)
        {
            throw new DomainException($"{Capitalized(what)} passa de {maxLength} caracteres.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new DomainException($"{Capitalized(what)} tem caracteres inválidos.");
        }

        return normalized;
    }

    /// <summary>
    /// Vai como argumento de <c>pg_dump</c>/<c>dropdb</c>. Começar com hífen
    /// faria a ferramenta ler o valor como opção — um banco chamado
    /// <c>--help</c>, ou pior.
    /// </summary>
    private static string Identifier(string? value, int maxLength, string what)
    {
        var normalized = Required(value, maxLength, what);

        if (normalized.StartsWith('-'))
        {
            throw new DomainException($"{Capitalized(what)} não pode começar com hífen.");
        }

        return normalized;
    }

    private static string? Optional(string? value, int maxLength, string what)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength
            ? normalized
            : throw new DomainException($"{what} passa de {maxLength} caracteres.");
    }

    private static string Capitalized(string what) =>
        string.Concat(char.ToUpper(what[0], CultureInfo.GetCultureInfo("pt-BR")).ToString(), what[1..]);
}

/// <summary>
/// Uma conexão como as portas a recebem (ADR-056): imutável, sem senha, com as
/// permissões já encaixadas no ambiente. É o que a política de segurança julga
/// — lida do banco no handler, nunca montada pela tela.
/// </summary>
public sealed record DatabaseConnectionSnapshot(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Database,
    string Username,
    DatabaseEnvironment Environment,
    DatabaseSslMode SslMode,
    bool IsEnabled,
    ConnectionPermissions Permissions,
    string? SecretReference)
{
    /// <summary>Host, porta e banco: duas conexões com a mesma chave são o mesmo banco, com qualquer usuário.</summary>
    public string EndpointKey => EndpointKeyOf(Host, Port, Database);

    /// <summary>Host e porta: o mesmo servidor.</summary>
    public string ServerKey => $"{Host.Trim().ToLowerInvariant()}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public bool IsProtected => EnvironmentPolicy.IsProtected(Environment);

    public static string EndpointKeyOf(string host, int port, string database) =>
        $"{host.Trim().ToLowerInvariant()}:{port.ToString(CultureInfo.InvariantCulture)}/{database.Trim()}";
}
