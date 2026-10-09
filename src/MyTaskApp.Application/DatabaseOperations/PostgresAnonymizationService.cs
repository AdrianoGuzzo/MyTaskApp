using System.Text.RegularExpressions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>O perfil confrontado com o servidor: o que falta lá, o que está diferente, o que sobrou sem regra.</summary>
public sealed record AnonymizationValidation(
    AnonymizerStatus Status,
    IReadOnlyList<string> MissingOnServer,
    IReadOnlyList<string> DifferentOnServer,
    IReadOnlyList<string> ExtraOnServer,
    IReadOnlyList<ColumnSuggestion> UncoveredCandidates)
{
    public bool IsValid =>
        Status.Installed && Status.IsSupportedVersion && MissingOnServer.Count == 0 && DifferentOnServer.Count == 0;

    public int UncoveredHigh => UncoveredCandidates.Count(candidate => candidate.Sensitivity == ColumnSensitivity.High);

    /// <summary>As regras que o servidor aplica de fato — as que o dump anônimo mascara.</summary>
    public int MaskedColumns => Status.Rules.Count;

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (!Status.Installed)
        {
            problems.Add("A extensão anon não está instalada no banco.");
        }
        else if (!Status.IsSupportedVersion)
        {
            problems.Add($"O Anonymizer {Status.InstalledVersion} não faz dump anônimo por role mascarada; atualize para 2.x.");
        }

        if (MissingOnServer.Count > 0)
        {
            problems.Add($"Regras do perfil que o servidor não tem: {string.Join(", ", MissingOnServer.Take(10))}. Rode o script do perfil.");
        }

        if (DifferentOnServer.Count > 0)
        {
            problems.Add($"Regras diferentes no servidor: {string.Join(", ", DifferentOnServer.Take(10))}.");
        }

        return problems;
    }
}

/// <summary>
/// O PostgreSQL Anonymizer como mecanismo de proteção (ADR-056): detectar a
/// extensão, validar as regras do perfil contra o servidor, gerar o dump
/// anônimo e conferir o resultado. Não há "anonimizar o banco" aqui: o
/// mascaramento estático reescreve dados, e a política o recusa em produção.
/// </summary>
public interface IPostgresAnonymizationService
{
    Task<AnonymizationValidation> ValidateAsync(
        AnonymizationProfile profile,
        DatabaseConnectionSnapshot maskedConnection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// O canário, antes de qualquer dump: as mesmas linhas lidas pela conexão
    /// normal e pela mascarada precisam ser diferentes nas colunas com regra.
    /// Iguais = a máscara não está ativa para essa role.
    /// </summary>
    Task<bool> IsMaskingActiveAsync(
        AnonymizationProfile profile,
        DatabaseConnectionSnapshot source,
        DatabaseConnectionSnapshot maskedConnection,
        CancellationToken cancellationToken = default);

    /// <summary>O dump pela role mascarada, depois de a política aceitar o pedido inteiro.</summary>
    Task<PgToolRun> CreateAnonymousDumpAsync(
        DatabaseOperationRequest decision,
        PgDumpRequest dump,
        IProgress<PgToolEvent>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Depois do restore: as colunas com regra não podem ter chegado iguais à origem.</summary>
    Task<IReadOnlyList<CheckResult>> VerifyResultAsync(
        AnonymizationProfile profile,
        DatabaseConnectionSnapshot source,
        DatabaseConnectionSnapshot destination,
        CancellationToken cancellationToken = default);
}

public sealed partial class PostgresAnonymizationService(
    IPostgresAnonymizerInspector anonymizer,
    IPostgresServerInspector inspector,
    IPostgresDumpService dumps,
    IDatabaseSecurityPolicy policy) : IPostgresAnonymizationService
{
    /// <summary>Linhas por coluna no canário: o bastante para não ser sorte, pouco para não pesar em produção.</summary>
    internal const int CanaryRows = 200;

    internal const int CanaryColumns = 3;

    internal const int VerificationRows = 10_000;

    /// <summary>Teto de colunas conferidas depois do restore: cada uma é uma leitura nos dois servidores.</summary>
    internal const int VerificationColumns = 20;

    public async Task<AnonymizationValidation> ValidateAsync(
        AnonymizationProfile profile,
        DatabaseConnectionSnapshot maskedConnection,
        CancellationToken cancellationToken = default)
    {
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.InspectDatabase, maskedConnection));

        var status = await anonymizer.GetStatusAsync(maskedConnection, profile.PolicyName, cancellationToken);
        var columns = await inspector.ListColumnsAsync(maskedConnection, cancellationToken);
        return Compare(profile, status, columns);
    }

