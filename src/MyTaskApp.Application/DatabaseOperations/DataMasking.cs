using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>
/// O perfil confrontado com as colunas da origem (ADR-058): o que copiar, como
/// mascarar, e o que impede a cópia. Nenhuma linha foi lida para chegar aqui.
/// </summary>
public sealed record MaskingValidation(
    IReadOnlyList<MaskedTablePlan> Tables,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ColumnSuggestion> UncoveredCandidates)
{
    public bool IsValid => Problems.Count == 0;

    public int UncoveredHigh => UncoveredCandidates.Count(candidate => candidate.Sensitivity == ColumnSensitivity.High);

    /// <summary>Colunas mascaradas no SELECT da cópia, contando cada partição.</summary>
    public int MaskedColumns => Tables.Sum(table => table.Columns.Count(column => column.IsMasked));

    /// <summary>Tabelas (partições contadas uma a uma) que vão vazias para o destino.</summary>
    public int SkippedTables => Tables.Count(table => table.SkipData);

    /// <summary>Tabelas com linhas que vão sem máscara (ADR-080): a condição precisa ser conferida no servidor.</summary>
    public IReadOnlyList<MaskedTablePlan> FilteredTables => Tables.Where(table => table.HasUnmaskedRows).ToList();

    /// <summary>
    /// Fora da comparação de linhas: as tabelas sem dados e as particionadas de
    /// cima delas, que no destino têm menos linhas que na origem, de propósito.
    /// </summary>
    public IReadOnlyCollection<string> NotCompared { get; init; } = [];

    /// <summary>As contagens de <see cref="UnmaskedRows.CountAsync"/>; vazio até alguém conferir no servidor.</summary>
    public IReadOnlyList<UnmaskedRowCount> UnmaskedRows { get; init; } = [];
}

/// <summary>
/// A condição de cada tabela com linhas sem máscara (ADR-080), rodada na
/// origem antes da cópia: quantas linhas saem com o valor real, ou por que o
/// servidor recusou — e aí a cópia não começa, em vez de quebrar no meio do COPY.
/// </summary>
public static class UnmaskedRows
{
    public static async Task<MaskingValidation> CountAsync(
        IPostgresServerInspector inspector,
        DatabaseConnectionSnapshot source,
        MaskingValidation validation,
        CancellationToken cancellationToken = default)
    {
        var counts = new List<UnmaskedRowCount>();

        foreach (var table in validation.FilteredTables)
        {
            try
            {
                counts.Add(new UnmaskedRowCount(table.QualifiedName, table.RowFilter!,
                    await inspector.CountMatchingAsync(source, table.QualifiedName, table.RowFilter!, cancellationToken)));
            }
            catch (DomainException exception)
            {
                counts.Add(new UnmaskedRowCount(table.QualifiedName, table.RowFilter!, 0, exception.Message));
            }
        }

        return counts.Count == 0
            ? validation
            : validation with
            {
                Problems = [.. validation.Problems, .. counts.Where(count => count.Error is not null)
                    .Select(count => $"A condição das linhas sem máscara de {count.Table} não roda na origem: {count.Error}")],
                Warnings = [.. validation.Warnings, .. counts.Where(count => count.Error is null)
                    .Select(count => $"{count.Table}: {count.Rows} linha(s) vão sem máscara, com os valores reais ({count.Condition}).")],
                UnmaskedRows = counts,
            };
    }
}

