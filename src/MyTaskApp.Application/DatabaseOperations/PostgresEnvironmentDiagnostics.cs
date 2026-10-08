using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>A tela "PostgreSQL Environment": ferramentas locais, servidor e Anonymizer, item por item.</summary>
public sealed record EnvironmentDiagnosticsReport(
    IReadOnlyList<CheckResult> ClientTools,
    IReadOnlyList<CheckResult> Server,
    IReadOnlyList<CheckResult> Anonymizer,
    PostgresInstallGuide Guide)
{
    public const string ClientToolsCategory = "Client Tools";
    public const string ServerCategory = "Server";
    public const string AnonymizerCategory = "PostgreSQL Anonymizer";

    public bool HasFailures => ClientTools.Concat(Server).Concat(Anonymizer).Any(check => check.Outcome == CheckOutcome.Fail);
}

/// <summary>
/// O diagnóstico do ambiente PostgreSQL (ADR-056): o que está instalado
/// aqui, o que o servidor diz e como está o Anonymizer. Só leitura.
/// </summary>
public interface IPostgresEnvironmentDiagnostics
{
    /// <summary>Só as ferramentas locais — não precisa de conexão nenhuma.</summary>
    Task<EnvironmentDiagnosticsReport> DiagnoseToolsAsync(bool refresh, CancellationToken cancellationToken = default);

    Task<EnvironmentDiagnosticsReport> DiagnoseAsync(
        DatabaseConnectionSnapshot connection,
        string policyName,
        bool refresh,
        CancellationToken cancellationToken = default);
}