    public async Task<bool> IsMaskingActiveAsync(
        AnonymizationProfile profile,
        DatabaseConnectionSnapshot source,
        DatabaseConnectionSnapshot maskedConnection,
        CancellationToken cancellationToken = default)
    {
        var salt = NewSalt();
        var compared = 0;

        foreach (var rule in profile.Rules.Take(CanaryColumns * 3))
        {
            var column = new ColumnReference(rule.Schema, rule.Table, rule.Column);
            var real = await inspector.FingerprintAsync(source, column, salt, CanaryRows, cancellationToken);

            if (real.Values.Values.All(value => value is null))
            {
                continue;
            }

            var masked = await inspector.FingerprintAsync(maskedConnection, column, salt, CanaryRows, cancellationToken);

            if (!VerificationEvaluator.MaskingIsActive(real, masked))
            {
                return false;
            }

            if (++compared == CanaryColumns)
            {
                break;
            }
        }

        // Nenhuma coluna com valor para comparar: não há o que vazar nas amostras,
        // e a validação das regras no servidor continua valendo.
        return true;
    }

    public Task<PgToolRun> CreateAnonymousDumpAsync(
        DatabaseOperationRequest decision,
        PgDumpRequest dump,
        IProgress<PgToolEvent>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!dump.Anonymous || decision.DumpConnection is not { } masked || dump.Connection.Id != masked.Id)
        {
            throw new DomainException("O dump anônimo só sai pela conexão mascarada do perfil.");
        }

        policy.Demand(decision with { Operation = DatabaseOperationType.AnonymousDump, Destination = null });
        return dumps.DumpAsync(dump, progress, cancellationToken);
    }

    public async Task<IReadOnlyList<CheckResult>> VerifyResultAsync(
        AnonymizationProfile profile,
        DatabaseConnectionSnapshot source,
        DatabaseConnectionSnapshot destination,
        CancellationToken cancellationToken = default)
    {
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.Verify, source, destination));

        var salt = NewSalt();
        var results = new List<CheckResult>();

        foreach (var rule in profile.Rules.Take(VerificationColumns))
        {
            var column = new ColumnReference(rule.Schema, rule.Table, rule.Column);
            var before = await inspector.FingerprintAsync(source, column, salt, VerificationRows, cancellationToken);
            var after = await inspector.FingerprintAsync(destination, column, salt, VerificationRows, cancellationToken);
            results.Add(VerificationEvaluator.CompareSensitive(before, after));
        }

        if (results.Count == 0)
        {
            results.Add(new CheckResult(VerificationEvaluator.SensitiveData, "Regras", CheckOutcome.Warning, "O perfil não tem regras para conferir."));
        }

        return results;
    }

    /// <summary>O perfil contra o que o servidor tem em <c>pg_seclabels</c>.</summary>
    public static AnonymizationValidation Compare(
        AnonymizationProfile profile,
        AnonymizerStatus status,
        IReadOnlyList<ColumnInfo> columns)
    {
        var server = status.Rules
            .GroupBy(rule => rule.ColumnKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Label, StringComparer.Ordinal);
        var expected = profile.Rules.ToDictionary(rule => rule.ColumnKey, ExpectedLabel, StringComparer.Ordinal);

        var missing = expected.Keys.Where(key => !server.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
        var different = expected
            .Where(pair => server.TryGetValue(pair.Key, out var label) && Normalize(label) != Normalize(pair.Value))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        var extra = server.Keys.Where(key => !expected.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();

        var covered = server.Keys.Concat(expected.Keys).ToHashSet(StringComparer.Ordinal);
        var uncovered = SensitiveColumnClassifier.Suggest(columns)
            .Where(suggestion => !covered.Contains(suggestion.ColumnKey))
            .ToList();

        return new AnonymizationValidation(status, missing, different, extra, uncovered);
    }

    internal static string ExpectedLabel(AnonymizationRule rule) =>
        (rule.Kind == MaskingKind.Function ? "MASKED WITH FUNCTION " : "MASKED WITH VALUE ") + rule.Expression;

    /// <summary>Espaços e maiúsculas das palavras-chave não fazem duas regras diferentes.</summary>
    private static string Normalize(string label) => Whitespace().Replace(label.Trim(), " ").ToUpperInvariant();

    private static string NewSalt() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
