namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// Uma regra de mascaramento confirmada pelo usuário: esta coluna sai assim.
/// O que o app sugere só vira regra quando alguém confirma (ADR-056).
/// </summary>
public sealed record AnonymizationRuleSpec(
    string Schema,
    string Table,
    string Column,
    MaskingMethod Method,
    string? Argument,
    ColumnSensitivity Sensitivity);

/// <summary>Uma tabela que vai para o destino vazia: a estrutura sim, as linhas não.</summary>
public sealed record SkippedTableSpec(string Schema, string Table);

/// <summary>
/// Um perfil de anonimização (ADR-058): as colunas de um banco que saem
/// mascaradas, e como. Nada disso vai para o servidor de origem: o app monta
/// o SELECT da cópia com as máscaras, e o dado real não sai de lá.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConnectionId"/> é a conexão de onde as colunas são lidas para
/// sugerir e pré-visualizar — em geral, a própria origem. O vínculo "este
/// banco usa este perfil" fica no apelido (ADR-057).
/// </para>
/// <para>
/// <see cref="SkippedTables"/>: tabelas cujas linhas não saem da origem —
/// nem mascaradas. A tabela é criada no destino, com índices e chaves, e
/// fica vazia. Serve para logs, auditoria, filas, ou dados pessoais que o
/// desenvolvimento não precisa.
/// </para>
/// </remarks>
public sealed class AnonymizationProfile
{
    public const int MaxNameLength = 80;

    public const int MaxDescriptionLength = 500;

    public const int MaxRules = 2000;

    public const int MaxSkippedTables = 2000;

    private readonly List<AnonymizationRule> _rules = [];

    private readonly List<AnonymizationSkippedTable> _skippedTables = [];

    private AnonymizationProfile(Guid id, DateTimeOffset createdAt)
    {
        Id = id;
        Name = string.Empty;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    /// <summary>A conexão de onde ler as colunas (sugestões e pré-visualização).</summary>
    public Guid ConnectionId { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<AnonymizationRule> Rules => _rules;

    /// <summary>As tabelas que vão vazias para o destino; vale também para as partições de uma tabela particionada.</summary>
    public IReadOnlyList<AnonymizationSkippedTable> SkippedTables => _skippedTables;

    public static AnonymizationProfile Create(
        string name,
        string? description,
        Guid connectionId,
        DateTimeOffset createdAt)
    {
        var profile = new AnonymizationProfile(Guid.CreateVersion7(createdAt), createdAt) { IsEnabled = true };
        profile.Apply(name, description, connectionId);
        return profile;
    }

    public void Update(string name, string? description, Guid connectionId, DateTimeOffset at)
    {
        Apply(name, description, connectionId);
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

    /// <summary>
    /// O conjunto inteiro de regras, como confirmado agora. Atômico: uma regra
    /// inválida recusa todas, e a mesma coluna duas vezes é erro, não "a última ganha".
    /// </summary>
    public void ReplaceRules(IEnumerable<AnonymizationRuleSpec> specs, DateTimeOffset confirmedAt)
    {
        var rules = new List<AnonymizationRule>();
        var columns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            var rule = AnonymizationRule.Create(Id, spec, confirmedAt);

            if (!columns.Add(rule.ColumnKey))
            {
                throw new DomainException($"A coluna {rule.QualifiedName} aparece duas vezes nas regras.");
            }

            rules.Add(rule);
        }

        if (rules.Count > MaxRules)
        {
            throw new DomainException($"Um perfil tem no máximo {MaxRules} regras.");
        }

        _rules.Clear();
        _rules.AddRange(rules);
        UpdatedAt = confirmedAt;
    }

    /// <summary>
    /// O conjunto inteiro de tabelas sem dados, como confirmado agora. Atômico,
    /// como as regras: a mesma tabela duas vezes é erro.
    /// </summary>
    public void ReplaceSkippedTables(IEnumerable<SkippedTableSpec> specs, DateTimeOffset confirmedAt)
    {
        var tables = new List<AnonymizationSkippedTable>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            var table = AnonymizationSkippedTable.Create(Id, spec, confirmedAt);

            if (!keys.Add(table.TableKey))
            {
                throw new DomainException($"A tabela {table.TableKey} aparece duas vezes nas tabelas sem dados.");
            }

            tables.Add(table);
        }

        if (tables.Count > MaxSkippedTables)
        {
            throw new DomainException($"Um perfil tem no máximo {MaxSkippedTables} tabelas sem dados.");
        }

        _skippedTables.Clear();
        _skippedTables.AddRange(tables);
        UpdatedAt = confirmedAt;
    }

    private void Apply(string name, string? description, Guid connectionId)
    {
        var normalizedName = name?.Trim();

        if (string.IsNullOrEmpty(normalizedName))
        {
            throw new DomainException("Informe o nome do perfil de anonimização.");
        }

        if (normalizedName.Length > MaxNameLength)
        {
            throw new DomainException($"O nome do perfil passa de {MaxNameLength} caracteres.");
        }

        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (normalizedDescription?.Length > MaxDescriptionLength)
        {
            throw new DomainException($"A descrição passa de {MaxDescriptionLength} caracteres.");
        }

        if (connectionId == Guid.Empty)
        {
            throw new DomainException("Escolha a conexão de onde ler as colunas.");
        }

        Name = normalizedName;
        Description = normalizedDescription;
        ConnectionId = connectionId;
    }
}

/// <summary>Uma coluna mascarada de um <see cref="AnonymizationProfile"/>.</summary>
public sealed class AnonymizationRule
{
    public const int MaxIdentifierLength = DatabaseConnection.MaxIdentifierLength;

