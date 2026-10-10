using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>A tela "PostgreSQL Environment": as ferramentas locais e o servidor, item por item.</summary>
public sealed record EnvironmentDiagnosticsReport(
    IReadOnlyList<CheckResult> ClientTools,
    IReadOnlyList<CheckResult> Server,
    PostgresInstallGuide Guide)
{
    public const string ClientToolsCategory = "Client Tools";
    public const string ServerCategory = "Server";

    public bool HasFailures => ClientTools.Concat(Server).Any(check => check.Outcome == CheckOutcome.Fail);
}

/// <summary>
/// O diagnóstico do ambiente PostgreSQL (ADR-056): o que está instalado aqui
/// e o que o servidor diz. Só leitura. Não há extensão a conferir: a cópia
/// mascara na consulta, sem nada instalado na origem (ADR-058).
/// </summary>
public interface IPostgresEnvironmentDiagnostics
{
    /// <summary>Só as ferramentas locais — não precisa de conexão nenhuma.</summary>
    Task<EnvironmentDiagnosticsReport> DiagnoseToolsAsync(bool refresh, CancellationToken cancellationToken = default);

    Task<EnvironmentDiagnosticsReport> DiagnoseAsync(
        DatabaseConnectionSnapshot connection,
        bool refresh,
        CancellationToken cancellationToken = default);
}

public sealed class PostgresEnvironmentDiagnostics(
    IPostgresToolLocator locator,
    IPostgresServerInspector inspector,
    IDatabaseSecurityPolicy policy,
    ILogger<PostgresEnvironmentDiagnostics> logger) : IPostgresEnvironmentDiagnostics
{
    public async Task<EnvironmentDiagnosticsReport> DiagnoseToolsAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        var tools = await locator.DetectAsync(refresh, cancellationToken);
        return new EnvironmentDiagnosticsReport(ToolChecks(tools), [], tools.Guide);
    }

    public async Task<EnvironmentDiagnosticsReport> DiagnoseAsync(
        DatabaseConnectionSnapshot connection,
        bool refresh,
        CancellationToken cancellationToken = default)
    {
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.Diagnose, connection));

        var server = await inspector.TestAsync(connection, cancellationToken: cancellationToken);

        // As ferramentas que uma cópia deste servidor usaria: o menor conjunto que o lê.
        var tools = (await locator.DetectAsync(refresh, cancellationToken)).ForSource(server.ServerVersion);
        var serverChecks = ServerChecks(server);

        if (server.Connected)
        {
            serverChecks.AddRange(PostgresCompatibility.Evaluate(tools, server.ServerVersion, null)
                .Where(check => check.Name == "pg_dump × origem")
                .Select(check => check with { Category = EnvironmentDiagnosticsReport.ServerCategory }));
        }

        logger.LogInformation("DatabaseDiagnosed {ConnectionId} {Connected}", connection.Id, server.Connected);
        return new EnvironmentDiagnosticsReport(ToolChecks(tools), serverChecks, tools.Guide);
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
