using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Mcp.Tools;

namespace MyTaskApp.Mcp.Tests;

/// <summary>
/// As ferramentas da Consulta Inteligente pelo MCP de verdade (ADR-081): só o
/// segredo de uma consulta aberta as enxerga, e ele não enxerga mais nada. O
/// PostgreSQL é falso — quem recusa escrita no servidor está provado nos
/// testes de integração do banco.
/// </summary>
public class ConsultationToolTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<McpTestHost> HostAsync(RecordingConsultationDatabase database) =>
        McpTestHost.CreateAsync(
            cancellationToken: Ct,
            configure: services => services.AddSingleton<IConsultationDatabase>(database));

    /// <summary>Uma tarefa com consulta num banco local, e a sessão aberta com o segredo devolvido.</summary>
    internal static async Task<(string Token, Guid SessionId)> OpenConsultationAsync(McpTestHost host, bool withSecret = true)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var task = TaskItem.Create("Pedido pendente", Now);
        await services.GetRequiredService<ITaskItemRepository>().AddAsync(task, Ct);

        var reader = DatabaseConnection.Create("Local (leitura)", "127.0.0.1", 5432, null, IConsultationReadAccessProvisioner.RoleName,
            DatabaseEnvironment.Development, DatabaseSslMode.Disable, null, ConnectionPermissions.FromFlags(ConnectionPermission.Read), Now);
        await services.GetRequiredService<IDatabaseConnectionRepository>().AddAsync(reader, Ct);

        var consultation = TaskConsultation.Create(task.Id, Now);
        consultation.SetDatabase(reader.Id, "loja_copia", Now);
        await services.GetRequiredService<ITaskConsultationRepository>().AddAsync(consultation, Ct);

        var (token, hash) = AgentHookToken.Create();
        var session = AgentSession.CreateConsultation(task.Id, "claude-code", @"C:\claude.exe", @"C:\Projetos\loja", Guid.NewGuid().ToString(), Now);

        if (withSecret)
        {
            session.GrantConsultationAccess(hash);
        }

        session.MarkRunning(4242, Now);
        await services.GetRequiredService<IAgentSessionRepository>().AddAsync(session, Ct);
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);

        return (token, session.Id);
    }

    private static async Task EndAsync(McpTestHost host, Guid sessionId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<IAgentSessionRepository>().FindByIdAsync(sessionId, Ct);
        session!.MarkExited(DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheServerToken_DoesNotSeeTheConsultationTools_NorCallThem()
    {
        await using var host = await HostAsync(new RecordingConsultationDatabase());
        await using var client = await host.ConnectAsync(cancellationToken: Ct);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        var error = await client.CallErrorAsync(ConsultationToolNames.Query, new() { ["query"] = "SELECT 1" }, Ct);

        tools.Select(tool => tool.Name).Should().NotIntersectWith(ConsultationToolNames.All).And.Contain("task_list");
        error.Should().Contain("só existe numa sessão de Consulta Inteligente");
    }

    [Fact]
    public async Task AConsultation_SeesOnlyItsFourTools_AndNoResourcesOrPrompts()
    {
        await using var host = await HostAsync(new RecordingConsultationDatabase());
        var (token, _) = await OpenConsultationAsync(host);
        await using var client = await host.ConnectAsync(token, Ct);

        (await client.ListToolsAsync(cancellationToken: Ct)).Select(tool => tool.Name).Should().BeEquivalentTo(ConsultationToolNames.All);
        (await client.ListResourcesAsync(cancellationToken: Ct)).Should().BeEmpty();
        (await client.ListPromptsAsync(cancellationToken: Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("task_list")]
    [InlineData("database_profile_list")]
    [InlineData("server_info")]
    public async Task AConsultation_CannotCallTheOtherTools(string tool)
    {
        await using var host = await HostAsync(new RecordingConsultationDatabase());
        var (token, _) = await OpenConsultationAsync(host);
        await using var client = await host.ConnectAsync(token, Ct);

        var error = await client.CallErrorAsync(tool, null, Ct);

        error.Should().Contain("só as ferramentas de consulta");
    }

    [Fact]
    public async Task AConsultation_CannotReadTheAppResources()
    {
        await using var host = await HostAsync(new RecordingConsultationDatabase());
        var (token, _) = await OpenConsultationAsync(host);
        await using var client = await host.ConnectAsync(token, Ct);

        var act = () => client.ReadResourceAsync("mytaskapp://databases/connections", cancellationToken: Ct).AsTask();

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task TheQuery_GoesToTheConsultationDatabase_WithTheCeiling()
    {
        var database = new RecordingConsultationDatabase();
        await using var host = await HostAsync(database);
        var (token, _) = await OpenConsultationAsync(host);
        await using var client = await host.ConnectAsync(token, Ct);

        var result = await client.CallJsonAsync(
            ConsultationToolNames.Query, new() { ["query"] = "SELECT situacao, count(*) FROM pedidos GROUP BY 1", ["limit"] = 9999 }, Ct);

        result.GetProperty("rows")[0][1].GetInt64().Should().Be(42);
        var (connection, query, limits) = database.Queries.Should().ContainSingle().Subject;
        connection.Database.Should().Be("loja_copia");
        connection.Username.Should().Be(IConsultationReadAccessProvisioner.RoleName);
        query.Should().Be("SELECT situacao, count(*) FROM pedidos GROUP BY 1");
        limits.MaxRows.Should().Be(ConsultationLimits.RowCeiling);
        host.Logs.All.Should().NotContain("FROM pedidos");
    }

    [Fact]
    public async Task TheDescription_SaysWhatCanBeRead_WithoutTheConnection()
    {
        await using var host = await HostAsync(new RecordingConsultationDatabase());
        var (token, _) = await OpenConsultationAsync(host);
        await using var client = await host.ConnectAsync(token, Ct);

        var description = await client.CallJsonAsync(ConsultationToolNames.DescribeDatabase, null, Ct);

        description.GetProperty("database").GetString().Should().Be("loja_copia");
        description.GetProperty("engine").GetString().Should().Be("PostgreSQL");
        description.GetRawText().Should().NotContain("127.0.0.1").And.NotContain(IConsultationReadAccessProvisioner.RoleName);
    }

    [Fact]
    public async Task ADatabaseError_ComesBackAsTheMessage()
    {
        var database = new RecordingConsultationDatabase { Failure = new DomainException("A consulta tentou alterar dados; aqui só leitura é permitida.") };
        await using var host = await HostAsync(database);
        var (token, _) = await OpenConsultationAsync(host);
        await using var client = await host.ConnectAsync(token, Ct);

        var error = await client.CallErrorAsync(ConsultationToolNames.Query, new() { ["query"] = "DELETE FROM pedidos" }, Ct);

        error.Should().Contain("só leitura é permitida");
    }

    [Fact]
    public async Task AnEndedConsultation_IsOutside()
    {
        await using var host = await HostAsync(new RecordingConsultationDatabase());
        var (token, sessionId) = await OpenConsultationAsync(host);
        await EndAsync(host, sessionId);

        var act = () => host.ConnectAsync(token, Ct);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task AnUnknownSecret_IsOutside()
    {
        await using var host = await HostAsync(new RecordingConsultationDatabase());

        var act = () => host.ConnectAsync(Convert.ToHexString(new byte[32]), Ct);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public void NoConsultationTool_LetsTheCallerChooseTheConnection()
    {
        typeof(ConsultationTools).GetMethods()
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.Name!)
            .Should().NotContain(name =>
                name.Contains("connection", StringComparison.OrdinalIgnoreCase)
                || name.Contains("database", StringComparison.OrdinalIgnoreCase)
                || name.Contains("host", StringComparison.OrdinalIgnoreCase)
                || name.Contains("session", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>O banco da consulta sem PostgreSQL: só leitura, e anota o que pediram.</summary>
internal sealed class RecordingConsultationDatabase : IConsultationDatabase
{
    public List<(DatabaseConnectionSnapshot Connection, string Query, ConsultationLimits Limits)> Queries { get; } = [];

    public Exception? Failure { get; init; }

    public Task<ReadAccessCheck> CheckReadAccessAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReadAccessCheck(connection.Username, "14.11", []));

    public Task<ConsultationDatabaseInfo> DescribeAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsultationDatabaseInfo(connection.Database!, "14.11", ["public"]));

    public Task<IReadOnlyList<ConsultationTable>> ListTablesAsync(
        DatabaseConnectionSnapshot connection,
        string? schema,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConsultationTable>>([new ConsultationTable("public", "pedidos", "table", 50, null)]);

    public Task<ConsultationTableDetail> DescribeTableAsync(
        DatabaseConnectionSnapshot connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsultationTableDetail(schema, table, null, [], []));

    public Task<ConsultationQueryResult> QueryAsync(
        DatabaseConnectionSnapshot connection,
        string query,
        ConsultationLimits limits,
        CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException<ConsultationQueryResult>(Failure);
        }

        Queries.Add((connection, query, limits));

        return Task.FromResult(new ConsultationQueryResult(
            [new ConsultationResultColumn("situacao", "text"), new ConsultationResultColumn("count", "int8")],
            [["pendente", 42L]],
            false,
            4));
    }
}
