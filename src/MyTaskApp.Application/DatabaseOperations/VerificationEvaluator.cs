using System.Globalization;
using System.Text;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>"Database Copy Verification": cada item com PASS, WARN ou FAIL, e o resultado.</summary>
public sealed record VerificationReport(IReadOnlyList<CheckResult> Checks)
{
    public bool Succeeded => Checks.All(check => check.Outcome != CheckOutcome.Fail);

    public string Result => Succeeded ? "SUCCESS" : "FAIL";

    /// <summary>Uma linha por categoria, com o pior resultado dela — é o que vai para a auditoria.</summary>
    public string Summarize()
    {
        var text = new StringBuilder();

        foreach (var category in Checks.GroupBy(check => check.Category))
        {
            var worst = category.Max(check => check.Outcome);
            text.Append(CultureInfo.InvariantCulture, $"{category.Key}: {Label(worst)}; ");
        }

        return text.Append(CultureInfo.InvariantCulture, $"Result: {Result}").ToString();
    }

    public static string Label(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Pass => "PASS",
        CheckOutcome.Warning => "WARN",
        _ => "FAIL",
    };
}

/// <summary>
/// Compara origem e destino depois do restore (ADR-056) e diz o que bate.
/// Puro: as consultas são feitas antes, pelo orquestrador.
/// </summary>
public static class VerificationEvaluator
{
    public const string Schema = "Schema";
    public const string Tables = "Tables";
    public const string Constraints = "Constraints";
    public const string Indexes = "Indexes";
    public const string Sequences = "Sequences";
    public const string Rows = "Row counts";
    public const string SensitiveData = "Sensitive Data";

    /// <summary>Acima disto de valores iguais, é aviso: anonimização fraca ou coluna quase constante.</summary>
    public const double SuspiciousEqualShare = 0.5;

    public static IReadOnlyList<CheckResult> CompareStructure(DatabaseStructure source, DatabaseStructure destination)
    {
        var results = new List<CheckResult>
        {
            CompareSets(Schema, "Schemas", source.Schemas, destination.Schemas),
            CompareSets(Tables, "Tabelas", source.Tables, destination.Tables),
        };

        var types = source.ConstraintsByType.Keys.Union(destination.ConstraintsByType.Keys).Order(StringComparer.Ordinal);
        var differences = types
            .Select(type => (type, from: source.ConstraintsByType.GetValueOrDefault(type), to: destination.ConstraintsByType.GetValueOrDefault(type)))
            .Where(item => item.from != item.to)
            .Select(item => $"{item.type}: {item.from} → {item.to}")
            .ToList();

        results.Add(differences.Count == 0
            ? new CheckResult(Constraints, "Constraints", CheckOutcome.Pass, $"{source.ConstraintsByType.Values.Sum()} iguais.")
            : new CheckResult(Constraints, "Constraints", CheckOutcome.Fail, string.Join("; ", differences)));

        results.Add(CompareCounts(Indexes, "Índices", source.Indexes, destination.Indexes));
        results.Add(CompareCounts(Sequences, "Sequences", source.Sequences, destination.Sequences));
        return results;
    }

    public static CheckResult CompareRows(IReadOnlyList<RowCount> source, IReadOnlyList<RowCount> destination)
    {
        var target = destination.ToDictionary(count => count.Table, StringComparer.Ordinal);
        var different = new List<string>();
        var estimated = 0;

        foreach (var count in source)
        {
            if (!target.TryGetValue(count.Table, out var other))
            {
                different.Add($"{count.Table}: ausente");
                continue;
            }

            if (count.IsEstimate || other.IsEstimate)
            {
                estimated++;
                continue;
            }

            if (count.Rows != other.Rows)
            {
                different.Add($"{count.Table}: {count.Rows} → {other.Rows}");
            }
        }

        if (different.Count > 0)
        {
            return new CheckResult(Rows, "Linhas por tabela", CheckOutcome.Fail, string.Join("; ", different.Take(10)));
        }

        return estimated > 0
            ? new CheckResult(Rows, "Linhas por tabela", CheckOutcome.Warning, $"{estimated} tabela(s) grande(s) só por estimativa.")
            : new CheckResult(Rows, "Linhas por tabela", CheckOutcome.Pass, $"{source.Count} tabela(s) iguais.");
    }

