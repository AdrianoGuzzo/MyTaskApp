using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>A consulta de cada tarefa, em memória (ADR-081).</summary>
internal sealed class FakeTaskConsultationRepository : ITaskConsultationRepository
{
    public Dictionary<Guid, TaskConsultation> Items { get; } = [];

    public Task<TaskConsultation?> FindAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.GetValueOrDefault(taskId));

    public Task AddAsync(TaskConsultation consultation, CancellationToken cancellationToken = default)
    {
        Items[consultation.TaskItemId] = consultation;
        return Task.CompletedTask;
    }

    public void Remove(TaskConsultation consultation) => Items.Remove(consultation.TaskItemId);
}

/// <summary>
/// O banco da consulta sem PostgreSQL: o catálogo responde o que o teste
/// mandou, e cada chamada fica anotada com a conexão que a fez.
/// </summary>
internal sealed class FakeConsultationDatabase : IConsultationDatabase
{
    public List<string> Problems { get; } = [];

    public Exception? Failure { get; set; }

    public List<(string Call, DatabaseConnectionSnapshot Connection)> Calls { get; } = [];

    public List<(string Query, ConsultationLimits Limits)> Queries { get; } = [];

    public Task<ReadAccessCheck> CheckReadAccessAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        Calls.Add(("check", connection));

        return Failure is not null
            ? Task.FromException<ReadAccessCheck>(Failure)
            : Task.FromResult(new ReadAccessCheck(connection.Username, "14.11", [.. Problems]));
    }

    public Task<ConsultationDatabaseInfo> DescribeAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        Calls.Add(("describe", connection));
        return Task.FromResult(new ConsultationDatabaseInfo(connection.Database!, "14.11", ["public"]));
    }

    public Task<IReadOnlyList<ConsultationTable>> ListTablesAsync(
        DatabaseConnectionSnapshot connection,
        string? schema,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(("tables", connection));
        return Task.FromResult<IReadOnlyList<ConsultationTable>>([new ConsultationTable("public", "clientes", "table", 300, null)]);
    }

    public Task<ConsultationTableDetail> DescribeTableAsync(
        DatabaseConnectionSnapshot connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(("table", connection));
        return Task.FromResult(new ConsultationTableDetail(schema, table, null, [], []));
    }

    public Task<ConsultationQueryResult> QueryAsync(
        DatabaseConnectionSnapshot connection,
        string query,
        ConsultationLimits limits,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(("query", connection));
        Queries.Add((query, limits));

        return Task.FromResult(new ConsultationQueryResult(
            [new ConsultationResultColumn("total", "int8")],
            [[42L]],
            false,
            3));
    }
}

/// <summary>"Preparar acesso de leitura" sem servidor: anota pela conexão de quem foi pedido.</summary>
internal sealed class FakeReadAccessProvisioner(DatabaseSecurityPolicy policy) : IConsultationReadAccessProvisioner
{
    public List<(DatabaseConnectionSnapshot Admin, string? Password)> Calls { get; } = [];

    public Exception? Failure { get; set; }

    public bool Exists { get; set; }

    public Task<bool> PrepareAsync(DatabaseConnectionSnapshot admin, SecretText? password, CancellationToken cancellationToken = default)
    {
        // Como o de verdade: a política de novo, antes de tocar no servidor.
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.PrepareReadAccess, Destination: admin));

        if (Failure is not null)
        {
            return Task.FromException<bool>(Failure);
        }

        Calls.Add((admin, password?.Reveal()));

        var created = !Exists;
        Exists = true;
        return Task.FromResult(created);
    }
}

/// <summary>O servidor MCP, ligado ou não.</summary>
internal sealed class FakeConsultationToolsEndpoint : IConsultationToolsEndpoint
{
    public static readonly Uri Listening = new("http://127.0.0.1:5197/mcp");

    public Uri? Address { get; set; } = Listening;
}

/// <summary>Uma falha do servidor de banco, como o adaptador a entrega: em pt-BR, sem senha.</summary>
internal sealed class FakeDatabaseFailure(string message) : DomainException(message);