public sealed class PostgresEnvironmentDiagnostics(
    IPostgresToolLocator locator,
    IPostgresServerInspector inspector,
    IPostgresAnonymizerInspector anonymizer,
    IDatabaseSecurityPolicy policy,
    ILogger<PostgresEnvironmentDiagnostics> logger) : IPostgresEnvironmentDiagnostics
{
    public async Task<EnvironmentDiagnosticsReport> DiagnoseToolsAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        var tools = await locator.DetectAsync(refresh, cancellationToken);
        return new EnvironmentDiagnosticsReport(ToolChecks(tools), [], [], tools.Guide);
    }

    public async Task<EnvironmentDiagnosticsReport> DiagnoseAsync(
        DatabaseConnectionSnapshot connection,
        string policyName,
        bool refresh,
        CancellationToken cancellationToken = default)
    {
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.Diagnose, connection));

        var tools = await locator.DetectAsync(refresh, cancellationToken);
        var server = await inspector.TestAsync(connection, cancellationToken: cancellationToken);
        var serverChecks = ServerChecks(server);

        if (!server.Connected)
        {
            return new EnvironmentDiagnosticsReport(
                ToolChecks(tools),
                serverChecks,
                [new CheckResult(EnvironmentDiagnosticsReport.AnonymizerCategory, "Extension", CheckOutcome.Warning, "Sem conexão com o servidor.")],
                tools.Guide);
        }

        serverChecks.AddRange(PostgresCompatibility.Evaluate(tools, server.ServerVersion, null, anonymous: false)
            .Where(check => check.Name == "pg_dump × origem")
            .Select(check => check with { Category = EnvironmentDiagnosticsReport.ServerCategory }));

        IReadOnlyList<CheckResult> anonymizerChecks;

        try
        {
            var status = await anonymizer.GetStatusAsync(connection, policyName, cancellationToken);
            var columns = await inspector.ListColumnsAsync(connection, cancellationToken);
            anonymizerChecks = AnonymizerChecks(status, columns);
        }
        catch (DomainException exception)
        {
            anonymizerChecks = [new CheckResult(EnvironmentDiagnosticsReport.AnonymizerCategory, "Extension", CheckOutcome.Fail, exception.Message)];
        }

        logger.LogInformation("DatabaseDiagnosed {ConnectionId} {Connected}", connection.Id, server.Connected);
        return new EnvironmentDiagnosticsReport(ToolChecks(tools), serverChecks, anonymizerChecks, tools.Guide);
    }

    internal static IReadOnlyList<CheckResult> ToolChecks(PostgresClientTools tools) =>
        tools.Tools
            .Select(tool => tool.Found
                ? new CheckResult(
                    EnvironmentDiagnosticsReport.ClientToolsCategory,
                    tool.Name,
                    tool.Version is null ? CheckOutcome.Warning : CheckOutcome.Pass,
                    tool.Version is null ? $"Versão ilegível em {tool.Path}" : $"{tool.Version} — {tool.Path}")
                : new CheckResult(EnvironmentDiagnosticsReport.ClientToolsCategory, tool.Name, CheckOutcome.Fail, "Não encontrado."))
            .ToList();

    internal static List<CheckResult> ServerChecks(ServerDiagnostics server)
    {
        const string category = EnvironmentDiagnosticsReport.ServerCategory;

        if (!server.Connected)
        {
            return [new CheckResult(category, "Connection", CheckOutcome.Fail, server.Error)];
        }

        var size = server.DatabaseSizeBytes is { } bytes ? $" — {FormatBytes(bytes)}" : string.Empty;
        var privileges = server.Privileges;

        return
        [
            new CheckResult(category, "Connection", CheckOutcome.Pass, $"{server.CurrentUser}@{server.Database}{size}"),
            new CheckResult(category, "PostgreSQL", CheckOutcome.Pass, server.ServerVersion?.ToString() ?? server.ServerVersionText),
            privileges.CanReadAllData || privileges.TablesWithoutSelect == 0
                ? new CheckResult(category, "Permissions", CheckOutcome.Pass, privileges.IsSuperuser ? "Superusuário." : "Leitura em todas as tabelas.")
                : new CheckResult(category, "Permissions", CheckOutcome.Warning,
                    $"{privileges.TablesWithoutSelect} tabela(s) sem SELECT para este usuário: o dump falharia nelas."),
            new CheckResult(category, "Schemas", CheckOutcome.Pass, $"{server.Schemas.Count}: {string.Join(", ", server.Schemas.Take(8))}"),
            new CheckResult(category, "Tables", CheckOutcome.Pass, server.TableCount.ToString(CultureInfo.InvariantCulture)),
        ];
    }

    internal static IReadOnlyList<CheckResult> AnonymizerChecks(AnonymizerStatus status, IReadOnlyList<ColumnInfo> columns)
    {
        const string category = EnvironmentDiagnosticsReport.AnonymizerCategory;

        if (!status.Available && !status.Installed)
        {
            return [new CheckResult(category, "Extension available", CheckOutcome.Fail, "A extensão anon não está disponível neste servidor.")];
        }

        var checks = new List<CheckResult>
        {
            new(category, "Extension available", CheckOutcome.Pass, status.AvailableVersion),
        };

        if (!status.Installed)
        {
            checks.Add(new(category, "Extension installed", CheckOutcome.Fail, "Disponível, mas sem CREATE EXTENSION anon neste banco."));
            return checks;
        }

        checks.Add(status.IsSupportedVersion
            ? new(category, "Extension installed", CheckOutcome.Pass, status.InstalledVersion)
            : new(category, "Extension installed", CheckOutcome.Fail,
                $"Versão {status.InstalledVersion}: o dump anônimo exige o Anonymizer {PostgresCompatibility.MinimumAnonymizerMajor}.x."));

        checks.Add(status.TransparentMaskingOn
            ? new(category, "Extension enabled", CheckOutcome.Pass, "anon.transparent_dynamic_masking = on")
            : new(category, "Extension enabled", CheckOutcome.Warning, "anon.transparent_dynamic_masking está desligado neste banco."));

        checks.Add(status.CurrentRoleMasked
            ? new(category, "Masked role", CheckOutcome.Pass, "O usuário desta conexão é MASKED.")
            : new(category, "Masked role", CheckOutcome.Warning, "O usuário desta conexão não é MASKED: use outra conexão, mascarada, no perfil."));

        checks.Add(status.CanExecuteFunctions
            ? new(category, "Functions", CheckOutcome.Pass, "Funções do anon executáveis.")
            : new(category, "Functions", CheckOutcome.Warning, "Sem permissão para executar as funções do anon."));

        checks.Add(status.Rules.Count > 0
            ? new(category, "Masking rules detected", CheckOutcome.Pass, $"{status.Rules.Count} coluna(s) com regra.")
            : new(category, "Masking rules detected", CheckOutcome.Warning, "Nenhuma coluna com regra de mascaramento."));

        var covered = status.Rules.Select(rule => rule.ColumnKey).ToHashSet(StringComparer.Ordinal);
        var uncovered = SensitiveColumnClassifier.Suggest(columns)
            .Where(suggestion => suggestion.Sensitivity >= ColumnSensitivity.Medium && !covered.Contains(suggestion.ColumnKey))
            .ToList();

        checks.Add(uncovered.Count == 0
            ? new(category, "Columns without masking policy", CheckOutcome.Pass, "Nenhuma coluna candidata sem regra.")
            : new(category, "Columns without masking policy", CheckOutcome.Warning,
                $"{uncovered.Count} coluna(s) candidata(s) sem regra: {string.Join(", ", uncovered.Take(5).Select(item => item.ColumnKey))}"));

        return checks;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{value:0.#} {units[unit]}");
    }
}
