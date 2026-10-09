using System.Text.RegularExpressions;

namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// Uma regra de mascaramento confirmada pelo usuário: esta coluna sai assim.
/// O que o app sugere só vira regra quando alguém confirma (ADR-056).
/// </summary>
public sealed record AnonymizationRuleSpec(
    string Schema,
    string Table,
    string Column,
    MaskingKind Kind,
    string Expression,
    ColumnSensitivity Sensitivity);

/// <summary>
/// Um perfil de anonimização (ADR-056): a política do PostgreSQL Anonymizer
/// que o dump anônimo usa, com as regras que o usuário confirmou.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConnectionId"/> é a conexão <b>mascarada</b>: o mesmo banco da
/// origem, com uma role marcada <c>MASKED</c> no servidor. O <c>pg_dump</c>
/// feito por ela já sai anonimizado — o dado bruto de produção nunca chega ao
/// disco desta máquina.
/// </para>
/// <para>
/// As regras daqui não são aplicadas pelo app: produção não sofre alteração.
/// Elas geram o script <c>SECURITY LABEL</c> que o DBA executa, e são
/// conferidas contra o que o servidor tem antes de cada cópia.
/// </para>
/// </remarks>
public sealed partial class AnonymizationProfile
{
    public const int MaxNameLength = 80;

    public const int MaxDescriptionLength = 500;

    public const string DefaultPolicyName = "anon";

    public const int MaxRules = 2000;

    private readonly List<AnonymizationRule> _rules = [];

    private AnonymizationProfile(Guid id, DateTimeOffset createdAt)
    {
        Id = id;
        Name = string.Empty;
        PolicyName = DefaultPolicyName;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    /// <summary>A conexão com a role mascarada, por onde sai o dump anônimo.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>O provedor de <c>SECURITY LABEL</c>: <c>anon</c>, ou outra política do Anonymizer 2.x.</summary>
    public string PolicyName { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<AnonymizationRule> Rules => _rules;

    public static AnonymizationProfile Create(
        string name,
        string? description,
        Guid connectionId,
        string? policyName,
        DateTimeOffset createdAt)
    {
        var profile = new AnonymizationProfile(Guid.CreateVersion7(createdAt), createdAt) { IsEnabled = true };
        profile.Apply(name, description, connectionId, policyName);
        return profile;
    }

    public void Update(string name, string? description, Guid connectionId, string? policyName, DateTimeOffset at)
    {
        Apply(name, description, connectionId, policyName);
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

    private void Apply(string name, string? description, Guid connectionId, string? policyName)
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
            throw new DomainException("Escolha a conexão mascarada do perfil.");
        }

        var normalizedPolicy = string.IsNullOrWhiteSpace(policyName) ? DefaultPolicyName : policyName.Trim();

        if (!PolicyNamePattern().IsMatch(normalizedPolicy))
        {
            throw new DomainException("O nome da política só aceita letras minúsculas, dígitos e sublinhado.");
        }

        Name = normalizedName;
        Description = normalizedDescription;
        ConnectionId = connectionId;
        PolicyName = normalizedPolicy;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex PolicyNamePattern();
}

/// <summary>Uma coluna mascarada de um <see cref="AnonymizationProfile"/>.</summary>
public sealed partial class AnonymizationRule
{
    public const int MaxIdentifierLength = DatabaseConnection.MaxIdentifierLength;

    public const int MaxExpressionLength = 500;

    // Exigido pela materialização do EF Core.
    private AnonymizationRule()
    {
        Schema = string.Empty;
        Table = string.Empty;
        Column = string.Empty;
        Expression = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ProfileId { get; private set; }

    public string Schema { get; private set; }

    public string Table { get; private set; }

    public string Column { get; private set; }

    public MaskingKind Kind { get; private set; }

    /// <summary><c>anon.fake_email()</c> numa função; <c>NULL</c> ou um literal num valor.</summary>
    public string Expression { get; private set; }

    /// <summary>O que a sugestão achou quando o usuário confirmou; só informativo.</summary>
    public ColumnSensitivity Sensitivity { get; private set; }

    public DateTimeOffset ConfirmedAt { get; private set; }

    public string QualifiedName => $"{Schema}.{Table}.{Column}";

    /// <summary>Para comparar com o servidor: identificadores do PostgreSQL diferenciam maiúsculas quando citados.</summary>
    public string ColumnKey => QualifiedName;

    internal static AnonymizationRule Create(Guid profileId, AnonymizationRuleSpec spec, DateTimeOffset confirmedAt)
    {
        var schema = Identifier(spec.Schema, "o schema");
        var table = Identifier(spec.Table, "a tabela");
        var column = Identifier(spec.Column, "a coluna");

        if (!Enum.IsDefined(spec.Kind))
        {
            throw new DomainException("Tipo de mascaramento desconhecido.");
        }

        if (!Enum.IsDefined(spec.Sensitivity))
        {
            throw new DomainException("Sensibilidade desconhecida.");
        }

        var expression = ValidExpression(spec.Kind, spec.Expression, $"{schema}.{table}.{column}");

        return new AnonymizationRule
        {
            Id = Guid.CreateVersion7(confirmedAt),
            ProfileId = profileId,
            Schema = schema,
            Table = table,
            Column = column,
            Kind = spec.Kind,
            Expression = expression,
            Sensitivity = spec.Sensitivity,
            ConfirmedAt = confirmedAt,
        };
    }

    /// <summary>
    /// A expressão vai para dentro de um <c>SECURITY LABEL</c> que o DBA roda
    /// como superusuário: só função do <c>anon</c>, ou um literal; nada de
    /// <c>;</c> nem comentário, que fechariam o comando e abririam outro.
    /// </summary>
    internal static string ValidExpression(MaskingKind kind, string? expression, string column)
    {
        var normalized = expression?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException($"Informe a máscara de {column}.");
        }

        if (normalized.Length > MaxExpressionLength)
        {
            throw new DomainException($"A máscara de {column} passa de {MaxExpressionLength} caracteres.");
        }

        if (normalized.Contains(';', StringComparison.Ordinal)
            || normalized.Contains("--", StringComparison.Ordinal)
            || normalized.Contains("/*", StringComparison.Ordinal)
            || normalized.Any(char.IsControl))
        {
            throw new DomainException($"A máscara de {column} tem caracteres não permitidos.");
        }

        var valid = kind switch
        {
            MaskingKind.Function => FunctionPattern().IsMatch(normalized) && !IsStaticMasking(normalized),
            MaskingKind.Value => ValuePattern().IsMatch(normalized),
            _ => false,
        };

        return valid
            ? normalized
            : throw new DomainException(kind == MaskingKind.Function
                ? $"A máscara de {column} deve ser uma função do anon, como anon.fake_email()."
                : $"A máscara de {column} deve ser NULL, um número ou um texto entre aspas simples.");
    }

    /// <summary>Mascaramento estático reescreve o banco: nunca vira regra (ADR-056).</summary>
    private static bool IsStaticMasking(string expression) =>
        expression.StartsWith("anon.anonymize_", StringComparison.OrdinalIgnoreCase)
        || expression.StartsWith("anon.shuffle_column", StringComparison.OrdinalIgnoreCase);

    private static string Identifier(string? value, string what)
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

    [GeneratedRegex(@"^anon\.[a-z_][a-z0-9_]*\(.*\)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex FunctionPattern();

    [GeneratedRegex(@"^(?:NULL|-?\d+(?:\.\d+)?|'(?:[^']|'')*')$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ValuePattern();
}
