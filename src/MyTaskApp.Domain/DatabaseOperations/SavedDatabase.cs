using System.Globalization;

namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// Um apelido para um banco de origem (ADR-057): a conexão (o servidor), o
/// banco escolhido nela e a anonimização daquele banco. "lock_eco_core_1010"
/// é cadastrado uma vez; na cópia, escolhe-se o apelido e o destino.
/// </summary>
/// <remarks>
/// O apelido também é o prefixo do banco criado no destino:
/// <c>lock_eco_core_1010_20261009_143000</c>. Por isso é um identificador
/// simples do PostgreSQL — minúsculas, dígitos e sublinhado — e deixa espaço
/// para o sufixo de data dentro dos 63 caracteres.
/// </remarks>
public sealed class SavedDatabase
{
    /// <summary><c>_yyyyMMdd_HHmmss</c>: o que a cópia acrescenta ao apelido.</summary>
    public const string CopySuffixFormat = "'_'yyyyMMdd'_'HHmmss";

    public const int CopySuffixLength = 16;

    public const int MaxAliasLength = DatabaseConnection.MaxIdentifierLength - CopySuffixLength;

    private SavedDatabase(Guid id, DateTimeOffset createdAt)
    {
        Id = id;
        Alias = string.Empty;
        DatabaseName = string.Empty;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Alias { get; private set; }

    /// <summary>A conexão de origem — em geral só o servidor, sem banco fixo.</summary>
    public Guid ConnectionId { get; private set; }

    public string DatabaseName { get; private set; }

    /// <summary>A anonimização deste banco; <c>null</c> para uma cópia sem anonimizar.</summary>
    public Guid? AnonymizationProfileId { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SavedDatabase Create(
        string alias,
        Guid connectionId,
        string databaseName,
        Guid? anonymizationProfileId,
        DateTimeOffset createdAt)
    {
        var saved = new SavedDatabase(Guid.CreateVersion7(createdAt), createdAt);
        saved.Apply(alias, connectionId, databaseName, anonymizationProfileId);
        return saved;
    }

    public void Update(
        string alias,
        Guid connectionId,
        string databaseName,
        Guid? anonymizationProfileId,
        DateTimeOffset at)
    {
        Apply(alias, connectionId, databaseName, anonymizationProfileId);
        UpdatedAt = at;
    }

    /// <summary>O apelido, já validado e em minúsculas.</summary>
    public static string NormalizeAlias(string? alias)
    {
        var normalized = alias?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException("Informe o apelido.");
        }

        if (normalized.Length > MaxAliasLength)
        {
            throw new DomainException(
                $"O apelido passa de {MaxAliasLength} caracteres: o nome do banco copiado ainda leva a data.");
        }

        if (!IsSimpleIdentifier(normalized))
        {
            throw new DomainException("O apelido usa só letras sem acento, números e _, e não começa com número.");
        }

        return normalized;
    }

    /// <summary>
    /// O nome do banco de uma cópia feita em <paramref name="at"/> (hora local):
    /// <c>{prefixo}_yyyyMMdd_HHmmss</c>. O prefixo é o apelido, ou o banco de origem.
    /// </summary>
    public static string CopyName(string prefix, DateTimeOffset at) =>
        CopyPrefix(prefix) + at.ToString(CopySuffixFormat, CultureInfo.InvariantCulture);

    /// <summary>O prefixo como vai no nome do banco copiado: minúsculo, só [a-z0-9_], até 47 caracteres.</summary>
    public static string CopyPrefix(string prefix)
    {
        var normalized = prefix.Trim().ToLowerInvariant();

        // Um banco de origem pode ter mais de 47 caracteres ou letras fora de
        // [a-z0-9_]: sem apelido, o prefixo é cortado e limpo para caber.
        var cleaned = new string(normalized.Select(character => IsSimpleCharacter(character) ? character : '_').ToArray());

        if (cleaned.Length == 0 || char.IsAsciiDigit(cleaned[0]))
        {
            cleaned = "db_" + cleaned;
        }

        if (cleaned.Length > MaxAliasLength)
        {
            cleaned = cleaned[..MaxAliasLength];
        }

        return cleaned;
    }

    private void Apply(string alias, Guid connectionId, string databaseName, Guid? anonymizationProfileId)
    {
        var normalizedAlias = NormalizeAlias(alias);

        if (connectionId == Guid.Empty)
        {
            throw new DomainException("Escolha a conexão de origem.");
        }

        var normalizedDatabase = DatabaseConnection.DatabaseName(databaseName);

        if (anonymizationProfileId == Guid.Empty)
        {
            anonymizationProfileId = null;
        }

        Alias = normalizedAlias;
        ConnectionId = connectionId;
        DatabaseName = normalizedDatabase;
        AnonymizationProfileId = anonymizationProfileId;
    }

    private static bool IsSimpleIdentifier(string value) =>
        !char.IsAsciiDigit(value[0]) && value.All(IsSimpleCharacter);

    private static bool IsSimpleCharacter(char character) =>
        char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_';
}