    /// <summary>
    /// As tabelas sem dados precisam ter chegado vazias: uma linha que seja
    /// quer dizer que algo saiu da origem sem dever.
    /// </summary>
    public static CheckResult CompareSkipped(IReadOnlyList<RowCount> destination)
    {
        var filled = destination.Where(count => count.Rows > 0).Select(count => $"{count.Table}: {count.Rows}").ToList();

        return filled.Count == 0
            ? new CheckResult(Rows, "Tabelas sem dados", CheckOutcome.Pass, $"{destination.Count} tabela(s) vazia(s) no destino, como pedido.")
            : new CheckResult(Rows, "Tabelas sem dados", CheckOutcome.Fail, $"Deviam estar vazias: {string.Join("; ", filled.Take(10))}.");
    }

    /// <summary>
    /// Uma coluna mascarada não pode ter chegado igual à origem. Pareia pela
    /// chave (hash), conta os valores não nulos idênticos.
    /// </summary>
    public static CheckResult CompareSensitive(ColumnFingerprint source, ColumnFingerprint destination)
    {
        var name = source.Column.ColumnKey;
        var compared = 0;
        var equal = 0;

        foreach (var (key, value) in source.Values)
        {
            if (value is null || !destination.Values.TryGetValue(key, out var other) || other is null)
            {
                continue;
            }

            compared++;

            if (string.Equals(value, other, StringComparison.Ordinal))
            {
                equal++;
            }
        }

        if (compared == 0)
        {
            return new CheckResult(SensitiveData, name, CheckOutcome.Warning, "Sem valores para comparar (tabela vazia, sem chave primária ou só nulos).");
        }

        var share = (double)equal / compared;

        if (equal == compared)
        {
            return new CheckResult(SensitiveData, name, CheckOutcome.Fail, $"Os {compared} valores comparados chegaram iguais à origem: a máscara não foi aplicada.");
        }

        return share > SuspiciousEqualShare
            ? new CheckResult(SensitiveData, name, CheckOutcome.Warning, $"{equal} de {compared} valores iguais à origem.")
            : new CheckResult(SensitiveData, name, CheckOutcome.Pass, $"{compared - equal} de {compared} valores diferentes da origem.");
    }

    /// <summary>Uma máscara fixa (texto, número, nulo) tem de estar em todas as linhas do destino.</summary>
    public static CheckResult CompareFixed(ColumnReference column, long notMasked) =>
        notMasked == 0
            ? new CheckResult(SensitiveData, column.ColumnKey, CheckOutcome.Pass, "Todas as linhas com o valor da máscara.")
            : new CheckResult(SensitiveData, column.ColumnKey, CheckOutcome.Fail,
                $"{notMasked.ToString(CultureInfo.InvariantCulture)} linha(s) sem a máscara.");

    private static CheckResult CompareSets(string category, string name, IReadOnlyList<string> source, IReadOnlyList<string> destination)
    {
        var missing = source.Except(destination, StringComparer.Ordinal).ToList();
        var extra = destination.Except(source, StringComparer.Ordinal).ToList();

        if (missing.Count == 0 && extra.Count == 0)
        {
            return new CheckResult(category, name, CheckOutcome.Pass, $"{source.Count} iguais.");
        }

        var detail = new List<string>();

        if (missing.Count > 0)
        {
            detail.Add("faltando: " + string.Join(", ", missing.Take(10)));
        }

        if (extra.Count > 0)
        {
            detail.Add("a mais: " + string.Join(", ", extra.Take(10)));
        }

        return new CheckResult(category, name, missing.Count > 0 ? CheckOutcome.Fail : CheckOutcome.Warning, string.Join("; ", detail));
    }

    private static CheckResult CompareCounts(string category, string name, int source, int destination) =>
        source == destination
            ? new CheckResult(category, name, CheckOutcome.Pass, $"{source} iguais.")
            : new CheckResult(category, name, CheckOutcome.Fail, $"{source} na origem, {destination} no destino.");
}
