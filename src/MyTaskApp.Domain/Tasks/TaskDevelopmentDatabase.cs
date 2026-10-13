using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Domain.Tasks;

/// <summary>
/// O banco copiado para um ambiente de desenvolvimento (ADR-060): em qual
/// servidor, com que nome e quando. É o que os comandos recebem em
/// <c>{database}</c>, <c>{database.host}</c>, <c>{database.port}</c> e
/// <c>{database.user}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Guarda um <b>retrato</b> da conexão de destino, e não uma referência — a
/// mesma razão dos caminhos em <see cref="TaskDevelopment"/>: a conexão pode
/// ser renomeada, editada ou apagada, e o banco continua onde foi criado.
/// <see cref="ConnectionId"/> só sugere o destino de "Copiar de novo".
/// </para>
/// <para>Nunca a senha: ela fica no cofre do sistema, com a conexão.</para>
/// </remarks>
public sealed class TaskDevelopmentDatabase
{
    private TaskDevelopmentDatabase(
        Guid connectionId,
        string connectionName,
        string host,
        int port,
        string username,
        string name,
        Guid? auditId,
        DateTimeOffset copiedAt)
    {
        ConnectionId = connectionId;
        ConnectionName = connectionName;
        Host = host;
        Port = port;
        Username = username;
        Name = name;
        AuditId = auditId;
        CopiedAt = copiedAt;
    }

    /// <summary>A conexão de destino da cópia, quando foi feita.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>O nome da conexão na hora da cópia.</summary>
    public string ConnectionName { get; private set; }

    public string Host { get; private set; }

    public int Port { get; private set; }

    public string Username { get; private set; }

    /// <summary>O banco criado: <c>lock_eco_core_1010_20261009_143000</c>.</summary>
    public string Name { get; private set; }

    /// <summary>A linha da auditoria da cópia, para achá-la no Histórico.</summary>
    public Guid? AuditId { get; private set; }

    public DateTimeOffset CopiedAt { get; private set; }

    /// <summary>
    /// O apelido de onde a cópia veio (ADR-057), ou <c>null</c>. É o
    /// <c>{lock_eco_core_1010}</c> que os comandos trocam pelo <see cref="Name"/>.
    /// </summary>
    public string? Alias { get; private set; }

    public static TaskDevelopmentDatabase Create(
        Guid connectionId,
        string? connectionName,
        string? host,
        int port,
        string? username,
        string? name,
        Guid? auditId,
        DateTimeOffset copiedAt,
        string? alias = null)
    {
        if (connectionId == Guid.Empty)
        {
            throw new DomainException("Falta a conexão do banco do ambiente.");
        }

        if (port is < 1 or > 65535)
        {
            throw new DomainException("A porta do banco do ambiente precisa estar entre 1 e 65535.");
        }

        return new TaskDevelopmentDatabase(
            connectionId,
            Required(connectionName, DatabaseConnection.MaxNameLength, "o nome da conexão"),
            Required(host, DatabaseConnection.MaxHostLength, "o servidor"),
            port,
            Required(username, DatabaseConnection.MaxIdentifierLength, "o usuário"),
            DatabaseConnection.DatabaseName(name ?? string.Empty),
            auditId == Guid.Empty ? null : auditId,
            copiedAt)
        {
            Alias = string.IsNullOrWhiteSpace(alias) ? null : SavedDatabase.NormalizeAlias(alias),
        };
    }

    private static string Required(string? value, int maxLength, string what)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException($"Falta {what} do banco do ambiente.");
        }

        return normalized.Length <= maxLength
            ? normalized
            : throw new DomainException($"{char.ToUpperInvariant(what[0])}{what[1..]} do banco do ambiente passa de {maxLength} caracteres.");
    }
}