/// <summary>
/// Monta a cópia mascarada a partir do catálogo da origem e das regras do
/// perfil (ADR-058), e recusa o que não daria certo — antes de qualquer processo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chave não se mascara.</b> Uma PK ou FK mascarada quebraria as ligações
/// entre as tabelas (ou as próprias FKs, criadas depois dos dados). Quem
/// identifica a pessoa é o CPF, o e-mail — não o id.
/// </para>
/// <para>
/// <b>Índice único só aceita máscara que mantém únicos.</b> Texto fixo, nulo
/// ou parcial repetem valores, e o índice falharia ao ser criado no destino.
/// </para>
/// <para>
/// <b>Tabela sem dados não pode ser referenciada por uma com dados.</b> As
/// FKs são criadas depois das linhas; uma linha de <c>pedidos</c> apontando
/// para um <c>clientes</c> vazio faria a FK falhar no destino.
/// </para>
/// <para>
/// <b>Linhas sem máscara (ADR-080).</b> O filtro de uma tabela vai junto no
/// plano, e o de uma particionada vale para as partições. Numa tabela sem
/// dados ele não tem o que escolher: é erro, para o usuário decidir qual dos
/// dois queria.
/// </para>
/// </remarks>
public static class MaskingPlanner
{
    public static MaskingValidation Plan(AnonymizationProfile? profile, SourceCatalog catalog)
    {
        var rules = (profile?.Rules ?? []).ToDictionary(rule => rule.ColumnKey, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        var keys = catalog.Keys
            .GroupBy(key => key.ColumnKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(key => key.Role).ToHashSet(), StringComparer.Ordinal);
        var skipping = new SkippedTables(profile, catalog);
        var filters = (profile?.RowFilters ?? []).ToDictionary(filter => filter.TableKey, StringComparer.Ordinal);
        var filtered = new HashSet<string>(StringComparer.Ordinal);
        var tables = new List<MaskedTablePlan>();

        foreach (var table in catalog.Tables)
        {
            var filter = filters.GetValueOrDefault(table.QualifiedName) ?? (table.Root is { } filterRoot ? filters.GetValueOrDefault(filterRoot) : null);

            if (filter is not null)
            {
                filtered.Add(filter.TableKey);
            }

            if (skipping.Skips(table.QualifiedName))
            {
                if (filter is not null)
                {
                    problems.Add($"{filter.TableKey} está nas tabelas sem dados e nas linhas sem máscara: escolha um dos dois.");
                }

                // Nenhuma linha sai: as regras dela não têm o que mascarar, e não são um erro.
                foreach (var column in table.Columns)
                {
                    if ((Find(rules, table.Schema, table.Table, column.Name) ?? (table.Root is { } skippedRoot ? Find(rules, skippedRoot, column.Name) : null))
                        is { } unused)
                    {
                        used.Add(unused.ColumnKey);
                    }
                }

                tables.Add(new MaskedTablePlan(table.Schema, table.Table, table.Bytes,
                    table.Columns.Where(column => !column.IsGenerated).Select(column => new MaskedColumnPlan(column.Name, column.DataType)).ToList(),
                    table.EstimatedRows, SkipData: true));
                continue;
            }

            var columns = new List<MaskedColumnPlan>();

            foreach (var column in table.Columns)
            {
                var rule = Find(rules, table.Schema, table.Table, column.Name)
                    ?? (table.Root is { } root ? Find(rules, root, column.Name) : null);

                if (rule is not null)
                {
                    used.Add(rule.ColumnKey);
                }

                if (column.IsGenerated)
                {
                    // O destino calcula a coluna gerada sozinho — a partir das colunas já mascaradas.
                    if (rule is not null)
                    {
                        problems.Add($"{rule.QualifiedName} é uma coluna gerada: mascare as colunas de onde ela vem.");
                    }

                    continue;
                }

                if (rule is not null && Judge(rule, column, keys) is { } problem)
                {
                    problems.Add(problem);
                }

                columns.Add(rule is null
                    ? new MaskedColumnPlan(column.Name, column.DataType)
                    : new MaskedColumnPlan(column.Name, column.DataType, rule.Method, rule.Argument));
            }

            tables.Add(new MaskedTablePlan(table.Schema, table.Table, table.Bytes, columns, table.EstimatedRows, RowFilter: filter?.Condition));
        }

        foreach (var rule in rules.Values.Where(rule => !used.Contains(rule.ColumnKey)))
        {
            problems.Add($"{rule.QualifiedName} não existe no banco de origem.");
        }

        problems.AddRange(skipping.BrokenForeignKeys());

        var warnings = new List<string>();

        if (catalog.LargeObjects > 0)
        {
            warnings.Add($"{catalog.LargeObjects} objeto(s) grande(s) (pg_largeobject) não são copiados.");
        }

        warnings.AddRange(skipping.Missing().Select(name => $"{name} está nas tabelas sem dados, mas não existe no banco de origem."));
        warnings.AddRange(filters.Keys.Where(name => !filtered.Contains(name)).Order(StringComparer.Ordinal)
            .Select(name => $"{name} tem linhas sem máscara, mas não existe no banco de origem."));
        warnings.AddRange(tables
            .Where(table => table.RowFilter is not null && !table.SkipData && !table.HasMaskedColumns)
            .Select(table => $"{table.QualifiedName} tem linhas sem máscara, mas nenhuma coluna mascarada: o filtro não muda nada."));

        return new MaskingValidation(tables, problems.Distinct().ToList(), warnings, Uncovered(catalog, rules.Keys, skipping))
        {
            NotCompared = skipping.NotCompared(),
        };
    }

    /// <summary>
    /// As colunas que parecem dado pessoal e não têm regra — nem na própria
    /// tabela, nem na tabela particionada de cima. Numa tabela sem dados, o dado
    /// não sai: a coluna está coberta.
    /// </summary>
    private static List<ColumnSuggestion> Uncovered(SourceCatalog catalog, IEnumerable<string> ruleKeys, SkippedTables skipping)
    {
        var covered = ruleKeys.ToHashSet(StringComparer.Ordinal);
        var roots = catalog.Tables
            .Where(table => table.Root is not null)
            .ToDictionary(table => table.QualifiedName, table => table.Root!, StringComparer.Ordinal);

        return SensitiveColumnClassifier.Suggest(catalog.AllColumns)
            .Where(candidate => !skipping.Skips($"{candidate.Schema}.{candidate.Table}"))
            .Where(candidate => !covered.Contains(candidate.ColumnKey))
            .Where(candidate => !roots.TryGetValue($"{candidate.Schema}.{candidate.Table}", out var root)
                || !covered.Contains($"{root}.{candidate.Column}"))
            .ToList();
    }

    /// <summary>As tabelas sem dados do perfil, contra o catálogo: uma particionada leva as partições junto.</summary>
    private sealed class SkippedTables(AnonymizationProfile? profile, SourceCatalog catalog)
    {
        private readonly HashSet<string> _names =
            (profile?.SkippedTables ?? []).Select(table => table.TableKey).ToHashSet(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _roots = catalog.Tables
            .Where(table => table.Root is not null)
            .ToDictionary(table => table.QualifiedName, table => table.Root!, StringComparer.Ordinal);

        public bool Skips(string table) =>
            _names.Contains(table) || (_roots.TryGetValue(table, out var root) && _names.Contains(root));

        /// <summary>Uma FK de uma tabela com dados para uma sem dados falharia no destino.</summary>
        public IEnumerable<string> BrokenForeignKeys() =>
            catalog.ForeignKeys
                .Where(link => Skips(link.To) && !Skips(link.From))
                .Select(link => (From: Shown(link.From), To: Shown(link.To)))
                .Distinct()
                .Select(link => $"{link.From} tem FK para {link.To}, que está sem dados: marque {link.From} também, ou copie os dados de {link.To}.");

        public IEnumerable<string> Missing() =>
            _names.Where(name => !catalog.Tables.Any(table =>
                    table.QualifiedName.Equals(name, StringComparison.Ordinal) || string.Equals(table.Root, name, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal);

        public IReadOnlyCollection<string> NotCompared()
        {
            var names = new HashSet<string>(_names, StringComparer.Ordinal);

            foreach (var table in catalog.Tables.Where(table => Skips(table.QualifiedName)))
            {
                names.Add(table.QualifiedName);

                if (table.Root is { } root)
                {
                    names.Add(root);
                }
            }

            return names;
        }

        /// <summary>Uma partição aparece pelo nome da tabela particionada: é o que o usuário marcou.</summary>
        private string Shown(string table) => _roots.GetValueOrDefault(table) ?? table;
    }

    private static string? Judge(AnonymizationRule rule, SourceColumn column, Dictionary<string, HashSet<KeyRole>> keys)
    {
        var info = MaskingCatalog.Of(rule.Method);
        var type = MaskingCatalog.ValueTypeOf(column.DataType);

        if (!MaskingCatalog.Fits(rule.Method, type))
        {
            return $"{rule.QualifiedName} é {column.DataType}: a máscara \"{info.Label}\" não serve para esse tipo.";
        }

        if (rule.Method == MaskingMethod.Null && !column.IsNullable)
        {
            return $"{rule.QualifiedName} não aceita NULL: escolha outra máscara.";
        }

        if (!keys.TryGetValue(rule.ColumnKey, out var roles))
        {
            return null;
        }

        if (roles.Contains(KeyRole.Primary) || roles.Contains(KeyRole.Foreign) || roles.Contains(KeyRole.Referenced))
        {
            return $"{rule.QualifiedName} é chave (PK ou FK): mascarar quebraria as ligações entre as tabelas.";
        }

        return roles.Contains(KeyRole.Unique) && !info.KeepsUniqueness
            ? $"{rule.QualifiedName} tem índice único, e a máscara \"{info.Label}\" repete valores: use Hash ou E-mail falso."
            : null;
    }

    private static AnonymizationRule? Find(Dictionary<string, AnonymizationRule> rules, string schema, string table, string column) =>
        rules.GetValueOrDefault($"{schema}.{table}.{column}");

    private static AnonymizationRule? Find(Dictionary<string, AnonymizationRule> rules, string qualifiedTable, string column) =>
        rules.GetValueOrDefault($"{qualifiedTable}.{column}");
}

/// <summary>
/// Depois da cópia mascarada: cada coluna com regra precisa ter chegado
/// mascarada. Máscara variável (hash, parcial, nome…) não pode bater com a
/// origem; máscara fixa (texto, número, nulo) precisa estar em todas as linhas.
/// Numa tabela com linhas sem máscara (ADR-080), essas linhas ficam fora da
/// comparação: chegaram iguais de propósito.
/// </summary>
public interface IMaskingVerifier
{
    Task<IReadOnlyList<CheckResult>> VerifyAsync(
        IReadOnlyList<MaskedTablePlan> tables,
        DatabaseConnectionSnapshot source,
        DatabaseConnectionSnapshot destination,
        CancellationToken cancellationToken = default);
}

public sealed class MaskingVerifier(IPostgresServerInspector inspector, IDatabaseSecurityPolicy policy) : IMaskingVerifier
{
    internal const int VerificationRows = 10_000;

    /// <summary>Teto de colunas conferidas: cada uma é uma leitura nos dois servidores.</summary>
    internal const int VerificationColumns = 20;

    public async Task<IReadOnlyList<CheckResult>> VerifyAsync(
        IReadOnlyList<MaskedTablePlan> tables,
        DatabaseConnectionSnapshot source,
        DatabaseConnectionSnapshot destination,
        CancellationToken cancellationToken = default)
    {
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.Verify, source, destination));

        var salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var results = new List<CheckResult>();
        var masked = tables
            .SelectMany(table => table.Columns.Where(column => column.IsMasked).Select(column => (table, column)))
            .Take(VerificationColumns);

        foreach (var (table, column) in masked)
        {
            var reference = new ColumnReference(table.Schema, table.Table, column.Name);

            if (column.Method is MaskingMethod.Null or MaskingMethod.FixedText or MaskingMethod.FixedNumber)
            {
                var different = await inspector.CountNotMaskedAsync(destination, reference, column, cancellationToken);

                // A condição pode citar a coluna mascarada: no destino ela já não vale. Conta-se na origem.
                var unmasked = table.RowFilter is { } condition
                    ? await inspector.CountMatchingAsync(source, table.QualifiedName, condition, cancellationToken)
                    : 0;
                results.Add(VerificationEvaluator.CompareFixed(reference, different, unmasked));
                continue;
            }

            var before = await inspector.FingerprintAsync(source, reference, salt, VerificationRows, table.RowFilter, cancellationToken);
            var after = await inspector.FingerprintAsync(destination, reference, salt, VerificationRows, cancellationToken: cancellationToken);
            results.Add(VerificationEvaluator.CompareSensitive(before, after));
        }

        if (results.Count == 0)
        {
            results.Add(new CheckResult(VerificationEvaluator.SensitiveData, "Regras", CheckOutcome.Warning, "O perfil não tem regras para conferir."));
        }

        return results;
    }
}