    public const int MaxArgumentLength = MaskingCatalog.MaxFixedTextLength;

    // Exigido pela materialização do EF Core.
    private AnonymizationRule()
    {
        Schema = string.Empty;
        Table = string.Empty;
        Column = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ProfileId { get; private set; }

    public string Schema { get; private set; }

    public string Table { get; private set; }

    public string Column { get; private set; }

    public MaskingMethod Method { get; private set; }

    /// <summary>O parâmetro da máscara, já validado: "0,2", "365", um texto fixo. <c>null</c> quando ela não pede.</summary>
    public string? Argument { get; private set; }

    /// <summary>O que a sugestão achou quando o usuário confirmou; só informativo.</summary>
    public ColumnSensitivity Sensitivity { get; private set; }

    public DateTimeOffset ConfirmedAt { get; private set; }

    public string QualifiedName => $"{Schema}.{Table}.{Column}";

    /// <summary>Identificadores do PostgreSQL diferenciam maiúsculas quando citados: a chave compara exato.</summary>
    public string ColumnKey => QualifiedName;

    public string TableKey => $"{Schema}.{Table}";

    internal static AnonymizationRule Create(Guid profileId, AnonymizationRuleSpec spec, DateTimeOffset confirmedAt)
    {
        var schema = Identifier(spec.Schema, "o schema");
        var table = Identifier(spec.Table, "a tabela");
        var column = Identifier(spec.Column, "a coluna");

        if (!Enum.IsDefined(spec.Method))
        {
            throw new DomainException("Máscara desconhecida.");
        }

        if (!Enum.IsDefined(spec.Sensitivity))
        {
            throw new DomainException("Sensibilidade desconhecida.");
        }

        return new AnonymizationRule
        {
            Id = Guid.CreateVersion7(confirmedAt),
            ProfileId = profileId,
            Schema = schema,
            Table = table,
            Column = column,
            Method = spec.Method,
            Argument = MaskingCatalog.NormalizeArgument(spec.Method, spec.Argument, $"{schema}.{table}.{column}"),
            Sensitivity = spec.Sensitivity,
            ConfirmedAt = confirmedAt,
        };
    }

    internal static string Identifier(string? value, string what)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException($"Informe {what} da regra.");
        }

        if (normalized.Length > MaxIdentifierLength || normalized.Any(char.IsControl))
        {
            throw new DomainException($"Nome inválido para {what}: {normalized}.");
        }

        return normalized;
    }
}

/// <summary>Uma tabela de um <see cref="AnonymizationProfile"/> que vai vazia para o destino.</summary>
public sealed class AnonymizationSkippedTable
{
    // Exigido pela materialização do EF Core.
    private AnonymizationSkippedTable()
    {
        Schema = string.Empty;
        Table = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ProfileId { get; private set; }

    public string Schema { get; private set; }

    public string Table { get; private set; }

    public DateTimeOffset ConfirmedAt { get; private set; }

    /// <summary>"public.auditoria": exato, como os identificadores citados do PostgreSQL.</summary>
    public string TableKey => $"{Schema}.{Table}";

    internal static AnonymizationSkippedTable Create(Guid profileId, SkippedTableSpec spec, DateTimeOffset confirmedAt) => new()
    {
        Id = Guid.CreateVersion7(confirmedAt),
        ProfileId = profileId,
        Schema = AnonymizationRule.Identifier(spec.Schema, "o schema"),
        Table = AnonymizationRule.Identifier(spec.Table, "a tabela"),
        ConfirmedAt = confirmedAt,
    };
}
