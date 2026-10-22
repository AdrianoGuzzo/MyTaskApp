using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Consultations;

/// <summary>Quem está chamando as ferramentas de consulta: a sessão e a tarefa dela.</summary>
public sealed record ConsultationAccess(Guid SessionId, Guid TaskId);

/// <summary>O segredo que veio no <c>Authorization</c> é de uma consulta aberta?</summary>
public sealed record ResolveConsultationAccess(string Token);

/// <summary>
/// Pelo hash, como o segredo dos avisos (ADR-037): o banco não guarda o
/// segredo, e uma sessão encerrada não entra mais — o segredo morre com o
/// processo.
/// </summary>
public sealed class ResolveConsultationAccessHandler(IAgentSessionRepository sessions)
{
    public async Task<ConsultationAccess?> HandleAsync(ResolveConsultationAccess query, CancellationToken cancellationToken = default)
    {
        // O segredo é sempre 64 hexadecimais: outra coisa nem vai ao banco.
        if (query.Token is not { Length: 64 } token || !token.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        var session = await sessions.FindActiveConsultationByTokenHashAsync(AgentHookToken.Hash(token), cancellationToken);

        return session is null ? null : new ConsultationAccess(session.Id, session.TaskItemId);
    }
}

/// <summary>O banco de uma consulta, como a ferramenta o descreve: sem conexão, sem usuário, sem senha.</summary>
public sealed record ConsultationDatabaseDescription(
    string Label,
    string Database,
    string Engine,
    string ServerVersion,
    DatabaseEnvironment Environment,
    IReadOnlyList<string> Schemas,
    int DefaultRows,
    int MaxRows,
    int TimeoutSeconds,
    int MaxCellLength);

/// <summary>
/// O banco que a sessão pode ler (ADR-081). A IA não escolhe conexão nem
/// banco: vêm da consulta da tarefa da sessão. A cada chamada, de novo: a
/// sessão está ativa? O banco ainda é permitido? O usuário ainda só lê?
/// </summary>
public sealed class ConsultationTargetResolver(
    IAgentSessionRepository sessions,
    ConsultationResolver resolver,
    ConsultationReadAccess readAccess,
    IDatabaseSecurityPolicy policy)
{
    public async Task<(DatabaseConnectionSnapshot Reader, ConsultationState State)> ResolveAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await sessions.FindByIdAsync(sessionId, cancellationToken);

        if (session is not { IsConsultation: true, IsActive: true })
        {
            throw new DomainException("Esta Consulta Inteligente já foi encerrada.");
        }

        if (session.ConsultationTokenHash is null)
        {
            throw new DomainException("Esta consulta foi aberta sem banco de dados: responda só com o código-fonte.");
        }

        var state = await resolver.ResolveAsync(session.TaskItemId, cancellationToken);

        var chosen = state.Connection
            ?? throw new DomainException(
                state.Database?.Problem ?? "Esta consulta não tem banco de dados: responda só com o código-fonte.");

        var reader = await readAccess.ReaderForAsync(chosen, cancellationToken);

        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.QueryData, Source: chosen, ProtectedEndpoints: state.ProtectedEndpoints));
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.QueryData, Source: reader, ProtectedEndpoints: state.ProtectedEndpoints));

        var check = await readAccess.CheckAsync(reader, cancellationToken);

        if (!check.IsReadOnly)
        {
            throw new DomainException(
                "O usuário do banco desta consulta pode alterar dados, e a consulta foi recusada. "
                + "Peça ao usuário para preparar o acesso de leitura na aba Consulta Inteligente.");
        }

        return (reader, state);
    }
}

public sealed record DescribeConsultationDatabase(Guid SessionId);

public sealed class DescribeConsultationDatabaseHandler(ConsultationTargetResolver targets, IConsultationDatabase database)
{
    public async Task<ConsultationDatabaseDescription> HandleAsync(
        DescribeConsultationDatabase query,
        CancellationToken cancellationToken = default)
    {
        var (reader, state) = await targets.ResolveAsync(query.SessionId, cancellationToken);
        var info = await database.DescribeAsync(reader, cancellationToken);
        var limits = ConsultationLimits.Default;

        return new ConsultationDatabaseDescription(
            state.Database!.Label,
            info.Database,
            "PostgreSQL",
            info.ServerVersion,
            reader.Environment,
            info.Schemas,
            limits.MaxRows,
            ConsultationLimits.RowCeiling,
            limits.TimeoutSeconds,
            limits.MaxCellLength);
    }
}

public sealed record ListConsultationTables(Guid SessionId, string? Schema);

public sealed class ListConsultationTablesHandler(ConsultationTargetResolver targets, IConsultationDatabase database)
{
    public async Task<IReadOnlyList<ConsultationTable>> HandleAsync(
        ListConsultationTables query,
        CancellationToken cancellationToken = default)
    {
        var (reader, _) = await targets.ResolveAsync(query.SessionId, cancellationToken);
        return await database.ListTablesAsync(reader, Blank(query.Schema), cancellationToken);
    }

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record DescribeConsultationTable(Guid SessionId, string? Schema, string Table);

public sealed class DescribeConsultationTableHandler(ConsultationTargetResolver targets, IConsultationDatabase database)
{
    public async Task<ConsultationTableDetail> HandleAsync(
        DescribeConsultationTable query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Table))
        {
            throw new DomainException("Informe a tabela.");
        }

        var (reader, _) = await targets.ResolveAsync(query.SessionId, cancellationToken);

        return await database.DescribeTableAsync(
            reader, ListConsultationTablesHandler.Blank(query.Schema) ?? "public", query.Table.Trim(), cancellationToken);
    }
}

/// <param name="Limit">Quantas linhas, no máximo; <c>null</c> = o padrão. Preso ao teto.</param>
public sealed record QueryConsultationData(Guid SessionId, string Query, int? Limit);

/// <summary>
/// Uma consulta de leitura no banco da sessão. Quem recusa escrita é o
/// servidor (<see cref="IConsultationDatabase.QueryAsync"/>); aqui entram a
/// sessão, a política e os limites. O log leva só números — nunca a consulta
/// nem os valores.
/// </summary>
public sealed class QueryConsultationDataHandler(
    ConsultationTargetResolver targets,
    IConsultationDatabase database,
    ILogger<QueryConsultationDataHandler> logger)
{
    public async Task<ConsultationQueryResult> HandleAsync(
        QueryConsultationData query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            throw new DomainException("Escreva a consulta (um SELECT).");
        }

        var (reader, state) = await targets.ResolveAsync(query.SessionId, cancellationToken);
        var result = await database.QueryAsync(reader, query.Query, ConsultationLimits.Default.WithRows(query.Limit), cancellationToken);

        logger.LogInformation(
            "ConsultationQueryAnswered {TaskId} {SessionId} {Rows} {Truncated} {ElapsedMs}",
            state.Task.Id,
            query.SessionId,
            result.Rows.Count,
            result.Truncated,
            result.ElapsedMilliseconds);

        return result;
    }
}
